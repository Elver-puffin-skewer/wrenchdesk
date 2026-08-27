using WrenchDesk.Data;

namespace WrenchDesk.Services.Google;

/// <summary>What one sync run did, for the settings screen and the log.</summary>
public record SyncReport
{
    public int Pushed { get; set; }
    public int Updated { get; set; }
    public int DeletedInGoogle { get; set; }
    public int Imported { get; set; }
    public int PulledUpdates { get; set; }
    public int DeletedLocally { get; set; }
    public int CustomersMatched { get; set; }
    public int Conflicts { get; set; }
    public string? Error { get; set; }
    public bool NeedsReconnect { get; set; }

    public bool Success => Error is null;

    public int TotalChanges => Pushed + Updated + DeletedInGoogle + Imported + PulledUpdates + DeletedLocally;

    public string Describe()
    {
        if (Error is not null) return $"Failed: {Error}";
        if (TotalChanges == 0) return "Already up to date";

        var parts = new List<string>();
        if (Pushed > 0) parts.Add($"{Pushed} added to Google");
        if (Updated > 0) parts.Add($"{Updated} updated in Google");
        if (DeletedInGoogle > 0) parts.Add($"{DeletedInGoogle} removed from Google");
        if (Imported > 0) parts.Add($"{Imported} brought in from Google");
        if (PulledUpdates > 0) parts.Add($"{PulledUpdates} updated from Google");
        if (DeletedLocally > 0) parts.Add($"{DeletedLocally} cancelled in Google");
        if (CustomersMatched > 0) parts.Add($"{CustomersMatched} matched to a customer");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Two-way sync between the shop's schedule and one Google calendar.
///
/// It syncs a single dedicated calendar rather than the account's main one. That is what makes
/// importing safe: everything on that calendar is shop work, so a stop added on someone's phone
/// becomes an appointment here, without dragging in dentist appointments and birthdays.
///
/// Google has no way to call a shop PC on a home network, so this polls rather than being pushed to.
/// </summary>
public class CalendarSyncService
{
    private readonly ScheduleRepo _schedule;
    private readonly CustomerRepo _customers;
    private readonly SettingsStore _settings;
    private readonly ILogger<CalendarSyncService> _log;

    /// <summary>
    /// How far back a first-time or forced read goes. Wide enough to pick up a season's work,
    /// bounded so connecting a years-old calendar does not import all of it.
    /// </summary>
    private static readonly TimeSpan InitialWindow = TimeSpan.FromDays(90);

    public CalendarSyncService(ScheduleRepo schedule, CustomerRepo customers, SettingsStore settings,
        ILogger<CalendarSyncService> log)
    {
        _schedule = schedule;
        _customers = customers;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Forgets the incremental position so the next sync reads the whole window again.
    ///
    /// Needed after a change to what gets imported: Google only reports what has altered since the
    /// last token, so entries that were previously passed over would never be offered again.
    /// </summary>
    public void ForceFullResync() => _settings.Set(SettingsStore.GoogleSyncToken, "");

    public async Task<SyncReport> SyncAsync(ICalendarApi api, string calendarId, CancellationToken ct = default)
    {
        var report = new SyncReport();

        try
        {
            // Push first. Doing it in this order means a stop written here a moment ago is on the
            // calendar before we ask Google what changed, so it cannot come back as a stranger.
            await PushAsync(api, calendarId, report, ct);
            await PullAsync(api, calendarId, report, ct);

            _settings.SetAll(new Dictionary<string, string>
            {
                [SettingsStore.GoogleLastSync] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                [SettingsStore.GoogleLastResult] = report.Describe(),
                [SettingsStore.GoogleLastError] = "",
                [SettingsStore.GoogleNeedsReconnect] = "false"
            });
        }
        catch (CalendarAuthException ex)
        {
            _log.LogError(ex, "Google Calendar authorisation lost");
            report.Error = ex.Message;
            report.NeedsReconnect = true;

            _settings.SetAll(new Dictionary<string, string>
            {
                [SettingsStore.GoogleLastError] = $"{DateTime.Now:yyyy-MM-dd HH:mm} — {ex.Message}",
                [SettingsStore.GoogleNeedsReconnect] = "true"
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Google Calendar sync failed");
            report.Error = ex.Message;
            _settings.Set(SettingsStore.GoogleLastError, $"{DateTime.Now:yyyy-MM-dd HH:mm} — {ex.Message}");
        }

        return report;
    }

    // ---- Local changes out to Google ----

    private async Task PushAsync(ICalendarApi api, string calendarId, SyncReport report, CancellationToken ct)
    {
        // Appointments deleted here still have an event sitting on the calendar.
        foreach (var (eventId, _) in _schedule.Tombstones())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await api.DeleteEventAsync(calendarId, eventId, ct);
                report.DeletedInGoogle++;
            }
            catch (CalendarAuthException) { throw; }
            catch (Exception ex)
            {
                // Already gone is a success as far as the shop is concerned.
                _log.LogWarning(ex, "Could not delete Google event {EventId}; dropping the tombstone", eventId);
            }

            _schedule.ClearTombstone(eventId);
        }

        foreach (var appointment in _schedule.NeedingPush())
        {
            ct.ThrowIfCancellationRequested();

            var row = _schedule.GetRow(appointment.Id);
            if (row is null) continue;

            var data = AppointmentEventMapper.ToEvent(row);

            if (string.IsNullOrWhiteSpace(appointment.GoogleEventId))
            {
                var created = await api.CreateEventAsync(calendarId, data, ct);
                _schedule.MarkSynced(appointment.Id, created.Id, created.Updated);
                report.Pushed++;
                continue;
            }

            // Both sides moved since the last sync — let the newer edit stand.
            if (!string.IsNullOrWhiteSpace(appointment.GoogleUpdated) &&
                AppointmentEventMapper.GoogleWins(appointment.UpdatedUtc, appointment.GoogleUpdated))
            {
                report.Conflicts++;
            }

            var updated = await api.UpdateEventAsync(calendarId, appointment.GoogleEventId, data, ct);

            if (updated is null)
            {
                // Someone deleted it in Google while it still existed here. Recreate it, since the
                // shop record is the one with a customer and a ticket attached.
                var recreated = await api.CreateEventAsync(calendarId, data, ct);
                _schedule.MarkSynced(appointment.Id, recreated.Id, recreated.Updated);
                report.Pushed++;
            }
            else
            {
                _schedule.MarkSynced(appointment.Id, updated.Id, updated.Updated);
                report.Updated++;
            }
        }
    }

    // ---- Google changes back into the shop ----

    private async Task PullAsync(ICalendarApi api, string calendarId, SyncReport report, CancellationToken ct)
    {
        var syncToken = _settings.Get(SettingsStore.GoogleSyncToken);
        if (string.IsNullOrWhiteSpace(syncToken)) syncToken = null;

        string? pageToken = null;
        string? nextSyncToken = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            CalendarChangePage page;
            try
            {
                page = await api.ListChangesAsync(
                    calendarId, syncToken, pageToken,
                    syncToken is null ? DateTime.Now - InitialWindow : null, ct);
            }
            catch (SyncTokenExpiredException)
            {
                // Normal after a long gap. Forget the token and read the window afresh.
                _log.LogInformation("Google sync token expired; falling back to a full read");
                _settings.Set(SettingsStore.GoogleSyncToken, "");
                syncToken = null;
                pageToken = null;
                continue;
            }

            foreach (var ev in page.Events)
            {
                ct.ThrowIfCancellationRequested();
                ApplyIncoming(ev, report);
            }

            if (page.NextSyncToken is not null) nextSyncToken = page.NextSyncToken;
            if (page.NextPageToken is null) break;
            pageToken = page.NextPageToken;
        }

        if (nextSyncToken is not null)
            _settings.Set(SettingsStore.GoogleSyncToken, nextSyncToken);
    }

    private void ApplyIncoming(CalendarEventData ev, SyncReport report)
    {
        var existing = _schedule.GetByGoogleEventId(ev.Id);

        if (ev.IsCancelled)
        {
            if (existing is null) return;

            // Deleted in Google — take it off the shop's board too. No tombstone: Google already knows.
            _schedule.DeleteFromGoogle(existing.Id);
            report.DeletedLocally++;
            return;
        }

        if (existing is null)
        {
            // Shops write plenty of work as an all-day entry against a date, so those come in too
            // — they are jobs, not notes. Skipping them was the reason most of a shop calendar
            // never arrived.
            var matched = _customers.MatchFromCalendarText($"{ev.Summary} {ev.Description}");

            var appointment = AppointmentEventMapper.ToNewAppointment(ev, matched);
            _schedule.InsertFromGoogle(appointment, ev.Updated);

            report.Imported++;
            if (matched is not null) report.CustomersMatched++;
            return;
        }

        // Nothing new — this is the echo of our own push coming back.
        if (existing.GoogleUpdated == ev.Updated) return;

        // Both sides edited since the last sync. The push above already sent the local version,
        // so keeping it means simply not applying Google's older copy.
        if (existing.HasLocalChanges && !AppointmentEventMapper.GoogleWins(existing.UpdatedUtc, ev.Updated))
        {
            report.Conflicts++;
            return;
        }

        if (existing.CustomerId is null && !string.IsNullOrWhiteSpace(existing.Title))
        {
            // The customer may have been added to the books after the entry first came across.
            var matched = _customers.MatchFromCalendarText($"{ev.Summary} {ev.Description}");
            if (matched is not null)
            {
                existing.CustomerId = matched.Id;
                report.CustomersMatched++;
            }
        }

        if (AppointmentEventMapper.ApplyToExisting(existing, ev))
        {
            _schedule.ApplyFromGoogle(existing, ev.Updated);
            report.PulledUpdates++;
        }
        else
        {
            // Same values, newer stamp — just record that we have seen it.
            _schedule.MarkSynced(existing.Id, ev.Id, ev.Updated);
        }
    }
}
