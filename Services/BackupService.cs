using Dapper;
using WrenchDesk.Data;

namespace WrenchDesk.Services;

/// <summary>
/// Keeps rolling copies of the database. This is the safety net that replaces "the notebook is
/// the only copy" — it runs on startup and once a day after that, and any backup file is a
/// complete database that can simply be renamed back into place.
/// </summary>
public class BackupService
{
    private readonly Db _db;
    private readonly ILogger<BackupService> _log;

    /// <summary>How many daily backups to keep before the oldest is pruned.</summary>
    public const int KeepCount = 30;

    public BackupService(Db db, ILogger<BackupService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Writes a consistent snapshot using VACUUM INTO, which is safe while the app is running —
    /// unlike copying the file, which can catch a half-written WAL.
    /// </summary>
    public string CreateBackup(string? label = null)
    {
        Directory.CreateDirectory(_db.BackupDirectory);

        var suffix = string.IsNullOrWhiteSpace(label) ? "" : $"-{Sanitize(label)}";
        var path = Path.Combine(_db.BackupDirectory, $"wrenchdesk-{DateTime.Now:yyyy-MM-dd-HHmmss}{suffix}.db");

        using (var conn = _db.Open())
        {
            // VACUUM INTO fails if the target exists, so a same-second collision gets a fresh name.
            if (File.Exists(path))
                path = Path.Combine(_db.BackupDirectory, $"wrenchdesk-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid():N}.db");

            conn.Execute("VACUUM INTO @path;", new { path });
        }

        _log.LogInformation("Backup written to {Path}", path);
        Prune();
        return path;
    }

    /// <summary>Creates today's backup unless one already exists, so restarts do not spam the folder.</summary>
    public string? CreateDailyBackupIfNeeded()
    {
        Directory.CreateDirectory(_db.BackupDirectory);
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var alreadyDone = Directory
            .EnumerateFiles(_db.BackupDirectory, "wrenchdesk-*.db")
            .Any(f => Path.GetFileName(f).StartsWith($"wrenchdesk-{today}", StringComparison.Ordinal));

        return alreadyDone ? null : CreateBackup();
    }

    public List<FileInfo> ListBackups()
    {
        Directory.CreateDirectory(_db.BackupDirectory);
        return new DirectoryInfo(_db.BackupDirectory)
            .GetFiles("wrenchdesk-*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();
    }

    private void Prune()
    {
        foreach (var old in ListBackups().Skip(KeepCount))
        {
            try
            {
                old.Delete();
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "Could not prune old backup {Name}", old.Name);
            }
        }
    }

    private static string Sanitize(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(label.Where(c => !invalid.Contains(c)).ToArray());
    }
}

/// <summary>Runs the daily backup in the background so nobody has to remember to.</summary>
public class BackupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BackupBackgroundService> _log;

    public BackupBackgroundService(IServiceProvider services, ILogger<BackupBackgroundService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
                backups.CreateDailyBackupIfNeeded();
            }
            catch (Exception ex)
            {
                // A failed backup must never take the app down — the shop still needs to write tickets.
                _log.LogError(ex, "Daily backup failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
