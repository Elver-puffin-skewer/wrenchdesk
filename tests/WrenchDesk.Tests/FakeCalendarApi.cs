using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

/// <summary>
/// An in-memory stand-in for Google Calendar. It models the parts of Google's behaviour the sync
/// actually depends on — server-assigned ids, an "updated" stamp that moves on every write, deleted
/// events coming back as cancelled, and sync tokens that can expire — so the merge rules can be
/// proven without a network or an account.
/// </summary>
public class FakeCalendarApi : ICalendarApi
{
    private readonly Dictionary<string, CalendarEventData> _events = new();
    private readonly List<CalendarSummary> _calendars = new()
    {
        new CalendarSummary("primary", "Personal", true, true),
        new CalendarSummary("shop-cal", "Walt's Small Engines — Schedule", false, true)
    };

    private int _idSeed;
    private int _clock;

    /// <summary>When set, the next ListChangesAsync with a sync token rejects it once.</summary>
    public bool ExpireNextSyncToken { get; set; }

    /// <summary>When set, every call fails as though the authorisation had been revoked.</summary>
    public bool ThrowAuthError { get; set; }

    public int CreateCount { get; private set; }
    public int UpdateCount { get; private set; }
    public int DeleteCount { get; private set; }

    public IReadOnlyCollection<CalendarEventData> Events => _events.Values;

    /// <summary>Every write moves the clock, mirroring Google stamping its own "updated" value.</summary>
    private string Tick() => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_clock).ToString("O");

    public CalendarEventData SeedForeignEvent(string summary, DateTime start, int minutes = 60,
        string location = "", string description = "", bool allDay = false)
    {
        var ev = new CalendarEventData
        {
            Id = $"foreign-{++_idSeed}",
            Summary = summary,
            Description = description,
            Location = location,
            Start = start,
            End = start.AddMinutes(minutes),
            IsAllDay = allDay,
            Updated = Tick()
        };

        _events[ev.Id] = ev;
        return ev;
    }

    /// <summary>Simulates somebody dragging the event to a new time in the Google UI.</summary>
    public void MoveEvent(string eventId, DateTime newStart, int minutes = 60)
    {
        var ev = _events[eventId];
        _events[eventId] = ev with
        {
            Start = newStart,
            End = newStart.AddMinutes(minutes),
            Updated = Tick()
        };
    }

    public void EditLocation(string eventId, string location)
    {
        var ev = _events[eventId];
        _events[eventId] = ev with { Location = location, Updated = Tick() };
    }

    public void RenameEvent(string eventId, string summary)
    {
        var ev = _events[eventId];
        _events[eventId] = ev with { Summary = summary, Updated = Tick() };
    }

    /// <summary>Deleting in Google leaves a cancelled tombstone that incremental reads return.</summary>
    public void CancelEvent(string eventId)
    {
        var ev = _events[eventId];
        _events[eventId] = ev with { IsCancelled = true, Updated = Tick() };
    }

    public void RemoveEventEntirely(string eventId) => _events.Remove(eventId);

    public Task<IReadOnlyList<CalendarSummary>> ListCalendarsAsync(CancellationToken ct)
    {
        GuardAuth();
        return Task.FromResult<IReadOnlyList<CalendarSummary>>(_calendars);
    }

    public Task<CalendarSummary> CreateCalendarAsync(string name, CancellationToken ct)
    {
        GuardAuth();
        var created = new CalendarSummary($"cal-{++_idSeed}", name, false, true);
        _calendars.Add(created);
        return Task.FromResult(created);
    }

    public Task<CalendarEventData> CreateEventAsync(string calendarId, CalendarEventData data, CancellationToken ct)
    {
        GuardAuth();
        CreateCount++;

        var stored = data with { Id = $"ev-{++_idSeed}", Updated = Tick() };
        _events[stored.Id] = stored;
        return Task.FromResult(stored);
    }

    public Task<CalendarEventData?> UpdateEventAsync(
        string calendarId, string eventId, CalendarEventData data, CancellationToken ct)
    {
        GuardAuth();

        if (!_events.ContainsKey(eventId)) return Task.FromResult<CalendarEventData?>(null);

        UpdateCount++;
        var stored = data with { Id = eventId, Updated = Tick() };
        _events[eventId] = stored;
        return Task.FromResult<CalendarEventData?>(stored);
    }

    public Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct)
    {
        GuardAuth();
        DeleteCount++;
        _events.Remove(eventId);
        return Task.CompletedTask;
    }

    public Task<CalendarChangePage> ListChangesAsync(
        string calendarId, string? syncToken, string? pageToken, DateTime? timeMin, CancellationToken ct)
    {
        GuardAuth();

        if (syncToken is not null && ExpireNextSyncToken)
        {
            ExpireNextSyncToken = false;
            throw new SyncTokenExpiredException("token expired");
        }

        // With a token, only what changed since it was issued; without, everything in the window.
        var since = syncToken is null ? null : syncToken;

        var events = _events.Values
            .Where(e => since is null || string.CompareOrdinal(e.Updated, since) > 0)
            .Where(e => since is not null || !e.IsCancelled)
            .OrderBy(e => e.Updated, StringComparer.Ordinal)
            .ToList();

        var newest = _events.Values.Count == 0
            ? Tick()
            : _events.Values.OrderBy(e => e.Updated, StringComparer.Ordinal).Last().Updated;

        return Task.FromResult(new CalendarChangePage(events, null, newest));
    }

    private void GuardAuth()
    {
        if (ThrowAuthError) throw new CalendarAuthException("Google has revoked this connection.");
    }
}
