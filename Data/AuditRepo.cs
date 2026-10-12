using Dapper;

namespace WrenchDesk.Data;

/// <summary>One thing somebody did, and when.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public string AtUtc { get; set; } = "";
    public long? UserId { get; set; }

    /// <summary>Copied in at the time, so the trail still reads after an account is removed.</summary>
    public string UserName { get; set; } = "";

    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public long? EntityId { get; set; }
    public string Detail { get; set; } = "";

    public DateTime? At =>
        DateTime.TryParse(AtUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime() : null;

    /// <summary>Where this entry points, when it points at something that still exists.</summary>
    public string? Link => Entity switch
    {
        AuditEntity.Ticket when EntityId is not null => "/tickets/" + EntityId,
        AuditEntity.Customer when EntityId is not null => "/customers/" + EntityId,
        _ => null
    };
}

/// <summary>What an entry is about. Kept as constants so the log can be filtered sensibly.</summary>
public static class AuditEntity
{
    public const string Ticket = "Ticket";
    public const string Customer = "Customer";
    public const string Payment = "Payment";
    public const string User = "User";
    public const string Settings = "Settings";
    public const string Session = "Session";
}

/// <summary>
/// The record of who did what.
///
/// Written for the awkward conversations: a ticket closed on the wrong day, a payment that is not
/// where somebody expected, a customer record that has gone. It covers the things worth answering
/// for - money, deletions, merges, status changes, accounts, settings and sign-ins - rather than
/// every keystroke, because a log nobody can read is the same as no log.
/// </summary>
public class AuditRepo
{
    private readonly Db _db;

    public AuditRepo(Db db) => _db = db;

    public void Write(long? userId, string userName, string action,
        string entity = "", long? entityId = null, string detail = "")
    {
        using var conn = _db.Open();
        conn.Execute(
            "INSERT INTO audit_log (at_utc, user_id, user_name, action, entity, entity_id, detail) " +
            "VALUES (@now, @userId, @userName, @action, @entity, @entityId, @detail);",
            new
            {
                now = DateTime.UtcNow.ToString("O"),
                userId,
                userName = userName ?? "",
                action,
                entity,
                entityId,
                detail = detail ?? ""
            });
    }

    /// <summary>Most recent first, optionally narrowed to one kind of thing or one person.</summary>
    public List<AuditEntry> Recent(int limit = 200, string? entity = null, long? userId = null)
    {
        using var conn = _db.Open();

        var clauses = new List<string>();
        var param = new DynamicParameters();
        param.Add("limit", Math.Clamp(limit, 1, 2000));

        if (!string.IsNullOrWhiteSpace(entity))
        {
            clauses.Add("entity = @entity");
            param.Add("entity", entity);
        }

        if (userId is not null)
        {
            clauses.Add("user_id = @userId");
            param.Add("userId", userId);
        }

        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";

        return conn.Query<AuditEntry>(
            $"SELECT * FROM audit_log {where} ORDER BY id DESC LIMIT @limit;", param).ToList();
    }

    /// <summary>Everything that has happened to one record, oldest first - its own history.</summary>
    public List<AuditEntry> ForEntity(string entity, long entityId)
    {
        using var conn = _db.Open();
        return conn.Query<AuditEntry>(
            "SELECT * FROM audit_log WHERE entity = @entity AND entity_id = @entityId ORDER BY id;",
            new { entity, entityId }).ToList();
    }

    public int Count()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM audit_log;");
    }

    /// <summary>
    /// Drops entries older than the given number of days. Nothing calls this on a schedule yet -
    /// a shop's log is a few thousand rows a year and worth more kept than tidied - but a shop
    /// that wants it trimmed should not have to open the database to do it.
    /// </summary>
    public int TrimOlderThan(int days)
    {
        if (days <= 0) return 0;

        using var conn = _db.Open();
        return conn.Execute("DELETE FROM audit_log WHERE at_utc < @cutoff;",
            new { cutoff = DateTime.UtcNow.AddDays(-days).ToString("O") });
    }
}
