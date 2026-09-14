using Dapper;
using WrenchDesk.Data;

namespace WrenchDesk.Services;

/// <summary>Outcome of one backup attempt. Failures are reported, never thrown at the UI.</summary>
public record BackupResult(bool Success, string? Path, string? Error, long Bytes)
{
    /// <summary>How many photo files were copied alongside the database this time.</summary>
    public int PhotosCopied { get; init; }

    /// <summary>
    /// Set when the database was written but a photo could not be. The backup still counts as
    /// done - the records are the part that matters - but the shop should be told.
    /// </summary>
    public string? PhotoWarning { get; init; }

    public static BackupResult Failed(string error) => new(false, null, error, 0);
    public static BackupResult Ok(string path, long bytes) => new(true, path, null, bytes);
}

/// <summary>A drive on the shop PC, offered as a backup destination.</summary>
public record DriveOption(string Root, string Label, string Kind, long FreeBytes, bool IsRemovable)
{
    /// <summary>What the dropdown shows, e.g. "E:\ — BACKUP USB (removable, 28.4 GB free)".</summary>
    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Label) ? Kind : Label;
            var kind = IsRemovable ? "removable, " : "";
            return $"{Root} — {name} ({kind}{FormatSize(FreeBytes)} free)";
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024d / 1024d:0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} bytes";
}

/// <summary>
/// Writes complete copies of the database. Every backup file is a whole working database — it can be
/// renamed back into place with nothing else to restore.
///
/// Two ways in: the scheduler (off unless the shop turns it on) and the on-demand button, which can
/// target any folder or drive the PC can see, including a USB stick they keep off site.
/// </summary>
public class BackupService
{
    private readonly Db _db;
    private readonly SettingsStore _settings;
    private readonly ILogger<BackupService> _log;

    /// <summary>Name given to every backup, so retention only ever considers files we wrote.</summary>
    private const string FilePattern = "wrenchdesk-*.db";

    public BackupService(Db db, SettingsStore settings, ILogger<BackupService> log)
    {
        _db = db;
        _settings = settings;
        _log = log;
    }

    /// <summary>Where scheduled backups go — the configured folder, or the data folder if none is set.</summary>
    public string ScheduledDestination
    {
        get
        {
            var configured = _settings.Get(SettingsStore.BackupDestination);
            return string.IsNullOrWhiteSpace(configured) ? _db.BackupDirectory : configured;
        }
    }

    /// <summary>
    /// A second folder the scheduled backup also writes to, or empty. A shop keeping one drive at
    /// the bench and another elsewhere wants both written the same night.
    /// </summary>
    public string SecondDestination => _settings.Get(SettingsStore.BackupDestination2);

    public bool HasSecondDestination => !string.IsNullOrWhiteSpace(SecondDestination);

    public bool AutoEnabled => _settings.GetBool(SettingsStore.BackupAutoEnabled);

    public int KeepCount
    {
        get
        {
            var n = _settings.GetInt(SettingsStore.BackupKeepCount);
            return n <= 0 ? 30 : n;
        }
    }

