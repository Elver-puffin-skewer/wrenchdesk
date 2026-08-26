using WrenchDesk.Data;
using WrenchDesk.Services;

namespace WrenchDesk.Tests;

public class BackupScheduleTests
{
    private static readonly TimeOnly SixPm = new(18, 0);

    [Fact]
    public void Daily_is_not_due_before_the_scheduled_time_on_the_first_day()
    {
        // 5pm, schedule is 6pm, never run: yesterday's 6pm slot is what is owed.
        var now = new DateTime(2026, 8, 25, 17, 0, 0);
        var lastRun = new DateTime(2026, 8, 24, 18, 0, 0);

        Assert.False(BackupSchedule.IsDue(now, lastRun, BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Daily_becomes_due_once_the_time_passes()
    {
        var now = new DateTime(2026, 8, 25, 18, 1, 0);
        var lastRun = new DateTime(2026, 8, 24, 18, 0, 0);

        Assert.True(BackupSchedule.IsDue(now, lastRun, BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Daily_does_not_run_twice_in_one_day()
    {
        var now = new DateTime(2026, 8, 25, 22, 0, 0);
        var lastRun = new DateTime(2026, 8, 25, 18, 0, 30);

        Assert.False(BackupSchedule.IsDue(now, lastRun, BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Never_run_is_always_due()
    {
        var now = new DateTime(2026, 8, 25, 9, 0, 0);

        Assert.True(BackupSchedule.IsDue(now, null, BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void A_run_missed_while_the_pc_was_off_is_picked_up_on_the_next_start()
    {
        // Shop closed Friday evening, PC off all weekend, opened again Monday morning.
        var now = new DateTime(2026, 8, 31, 8, 30, 0);
        var lastRun = new DateTime(2026, 8, 28, 18, 0, 0);

        Assert.True(BackupSchedule.IsDue(now, lastRun, BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Weekly_is_due_only_on_or_after_the_chosen_day()
    {
        var lastRun = new DateTime(2026, 8, 21, 18, 0, 0);

        // Thursday 27 Aug 2026 — before Friday's slot.
        Assert.False(BackupSchedule.IsDue(
            new DateTime(2026, 8, 27, 20, 0, 0), lastRun, BackupFrequency.Weekly, SixPm, DayOfWeek.Friday));

        // Friday 28 Aug 2026, after 6pm.
        Assert.True(BackupSchedule.IsDue(
            new DateTime(2026, 8, 28, 18, 5, 0), lastRun, BackupFrequency.Weekly, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Weekly_does_not_repeat_within_the_same_week()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        var lastRun = new DateTime(2026, 8, 28, 18, 0, 10);

        Assert.False(BackupSchedule.IsDue(now, lastRun, BackupFrequency.Weekly, SixPm, DayOfWeek.Friday));
    }

    [Fact]
    public void Next_occurrence_is_always_in_the_future()
    {
        var now = new DateTime(2026, 8, 25, 18, 30, 0);

        var nextDaily = BackupSchedule.NextOccurrence(now, BackupFrequency.Daily, SixPm, DayOfWeek.Friday);
        Assert.Equal(new DateTime(2026, 8, 26, 18, 0, 0), nextDaily);

        var nextWeekly = BackupSchedule.NextOccurrence(now, BackupFrequency.Weekly, SixPm, DayOfWeek.Friday);
        Assert.Equal(new DateTime(2026, 8, 28, 18, 0, 0), nextWeekly);
        Assert.True(nextWeekly > now);
    }

    [Fact]
    public void Description_reads_plainly()
    {
        Assert.Equal("Every day at 6:00 PM",
            BackupSchedule.Describe(BackupFrequency.Daily, SixPm, DayOfWeek.Friday));
        Assert.Equal("Every Friday at 6:00 PM",
            BackupSchedule.Describe(BackupFrequency.Weekly, SixPm, DayOfWeek.Friday));
    }
}

public class BackupServiceTests
{
    [Fact]
    public void Automatic_backups_are_off_until_switched_on()
    {
        using var h = new TestDb();

        Assert.False(h.Backups.AutoEnabled);
        Assert.Null(h.Backups.RunScheduledIfDue(DateTime.Now));
        Assert.Empty(h.Backups.ListBackups());
    }

    [Fact]
    public void Nothing_is_written_on_a_schedule_while_disabled_even_when_overdue()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "00:01");

        // Never run and long past the time — still nothing, because the feature is off.
        Assert.Null(h.Backups.RunScheduledIfDue(new DateTime(2026, 8, 25, 23, 59, 0)));
        Assert.Empty(h.Backups.ListBackups());
    }

    [Fact]
    public void Enabling_the_schedule_produces_a_backup_and_records_the_run()
    {
        using var h = new TestDb();
        h.NewCustomer();

        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");

        var result = h.Backups.RunScheduledIfDue(new DateTime(2026, 8, 25, 18, 0, 0));

        Assert.NotNull(result);
        Assert.True(result!.Success, result.Error);
        Assert.True(File.Exists(result.Path));
        Assert.Single(h.Backups.ListBackups());
        Assert.NotNull(h.Settings.GetTimestamp(SettingsStore.BackupLastRun));
    }

    [Fact]
    public void A_second_check_the_same_day_does_not_write_again()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");

        var at = new DateTime(2026, 8, 25, 18, 0, 0);
        h.Backups.RunScheduledIfDue(at);
        var second = h.Backups.RunScheduledIfDue(at.AddMinutes(1));

        Assert.Null(second);
        Assert.Single(h.Backups.ListBackups());
    }

    [Fact]
    public void A_failed_scheduled_run_does_not_advance_the_clock_so_it_retries()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupAutoEnabled, "true");
        h.Settings.Set(SettingsStore.BackupTimeOfDay, "06:00");
        h.Settings.Set(SettingsStore.BackupDestination, "Z:\\definitely-not-a-real-drive");

        var result = h.Backups.RunScheduledIfDue(new DateTime(2026, 8, 25, 18, 0, 0));

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Null(h.Settings.GetTimestamp(SettingsStore.BackupLastRun));
        Assert.NotEmpty(h.Settings.Get(SettingsStore.BackupLastError));
    }

    [Fact]
    public void On_demand_backup_writes_to_the_folder_it_is_given()
    {
        using var h = new TestDb();
        h.NewCustomer();

        var target = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));

        var result = h.Backups.CreateBackup(target, label: "manual");

        Assert.True(result.Success, result.Error);
        Assert.StartsWith(target, result.Path);
        Assert.Contains("manual", Path.GetFileName(result.Path)!);
        Assert.True(result.Bytes > 0);

        Directory.Delete(target, recursive: true);
    }

    [Fact]
    public void On_demand_backup_creates_the_destination_folder_if_it_is_missing()
    {
        using var h = new TestDb();
        var target = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"), "Nested", "Deeper");

        var result = h.Backups.CreateBackup(target);

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(target));

        Directory.Delete(Path.GetFullPath(Path.Combine(target, "..", "..")), recursive: true);
    }

    [Fact]
    public void A_backup_is_a_complete_database_that_opens_on_its_own()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer("Dale", "Fenner");
        var ticketId = h.NewTicket(customerId);
        h.AddLine(ticketId, "Part", 1, 25m, taxable: true);

        var result = h.Backups.CreateBackup();
        Assert.True(result.Success, result.Error);

        // Open the backup file directly — it must carry the data, not just be a valid-looking file.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={result.Path};Mode=ReadOnly");
        conn.Open();

        Assert.Equal(1, Dapper.SqlMapper.ExecuteScalar<int>(conn, "SELECT COUNT(*) FROM customers;"));
        Assert.Equal(1, Dapper.SqlMapper.ExecuteScalar<int>(conn, "SELECT COUNT(*) FROM tickets;"));
        Assert.Equal("Fenner", Dapper.SqlMapper.ExecuteScalar<string>(conn, "SELECT last_name FROM customers;"));
    }

    [Fact]
    public void An_unavailable_drive_reports_a_clear_message_rather_than_throwing()
    {
        using var h = new TestDb();

        var problem = h.Backups.ValidateDestination("Z:\\definitely-not-a-real-drive");

        Assert.NotNull(problem);
        Assert.Contains("Z:", problem);

        var result = h.Backups.CreateBackup("Z:\\definitely-not-a-real-drive");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_empty_destination_is_rejected_with_guidance()
    {
        using var h = new TestDb();

        Assert.NotNull(h.Backups.ValidateDestination(""));
        Assert.NotNull(h.Backups.ValidateDestination("   "));
    }

    [Fact]
    public void Retention_removes_the_oldest_beyond_the_keep_count()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupKeepCount, "3");

        for (var i = 0; i < 5; i++)
        {
            var result = h.Backups.CreateBackup(label: $"n{i}", applyRetention: true);
            Assert.True(result.Success, result.Error);

            // VACUUM INTO names by the second, so nudge the timestamps apart to make ordering deterministic.
            File.SetLastWriteTimeUtc(result.Path!, DateTime.UtcNow.AddMinutes(i));
        }

        Assert.Equal(3, h.Backups.ListBackups().Count);
    }

    [Fact]
    public void Retention_never_touches_files_wrenchdesk_did_not_write()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupKeepCount, "1");

        var bystander = Path.Combine(h.Db.BackupDirectory, "important-tax-records.xlsx");
        Directory.CreateDirectory(h.Db.BackupDirectory);
        File.WriteAllText(bystander, "not ours");

        for (var i = 0; i < 3; i++)
            h.Backups.CreateBackup(label: $"n{i}", applyRetention: true);

        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public void On_demand_backups_are_never_pruned()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.BackupKeepCount, "1");

        for (var i = 0; i < 4; i++)
        {
            var result = h.Backups.CreateBackup(label: $"manual{i}");
            Assert.True(result.Success, result.Error);
            File.SetLastWriteTimeUtc(result.Path!, DateTime.UtcNow.AddMinutes(i));
        }

        // Nothing was removed, because the on-demand path does not apply retention.
        Assert.Equal(4, h.Backups.ListBackups().Count);
    }

    [Fact]
    public void Scheduled_destination_falls_back_to_the_data_folder_when_unset()
    {
        using var h = new TestDb();

        Assert.Equal(h.Db.BackupDirectory, h.Backups.ScheduledDestination);

        h.Settings.Set(SettingsStore.BackupDestination, "D:\\ShopBackups");
        Assert.Equal("D:\\ShopBackups", h.Backups.ScheduledDestination);
    }

    [Fact]
    public void Drive_listing_finds_at_least_the_system_drive()
    {
        var drives = BackupService.ListDrives();

        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.False(string.IsNullOrWhiteSpace(d.Display)));
    }
}
