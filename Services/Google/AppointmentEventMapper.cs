using System.Globalization;
using WrenchDesk.Data;

namespace WrenchDesk.Services.Google;

/// <summary>
/// Translates between a WrenchDesk appointment and a calendar event.
///
/// The asymmetry here is deliberate. Pushing sends everything, because the shop calendar should
/// read well on a phone. Pulling only takes back what a person plausibly changed in Google —
/// the time, how long it runs, where it is, and whether it was cancelled. The description we
/// generate is not read back, or every sync would overwrite the shop's own notes with a copy
/// of itself.
/// </summary>
public static class AppointmentEventMapper
{
    private const string TitleSeparator = " — ";

    /// <summary>Builds the event for a local appointment. Context comes from the joined row.</summary>
    public static CalendarEventData ToEvent(AppointmentRow row)
    {
        var start = row.Start ?? DateTime.Now;
        var duration = row.DurationMin <= 0 ? 60 : row.DurationMin;

        return new CalendarEventData
        {
            Id = "",
            Summary = BuildTitle(row.Kind, row.CustomerName, row.TicketNumber),
            Description = BuildDescription(row),
            Location = row.Address ?? "",
            Start = start,
            End = start.AddMinutes(duration),
            IsCancelled = row.Status == "Canceled",
            WrenchDeskAppointmentId = row.Id
        };
    }

    public static string BuildTitle(string kind, string? customerName, string? ticketNumber)
    {
        var who = string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName;
        var ticket = string.IsNullOrWhiteSpace(ticketNumber) ? "" : $" ({ticketNumber})";
        return $"{kind}{TitleSeparator}{who}{ticket}";
    }

    public static string BuildDescription(AppointmentRow row)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.CustomerName)) lines.Add($"Customer: {row.CustomerName}");
        if (!string.IsNullOrWhiteSpace(row.CustomerPhone)) lines.Add($"Phone: {row.CustomerPhone}");
        if (!string.IsNullOrWhiteSpace(row.TicketNumber)) lines.Add($"Ticket: {row.TicketNumber}");
        if (!string.IsNullOrWhiteSpace(row.Notes)) lines.Add(row.Notes);
        lines.Add("");
        lines.Add("Managed by WrenchDesk. Moving this event here will move it in the shop app.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reads a kind out of an event title, so renaming "Pickup — Dale" to "Delivery — Dale" in
    /// Google carries across. Anything unrecognised leaves the existing kind alone.
    /// </summary>
    public static string? ParseKind(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;

        var head = summary.Split(TitleSeparator, 2, StringSplitOptions.TrimEntries)[0];
        return Appointment.Kinds.FirstOrDefault(k => string.Equals(k, head, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies the fields a person can meaningfully change in Google onto an appointment we own.
    /// Returns true when something actually differed, so an unchanged event costs no write.
    /// </summary>
    public static bool ApplyToExisting(Appointment appointment, CalendarEventData ev)
    {
        var changed = false;

        var scheduled = FormatLocal(ev.Start);
        if (appointment.ScheduledLocal != scheduled)
        {
            appointment.ScheduledLocal = scheduled;
            changed = true;
        }

        if (appointment.DurationMin != ev.DurationMinutes)
        {
            appointment.DurationMin = ev.DurationMinutes;
            changed = true;
        }

        var location = ev.Location ?? "";
        if (appointment.Address != location)
        {
            appointment.Address = location;
            changed = true;
        }

        var kind = ParseKind(ev.Summary);
        if (kind is not null && appointment.Kind != kind)
        {
            appointment.Kind = kind;
            changed = true;
        }

        // Cancelling in Google marks the stop cancelled here rather than deleting it, so the
        // shop can still see that it was meant to happen.
        if (ev.IsCancelled && appointment.Status != "Canceled")
        {
            appointment.Status = "Canceled";
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Turns an event created directly in Google into a new appointment. It has no customer —
    /// somebody at the shop can attach one afterwards.
    /// </summary>
    public static Appointment ToNewAppointment(CalendarEventData ev)
    {
        var notes = string.IsNullOrWhiteSpace(ev.Description)
            ? ""
            : ev.Description.Trim();

        // A foreign event's title carries the meaning, so keep it where someone will read it.
        var kind = ParseKind(ev.Summary) ?? "Other";
        var title = ev.Summary?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(title) && ParseKind(ev.Summary) is null)
            notes = string.IsNullOrWhiteSpace(notes) ? title : $"{title}\n{notes}";

        return new Appointment
        {
            CustomerId = null,
            TicketId = null,
            Kind = kind,
            ScheduledLocal = FormatLocal(ev.Start),
            DurationMin = ev.DurationMinutes,
            Address = ev.Location ?? "",
            Status = ev.IsCancelled ? "Canceled" : "Scheduled",
            Notes = Truncate(notes, 500),
            GoogleEventId = ev.Id
        };
    }

    /// <summary>
    /// Decides who wins when both sides changed since the last sync. Newest edit wins; a tie goes
    /// to the shop, because that is the copy the person in front of the machine is looking at.
    /// </summary>
    public static bool GoogleWins(string localUpdatedUtc, string googleUpdated)
    {
        var local = ParseUtc(localUpdatedUtc);
        var remote = ParseUtc(googleUpdated);

        if (local is null) return true;
        if (remote is null) return false;

        return remote > local;
    }

    public static string FormatLocal(DateTime local) => local.ToString("yyyy-MM-dd HH:mm");

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
