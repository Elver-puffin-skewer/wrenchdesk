namespace WrenchDesk.Services.Google;

/// <summary>One calendar on the connected account.</summary>
public record CalendarSummary(string Id, string Name, bool IsPrimary, bool CanWrite);

/// <summary>
/// A calendar event, reduced to the fields WrenchDesk cares about. Keeping the sync engine
/// working against this rather than Google's own types is what makes the merge rules testable
/// without a network connection or a real account.
/// </summary>
public record CalendarEventData
{
    public string Id { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary>All-day events are never turned into appointments — a shop stop has a time.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>True once the event has been cancelled or deleted in Google.</summary>
    public bool IsCancelled { get; init; }

    /// <summary>Google's own last-modified stamp, used to tell whose copy is newer.</summary>
    public string Updated { get; init; } = "";

    /// <summary>Set on events WrenchDesk created, so imports can be told apart from our own.</summary>
    public long? WrenchDeskAppointmentId { get; init; }

    public int DurationMinutes => Math.Max(1, (int)Math.Round((End - Start).TotalMinutes));
}

/// <summary>
/// One page of changes. A null <see cref="NextSyncToken"/> means the caller should keep paging;
/// once it arrives, store it and the next run only asks for what has changed since.
/// </summary>
public record CalendarChangePage(IReadOnlyList<CalendarEventData> Events, string? NextPageToken, string? NextSyncToken);

/// <summary>
/// Thrown when Google rejects a stored sync token — normal after a long gap. The caller answers by
/// discarding the token and doing a full read rather than treating it as a failure.
/// </summary>
public class SyncTokenExpiredException : Exception
{
    public SyncTokenExpiredException(string message) : base(message) { }
}

/// <summary>Thrown when the account's authorisation is gone and the shop has to reconnect.</summary>
public class CalendarAuthException : Exception
{
    public CalendarAuthException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The calendar operations the sync needs. Implemented against Google, faked in tests.</summary>
public interface ICalendarApi
{
    Task<IReadOnlyList<CalendarSummary>> ListCalendarsAsync(CancellationToken ct);

    Task<CalendarSummary> CreateCalendarAsync(string name, CancellationToken ct);

    Task<CalendarEventData> CreateEventAsync(string calendarId, CalendarEventData data, CancellationToken ct);

    /// <summary>Returns null when the event has been removed in Google since we last saw it.</summary>
    Task<CalendarEventData?> UpdateEventAsync(string calendarId, string eventId, CalendarEventData data, CancellationToken ct);

    Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct);

    Task<CalendarChangePage> ListChangesAsync(
        string calendarId, string? syncToken, string? pageToken, DateTime? timeMin, CancellationToken ct);
}
