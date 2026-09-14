using Dapper;

namespace WrenchDesk.Data;

/// <summary>
/// Photos of a machine as it came in, and of the work done.
///
/// The picture itself is a file in the Photos folder beside the database; only the record of what
/// it belongs to is stored in SQLite. That keeps the database small enough to stay copyable, and
/// means a photo can be looked at with anything that opens a picture, without WrenchDesk at all.
/// </summary>
public class PhotoRepo
{
    private readonly Db _db;
    private readonly ILogger<PhotoRepo> _log;

    /// <summary>
    /// What a phone or a camera actually produces. Anything else is refused outright rather than
    /// stored and then found to be unopenable.
    /// </summary>
    public static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Generous for a phone photo, small enough that a slip cannot fill the shop's disk.</summary>
    public const long MaxBytes = 12 * 1024 * 1024;

    public PhotoRepo(Db db, ILogger<PhotoRepo> log)
    {
        _db = db;
        _log = log;
    }

    public List<Photo> ForTicket(long ticketId)
    {
        using var conn = _db.Open();
        return conn.Query<Photo>(
            "SELECT * FROM photos WHERE ticket_id = @ticketId ORDER BY id;", new { ticketId }).ToList();
    }

    public int CountForTicket(long ticketId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM photos WHERE ticket_id = @ticketId;", new { ticketId });
    }

    /// <summary>
    /// Stores one picture. The file is written first and the row only after it is safely on disk,
    /// so a failed write can never leave a thumbnail pointing at nothing.
    /// Returns null on success, or a sentence to show the person who tried.
    /// </summary>
    public async Task<string?> AddAsync(long ticketId, long? equipmentId, string originalName,
        Stream content, string caption = "", CancellationToken ct = default)
    {
        var extension = Path.GetExtension(originalName ?? "").ToLowerInvariant();

        if (!AllowedExtensions.Contains(extension))
            return $"{Path.GetFileName(originalName)} is not a picture WrenchDesk can show. "
                 + "Use a JPG, PNG or WEBP - which is what a phone camera gives you.";

        // Nothing the person supplied is used in the name. A file called ..\..\something is then
        // simply not a thing that can happen.
        var fileName = $"t{ticketId}-{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(_db.PhotoDirectory, fileName);

        try
        {
            Directory.CreateDirectory(_db.PhotoDirectory);

            await using (var file = File.Create(fullPath))
            {
                await content.CopyToAsync(file, ct);
            }

            var written = new FileInfo(fullPath).Length;
            if (written == 0)
            {
                TryDeleteFile(fullPath);
                return "That file came through empty. Try adding it again.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not store photo for ticket {TicketId}", ticketId);
            TryDeleteFile(fullPath);
            return $"Could not save that picture: {ex.Message}";
        }

        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO photos (ticket_id, equipment_id, file_name, caption, created_utc)
            VALUES (@ticketId, @equipmentId, @fileName, @caption, @now);
            """,
            new { ticketId, equipmentId, fileName, caption, now = DateTime.UtcNow.ToString("O") });

        return null;
    }

    public void SetCaption(long id, string caption)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE photos SET caption = @caption WHERE id = @id;", new { id, caption });
    }

    /// <summary>
    /// Removes the record and then the file. In that order on purpose: a file left behind is
    /// tidy-up, while a row pointing at a deleted file is a broken picture on the ticket.
    /// </summary>
    public void Delete(long id)
    {
        using var conn = _db.Open();

        var fileName = conn.ExecuteScalar<string?>(
            "SELECT file_name FROM photos WHERE id = @id;", new { id });

        conn.Execute("DELETE FROM photos WHERE id = @id;", new { id });

        if (!string.IsNullOrWhiteSpace(fileName))
            TryDeleteFile(Path.Combine(_db.PhotoDirectory, fileName));
    }

    /// <summary>
    /// Turns a name from a URL into a file on disk, or null if it is not one of ours.
    ///
    /// Everything about this is deliberately narrow. The name has to be a bare file name, made of
    /// characters we generate, ending in an extension we allow, and the path it resolves to has
    /// to still be inside the Photos folder. A request for a name dressed up to climb out of that
    /// folder gets nothing.
    /// </summary>
    public string? ResolveFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        if (fileName.Length > 128) return null;

        // A name with a directory in it is not a name.
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return null;

        if (!fileName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.')) return null;
        if (fileName.Contains("..", StringComparison.Ordinal)) return null;

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) return null;

        var root = Path.GetFullPath(_db.PhotoDirectory);
        var full = Path.GetFullPath(Path.Combine(root, fileName));

        // Belt and braces: whatever the name did, the answer has to be inside the folder.
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        return File.Exists(full) ? full : null;
    }

    public static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove photo file {Path}", path);
        }
    }
}
