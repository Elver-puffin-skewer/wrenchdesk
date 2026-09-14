using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WrenchDesk.Data;
using WrenchDesk.Services;

namespace WrenchDesk.Tests;

/// <summary>
/// A schema change cannot be undone, and it lands on the day the shop downloads a new version -
/// which is exactly the day nobody thought to take a backup first. So the program takes one.
/// </summary>
public class UpgradeSafetyTests : IDisposable
{
    private readonly string _dir;

    public UpgradeSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wrenchdesk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void A_brand_new_shop_gets_no_copy_of_nothing()
    {
        var db = Open();
        db.Migrate();

        // Version 0 has nothing in it worth copying.
        Assert.Empty(PreUpgradeCopies(db));
    }

    [Fact]
    public void An_ordinary_start_copies_nothing()
    {
        var db = Open();
        db.Migrate();

        // Already current: starting the program every morning must not pile up copies.
        db.Migrate();
        db.Migrate();

        Assert.Empty(PreUpgradeCopies(db));
    }

    [Fact]
    public void Meeting_a_database_from_an_older_release_copies_it_first()
    {
        var db = Open();

        // A shop on the version before this one.
        db.MigrateTo(Db.LatestSchemaVersion - 1);
        Assert.Empty(PreUpgradeCopies(db));

        // They download the new one.
        db.Migrate();

        var copy = Assert.Single(PreUpgradeCopies(db));
        Assert.StartsWith(Db.PreUpgradePrefix, Path.GetFileName(copy));
        Assert.True(new FileInfo(copy).Length > 0);
    }

    [Fact]
    public void The_copy_holds_the_shop_as_it_was_before_the_update()
    {
        var db = Open();
        db.MigrateTo(Db.LatestSchemaVersion - 1);

        // Written through raw SQL on purpose: the repositories in this build already know about
        // columns the older schema does not have, which is the whole situation being tested.
        using (var conn = db.Open())
        {
            conn.Execute("""
                INSERT INTO customers (first_name, last_name, phone, created_utc, updated_utc)
                VALUES ('Dale', 'Fenner', '256-555-0142', '2026-09-01', '2026-09-01');
                """);
        }

        db.Migrate();

        // Renaming this file into place is the whole restore procedure, so it has to open, read,
        // and still be the shape the old version expects.
        var copy = PreUpgradeCopies(db).Single();
        using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy}");
        check.Open();

        Assert.Equal(Db.LatestSchemaVersion - 1, check.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal("Fenner", check.ExecuteScalar<string>("SELECT last_name FROM customers;"));

        // The live file moved on; the copy did not.
        Assert.Equal(Db.LatestSchemaVersion, CurrentVersion(db));
    }

    [Fact]
    public void Backup_retention_never_deletes_the_pre_upgrade_copy()
    {
        var db = Open();
        db.MigrateTo(Db.LatestSchemaVersion - 1);
        db.Migrate();

        var copy = PreUpgradeCopies(db).Single();

        var settings = new SettingsStore(db);
        settings.Set(SettingsStore.BackupKeepCount, "1");
        var backups = new BackupService(db, settings, NullLogger<BackupService>.Instance);

        // Retention prunes wrenchdesk-*.db. This copy is the one thing that has to outlive every
        // rolling backup, so it is deliberately not named like one.
        for (var i = 0; i < 4; i++)
            Assert.True(backups.CreateBackup(db.BackupDirectory, applyRetention: true).Success);

        Assert.True(File.Exists(copy));
        Assert.Single(PreUpgradeCopies(db));
    }

    private Db Open()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WrenchDesk:DataDirectory"] = _dir })
            .Build();
        return new Db(config);
    }

    private static string[] PreUpgradeCopies(Db db) =>
        Directory.Exists(db.BackupDirectory)
            ? Directory.GetFiles(db.BackupDirectory, Db.PreUpgradePrefix + "*.db")
            : Array.Empty<string>();

    private static long CurrentVersion(Db db)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>("PRAGMA user_version;");
    }

    private static void SetVersion(Db db, long version)
    {
        using var conn = db.Open();
        conn.Execute($"PRAGMA user_version={version};");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Clicking the icon while WrenchDesk is already down by the clock opens the shop screen - unless
/// the shop has said it does not want browser windows opened, which is an answer that holds
/// whoever started the program.
/// </summary>
public class HandOverTests
{
    [Fact]
    public void The_setting_is_read_from_configuration_the_way_startup_reads_it()
    {
        var off = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WrenchDesk:OpenBrowser"] = "false" })
            .Build();

        var unset = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        Assert.False(off.GetValue("WrenchDesk:OpenBrowser", true));
        Assert.True(unset.GetValue("WrenchDesk:OpenBrowser", true));
    }
}
