namespace WrenchDesk.Services;

public static class BackupFrequency
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";

    public static readonly string[] All = { Daily, Weekly };
}

/// <summary>
/// Works out when a scheduled backup is owed. Kept as pure functions over an explicit "now" so the
/// awkward cases — the shop PC being switched off overnight, a run being missed entirely — can be
/// tested without waiting for a clock.
/// </summary>
public static class BackupSchedule
{
    /// <summary>
    /// The most recent moment the schedule called for a backup, at or before <paramref name="now"/>.
    /// A backup is owed whenever the last successful run is older than this.
    /// </summary>
    public static DateTime MostRecentOccurrence(DateTime now, string frequency, TimeOnly timeOfDay, DayOfWeek dayOfWeek)
    {
        if (frequency == BackupFrequency.Weekly)
        {
            // Walk back to the configured day, then step back a further week if that slot is still ahead of us.
            var daysSince = ((int)now.DayOfWeek - (int)dayOfWeek + 7) % 7;
            var candidate = now.Date.AddDays(-daysSince).Add(timeOfDay.ToTimeSpan());
            return candidate <= now ? candidate : candidate.AddDays(-7);
        }

        var today = now.Date.Add(timeOfDay.ToTimeSpan());
        return today <= now ? today : today.AddDays(-1);
    }

    /// <summary>The next moment the schedule will call for a backup, strictly after <paramref name="now"/>.</summary>
    public static DateTime NextOccurrence(DateTime now, string frequency, TimeOnly timeOfDay, DayOfWeek dayOfWeek)
    {
        var step = frequency == BackupFrequency.Weekly ? 7 : 1;
        return MostRecentOccurrence(now, frequency, timeOfDay, dayOfWeek).AddDays(step);
    }

    /// <summary>
    /// True when a backup is owed. A run missed while the PC was off is picked up on the next start
    /// rather than skipped — for a shop, a late backup beats no backup.
    /// </summary>
    public static bool IsDue(DateTime now, DateTime? lastRun, string frequency, TimeOnly timeOfDay, DayOfWeek dayOfWeek)
    {
        var owed = MostRecentOccurrence(now, frequency, timeOfDay, dayOfWeek);
        return lastRun is null || lastRun.Value < owed;
    }

    /// <summary>Plain-English description for the settings screen.</summary>
    public static string Describe(string frequency, TimeOnly timeOfDay, DayOfWeek dayOfWeek) =>
        frequency == BackupFrequency.Weekly
            ? $"Every {dayOfWeek} at {timeOfDay:h:mm tt}"
            : $"Every day at {timeOfDay:h:mm tt}";
}