    /// <summary>
    /// Checks a destination before anything is written, so an unplugged USB stick produces a clear
    /// message instead of a stack trace. Returns null when the folder is good to use.
    /// </summary>
    public string? ValidateDestination(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return "Choose a folder or drive first.";

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That is not a valid folder path.";
        }

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root))
        {
            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return $"Drive {root} is not available. Is the drive plugged in?";

                var needed = CurrentDatabaseBytes() + CurrentPhotoBytes() + (10 * 1024 * 1024);
                if (drive.AvailableFreeSpace < needed)
                    return $"Not enough room on {root} — needs about {DriveOption.FormatSize(needed)}, "
                         + $"only {DriveOption.FormatSize(drive.AvailableFreeSpace)} free.";
            }
            catch (ArgumentException)
            {
                // A UNC path has no DriveInfo; fall through and let the write attempt decide.
            }
            catch (IOException ex)
            {
                return $"Could not reach {root}: {ex.Message}";
            }
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not create that folder: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Writes a snapshot with VACUUM INTO, which is safe while the shop is still using the app —
    /// unlike copying the file, which can catch a half-written journal.
    /// </summary>
    public BackupResult CreateBackup(string? destinationDirectory = null, string? label = null, bool applyRetention = false)
    {
        var directory = string.IsNullOrWhiteSpace(destinationDirectory) ? _db.BackupDirectory : destinationDirectory;

        var problem = ValidateDestination(directory);
        if (problem is not null) return BackupResult.Failed(problem);

        var full = Path.GetFullPath(directory);
        var suffix = string.IsNullOrWhiteSpace(label) ? "" : $"-{Sanitize(label)}";
        var path = Path.Combine(full, $"wrenchdesk-{DateTime.Now:yyyy-MM-dd-HHmmss}{suffix}.db");

        // VACUUM INTO refuses to overwrite, so a same-second collision gets a distinct name.
        if (File.Exists(path))
            path = Path.Combine(full, $"wrenchdesk-{DateTime.Now:yyyy-MM-dd-HHmmss}{suffix}-{Guid.NewGuid():N}.db");

        try
        {
            using var conn = _db.Open();
            conn.Execute("VACUUM INTO @path;", new { path });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backup to {Path} failed", path);

            // A partial file is worse than none — it looks like a usable backup and is not.
            TryDelete(path);
            return BackupResult.Failed(ex.Message);
        }

        var bytes = new FileInfo(path).Length;
        _log.LogInformation("Backup written to {Path} ({Bytes} bytes)", path, bytes);

        // Photos live beside the database rather than inside it, so a copy of the database on its
        // own is not a copy of the shop. They go next to the backup in a Photos folder.
        var (photosCopied, photoWarning) = MirrorPhotos(full);

        if (applyRetention) Prune(full, justWritten: path);

        return BackupResult.Ok(path, bytes) with
        {
            PhotosCopied = photosCopied,
            PhotoWarning = photoWarning
        };
    }

    /// <summary>
    /// Runs the scheduled backup if one is owed. Returns null when nothing was due, so the caller
    /// can stay quiet on the vast majority of ticks.
    /// </summary>
    public BackupResult? RunScheduledIfDue(DateTime now)
    {
        if (!AutoEnabled) return null;

        var frequency = _settings.Get(SettingsStore.BackupFrequency);
        var timeOfDay = _settings.GetTime(SettingsStore.BackupTimeOfDay, new TimeOnly(18, 0));
        var dayOfWeek = _settings.GetDay(SettingsStore.BackupDayOfWeek, DayOfWeek.Friday);
        var lastRun = _settings.GetTimestamp(SettingsStore.BackupLastRun);

        if (!BackupSchedule.IsDue(now, lastRun, frequency, timeOfDay, dayOfWeek)) return null;

        var result = CreateBackup(ScheduledDestination, label: "auto", applyRetention: true);

        // The second copy is written even when it is the one that matters — a drive that is
        // unplugged must not stop the first copy being recorded as done.
        BackupResult? second = null;
        if (HasSecondDestination)
        {
            second = CreateBackup(SecondDestination, label: "auto", applyRetention: true);

            if (!second.Success)
            {
                _log.LogWarning("Second backup destination failed: {Error}", second.Error);
                _settings.Set(SettingsStore.BackupLastError,
                    $"{now:yyyy-MM-dd HH:mm} — second copy failed: {second.Error}");
            }
        }

        if (result.Success)
        {
            // Only a success advances the clock, so a failed run is retried on the next tick
            // rather than being silently skipped until tomorrow.
            var photos = result.PhotosCopied > 0
                ? $" (+{result.PhotosCopied} photo{(result.PhotosCopied == 1 ? "" : "s")})"
                : "";

            var summary = second is { Success: true }
                ? $"Saved to {result.Path} and {second.Path}{photos}"
                : second is not null
                    ? $"Saved to {result.Path}{photos}. Second copy failed: {second.Error}"
                    : $"Saved to {result.Path}{photos}";

            _settings.SetAll(new Dictionary<string, string>
            {
                [SettingsStore.BackupLastRun] = now.ToString("yyyy-MM-dd HH:mm:ss"),
                [SettingsStore.BackupLastResult] = summary,
                [SettingsStore.BackupLastError] = second is { Success: false }
                    ? $"{now:yyyy-MM-dd HH:mm} — second copy failed: {second.Error}"
                    : ""
            });
        }
        else
        {
            _settings.Set(SettingsStore.BackupLastError,
                $"{now:yyyy-MM-dd HH:mm} — {result.Error}");
        }

        return result;
    }

    /// <summary>Backups already sitting in a folder, newest first.</summary>
    public List<FileInfo> ListBackups(string? directory = null)
    {
        var full = string.IsNullOrWhiteSpace(directory) ? _db.BackupDirectory : directory;

        try
        {
            Directory.CreateDirectory(full);
            // Name is the tie-break because it embeds the timestamp — without it, several backups
            // written in the same second would sort arbitrarily and retention could drop the wrong one.
            return new DirectoryInfo(full)
                .GetFiles(FilePattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unplugged drive should show an empty list, not break the settings page.
            return new List<FileInfo>();
        }
    }

    /// <summary>Drives the PC can currently see, for the on-demand destination picker.</summary>
    public static List<DriveOption> ListDrives()
    {
        var options = new List<DriveOption>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is DriveType.Unknown or DriveType.CDRom) continue;

                options.Add(new DriveOption(
                    Root: drive.RootDirectory.FullName,
                    Label: drive.VolumeLabel,
                    Kind: drive.DriveType.ToString(),
                    FreeBytes: drive.AvailableFreeSpace,
                    IsRemovable: drive.DriveType == DriveType.Removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that vanishes mid-enumeration is simply not offered.
            }
        }

        // Removable drives first — that is what someone reaching for "back up to my USB stick" wants.
        return options
            .OrderByDescending(d => d.IsRemovable)
            .ThenBy(d => d.Root)
            .ToList();
    }

    public long CurrentDatabaseBytes() =>
        File.Exists(_db.DatabasePath) ? new FileInfo(_db.DatabasePath).Length : 0;

    /// <summary>Total size of the photo files, which a destination has to have room for too.</summary>
    public long CurrentPhotoBytes()
    {
        try
        {
            return Directory.Exists(_db.PhotoDirectory)
                ? new DirectoryInfo(_db.PhotoDirectory).GetFiles().Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public int CurrentPhotoCount()
    {
        try
        {
            return Directory.Exists(_db.PhotoDirectory)
                ? new DirectoryInfo(_db.PhotoDirectory).GetFiles().Length
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Copies photo files next to the backup, into a Photos folder.
    ///
    /// Files already there with the same size are left alone, so backing up daily to the same
    /// stick copies only what is new rather than the whole shop's photos every night. Nothing is
    /// ever deleted from the copy: a photo removed from a ticket by mistake is then still
    /// recoverable from the stick, which is the entire point of a backup.
    /// </summary>
    private (int Copied, string? Warning) MirrorPhotos(string destinationDirectory)
    {
        if (!Directory.Exists(_db.PhotoDirectory)) return (0, null);

        FileInfo[] sources;
        try
        {
            sources = new DirectoryInfo(_db.PhotoDirectory).GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, $"Could not read the photos folder: {ex.Message}");
        }

        if (sources.Length == 0) return (0, null);

        var target = Path.Combine(destinationDirectory, "Photos");
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, $"Could not make a Photos folder at the backup: {ex.Message}");
        }

        var copied = 0;
        string? warning = null;

        foreach (var source in sources)
        {
            var destination = Path.Combine(target, source.Name);

            try
            {
                var existing = new FileInfo(destination);
                if (existing.Exists && existing.Length == source.Length) continue;

                source.CopyTo(destination, overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not copy photo {Name} to {Target}", source.Name, target);
                warning ??= $"{source.Name} could not be copied: {ex.Message}";
            }
        }

        return (copied, warning);
    }

    /// <summary>
    /// Deletes the oldest managed backups beyond the keep count. Only ever touches files matching
    /// our own naming pattern, so pointing this at a folder holding other files cannot eat them.
    /// The backup that was just written is always kept, whatever the timestamps say.
    /// </summary>
    private void Prune(string directory, string? justWritten = null)
    {
        var candidates = ListBackups(directory)
            .Where(f => justWritten is null ||
                        !string.Equals(f.FullName, Path.GetFullPath(justWritten), StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Keep one slot for the file just written so the count still honours the setting.
        var keep = justWritten is null ? KeepCount : Math.Max(0, KeepCount - 1);

        foreach (var old in candidates.Skip(keep))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not remove old backup {Name}", old.Name);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not clean up partial backup {Path}", path);
        }
    }

    private static string Sanitize(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(label.Where(c => !invalid.Contains(c)).ToArray());
    }
}

/// <summary>
/// Ticks once a minute and asks the service whether a scheduled backup is owed. Does nothing at all
/// while automatic backups are switched off, which is the default.
/// </summary>
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

                var result = backups.RunScheduledIfDue(DateTime.Now);
                if (result is { Success: false })
                    _log.LogError("Scheduled backup failed: {Error}", result.Error);
            }
            catch (Exception ex)
            {
                // A backup problem must never take the app down — the shop still needs to write tickets.
                _log.LogError(ex, "Scheduled backup check failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
