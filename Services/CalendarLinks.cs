using System.Text;
using WrenchDesk.Data;

namespace WrenchDesk.Services;

/// <summary>
/// Bridges the schedule to Google Calendar and Google Maps without needing an API key or an
/// OAuth sign-in: "Add to Google Calendar" links open a prefilled event, and the .ics export
/// imports a whole run of pickups at once. A real two-way sync can replace this later.
/// </summary>
public static class CalendarLinks
{
    /// <summary>Human-readable event title, e.g. "Pickup — Dale Fenner (WD-1042)".</summary>
    public static string Title(AppointmentRow a)
    {
        var who = string.IsNullOrWhiteSpace(a.CustomerName) ? "Customer" : a.CustomerName;
        var ticket = string.IsNullOrWhiteSpace(a.TicketNumber) ? "" : $" ({a.TicketNumber})";
        return $"{a.Kind} — {who}{ticket}";
    }

    public static string Details(AppointmentRow a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.CustomerName)) parts.Add($"Customer: {a.CustomerName}");
        if (!string.IsNullOrWhiteSpace(a.CustomerPhone)) parts.Add($"Phone: {a.CustomerPhone}");
        if (!string.IsNullOrWhiteSpace(a.TicketNumber)) parts.Add($"Ticket: {a.TicketNumber}");
        if (!string.IsNullOrWhiteSpace(a.Notes)) parts.Add(a.Notes);
        return string.Join("\n", parts);
    }

    /// <summary>Opens Google Calendar with the event prefilled; the user just presses Save.</summary>
    public static string GoogleCalendarUrl(AppointmentRow a)
    {
        var start = a.Start ?? DateTime.Now;
        var end = start.AddMinutes(a.DurationMin <= 0 ? 60 : a.DurationMin);

        // Google's template endpoint wants UTC stamps.
        var startUtc = start.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var endUtc = end.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

        var q = new Dictionary<string, string>
        {
            ["action"] = "TEMPLATE",
            ["text"] = Title(a),
            ["dates"] = $"{startUtc}/{endUtc}",
            ["details"] = Details(a),
            ["location"] = a.Address ?? ""
        };

        var query = string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"https://calendar.google.com/calendar/render?{query}";
    }

    /// <summary>Turn-by-turn directions to the stop, for the person driving the truck.</summary>
    public static string? MapsUrl(string? address) =>
        string.IsNullOrWhiteSpace(address)
            ? null
            : $"https://www.google.com/maps/dir/?api=1&destination={Uri.EscapeDataString(address)}";

    /// <summary>
    /// An .ics file covering the given appointments. Times are written as floating local time,
    /// which is what a shop wants — 9am stays 9am regardless of the importing calendar's zone.
    /// </summary>
    public static string BuildIcs(IEnumerable<AppointmentRow> appointments, string shopName)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\n");
        sb.Append("VERSION:2.0\r\n");
        sb.Append("PRODID:-//WrenchDesk//Shop Schedule//EN\r\n");
        sb.Append("CALSCALE:GREGORIAN\r\n");
        sb.Append("METHOD:PUBLISH\r\n");
        sb.Append($"X-WR-CALNAME:{Escape(shopName)} Schedule\r\n");

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");

        foreach (var a in appointments)
        {
            var start = a.Start;
            if (start is null) continue;

            var end = start.Value.AddMinutes(a.DurationMin <= 0 ? 60 : a.DurationMin);

            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:wrenchdesk-appt-{a.Id}@wrenchdesk.local\r\n");
            sb.Append($"DTSTAMP:{stamp}\r\n");
            sb.Append($"DTSTART:{start.Value:yyyyMMdd'T'HHmmss}\r\n");
            sb.Append($"DTEND:{end:yyyyMMdd'T'HHmmss}\r\n");
            sb.Append($"SUMMARY:{Escape(Title(a))}\r\n");

            if (!string.IsNullOrWhiteSpace(a.Address))
                sb.Append($"LOCATION:{Escape(a.Address)}\r\n");

            var details = Details(a);
            if (!string.IsNullOrWhiteSpace(details))
                sb.Append($"DESCRIPTION:{Escape(details)}\r\n");

            sb.Append($"STATUS:{(a.Status == "Canceled" ? "CANCELLED" : "CONFIRMED")}\r\n");
            sb.Append("END:VEVENT\r\n");
        }

        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    /// <summary>RFC 5545 text escaping: backslash, comma, semicolon and newlines.</summary>
    private static string Escape(string? value) =>
        (value ?? "")
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");
}
