using Dapper;
using Microsoft.Data.Sqlite;

namespace WrenchDesk.Data;

/// <summary>
/// Owns the SQLite file: where it lives, opening connections, and stepping the schema forward.
/// Everything is one local file so a backup is just a file copy.
/// </summary>
public class Db
{
    private readonly string _connectionString;

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string BackupDirectory { get; }

    static Db()
    {
        // Lets SQL keep snake_case column names while models stay PascalCase.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(IConfiguration config)
    {
        var configured = config["WrenchDesk:DataDirectory"];
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WrenchDesk")
            : Environment.ExpandEnvironmentVariables(configured);

        Directory.CreateDirectory(DataDirectory);
        BackupDirectory = Path.Combine(DataDirectory, "Backups");
        Directory.CreateDirectory(BackupDirectory);

        DatabasePath = Path.Combine(DataDirectory, "wrenchdesk.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // WAL survives an unclean shutdown far better, which matters on a shop PC that just gets switched off.
        conn.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return conn;
    }

    /// <summary>Applies any migrations the file has not seen yet. Safe to call on every startup.</summary>
    public void Migrate()
    {
        using var conn = Open();
        var version = conn.ExecuteScalar<long>("PRAGMA user_version;");

        for (var next = (int)version; next < Migrations.Length; next++)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(Migrations[next], transaction: tx);
            // PRAGMA will not take a parameter, and the value is a loop counter, not user input.
            conn.Execute($"PRAGMA user_version={next + 1};", transaction: tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Append-only list. Index N is the migration that moves the schema from version N to N+1 —
    /// never edit or reorder an entry that has shipped, only add to the end.
    /// </summary>
    private static readonly string[] Migrations =
    {
        // 0 -> 1: initial schema
        """
        CREATE TABLE customers (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            first_name    TEXT NOT NULL DEFAULT '',
            last_name     TEXT NOT NULL DEFAULT '',
            business_name TEXT NOT NULL DEFAULT '',
            phone         TEXT NOT NULL DEFAULT '',
            phone_alt     TEXT NOT NULL DEFAULT '',
            email         TEXT NOT NULL DEFAULT '',
            address1      TEXT NOT NULL DEFAULT '',
            address2      TEXT NOT NULL DEFAULT '',
            city          TEXT NOT NULL DEFAULT '',
            state         TEXT NOT NULL DEFAULT '',
            zip           TEXT NOT NULL DEFAULT '',
            notes         TEXT NOT NULL DEFAULT '',
            is_archived   INTEGER NOT NULL DEFAULT 0,
            created_utc   TEXT NOT NULL,
            updated_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_customers_last  ON customers(last_name, first_name);
        CREATE INDEX ix_customers_phone ON customers(phone);

        CREATE TABLE equipment (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            customer_id   INTEGER NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
            category      TEXT NOT NULL DEFAULT 'Other',
            make          TEXT NOT NULL DEFAULT '',
            model         TEXT NOT NULL DEFAULT '',
            serial        TEXT NOT NULL DEFAULT '',
            engine_make   TEXT NOT NULL DEFAULT '',
            engine_model  TEXT NOT NULL DEFAULT '',
            engine_serial TEXT NOT NULL DEFAULT '',
            year          TEXT NOT NULL DEFAULT '',
            notes         TEXT NOT NULL DEFAULT '',
            is_archived   INTEGER NOT NULL DEFAULT 0,
            created_utc   TEXT NOT NULL,
            updated_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_equipment_customer ON equipment(customer_id);

        CREATE TABLE tickets (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            number        TEXT NOT NULL UNIQUE,
            customer_id   INTEGER NOT NULL REFERENCES customers(id),
            equipment_id  INTEGER NULL REFERENCES equipment(id) ON DELETE SET NULL,
            status        TEXT NOT NULL DEFAULT 'Estimate',
            complaint     TEXT NOT NULL DEFAULT '',
            diagnosis     TEXT NOT NULL DEFAULT '',
            notes         TEXT NOT NULL DEFAULT '',
            tax_rate_bp   INTEGER NOT NULL DEFAULT 0,
            intake_on     TEXT NOT NULL,
            promised_on   TEXT NULL,
            completed_on  TEXT NULL,
            closed_on     TEXT NULL,
            created_utc   TEXT NOT NULL,
            updated_utc   TEXT NOT NULL
        );
        CREATE INDEX ix_tickets_customer ON tickets(customer_id);
        CREATE INDEX ix_tickets_status   ON tickets(status);
        CREATE INDEX ix_tickets_intake   ON tickets(intake_on);

        CREATE TABLE ticket_lines (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ticket_id   INTEGER NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            kind        TEXT NOT NULL DEFAULT 'Part',
            description TEXT NOT NULL DEFAULT '',
            qty_milli   INTEGER NOT NULL DEFAULT 1000,
            unit_cents  INTEGER NOT NULL DEFAULT 0,
            taxable     INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX ix_lines_ticket ON ticket_lines(ticket_id, sort_order);

        CREATE TABLE payments (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            customer_id  INTEGER NULL REFERENCES customers(id) ON DELETE SET NULL,
            ticket_id    INTEGER NULL REFERENCES tickets(id) ON DELETE SET NULL,
            amount_cents INTEGER NOT NULL,
            method       TEXT NOT NULL DEFAULT 'Cash',
            reference    TEXT NOT NULL DEFAULT '',
            note         TEXT NOT NULL DEFAULT '',
            paid_on      TEXT NOT NULL,
            created_utc  TEXT NOT NULL
        );
        CREATE INDEX ix_payments_paid_on ON payments(paid_on);
        CREATE INDEX ix_payments_ticket  ON payments(ticket_id);

        CREATE TABLE appointments (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            customer_id     INTEGER NULL REFERENCES customers(id) ON DELETE SET NULL,
            ticket_id       INTEGER NULL REFERENCES tickets(id) ON DELETE SET NULL,
            kind            TEXT NOT NULL DEFAULT 'Pickup',
            scheduled_local TEXT NOT NULL,
            duration_min    INTEGER NOT NULL DEFAULT 60,
            address         TEXT NOT NULL DEFAULT '',
            status          TEXT NOT NULL DEFAULT 'Scheduled',
            notes           TEXT NOT NULL DEFAULT '',
            created_utc     TEXT NOT NULL,
            updated_utc     TEXT NOT NULL
        );
        CREATE INDEX ix_appts_when ON appointments(scheduled_local);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- Pricing lives in these views so every screen totals a ticket the same way.
        -- Each line is rounded to whole cents before summing, matching TicketTotals.From in C#.
        CREATE VIEW ticket_line_calc AS
        SELECT
            tl.id,
            tl.ticket_id,
            tl.taxable,
            CASE WHEN tl.kind = 'Discount'
                 THEN -ABS(CAST(ROUND(tl.qty_milli * tl.unit_cents / 1000.0) AS INTEGER))
                 ELSE       CAST(ROUND(tl.qty_milli * tl.unit_cents / 1000.0) AS INTEGER)
            END AS total_cents
        FROM ticket_lines tl;

        CREATE VIEW ticket_totals AS
        SELECT
            tk.id AS ticket_id,
            COALESCE(SUM(c.total_cents), 0) AS subtotal_cents,
            COALESCE(SUM(CASE WHEN c.taxable = 1 THEN c.total_cents ELSE 0 END), 0) AS taxable_base_cents,
            CAST(ROUND(COALESCE(SUM(CASE WHEN c.taxable = 1 THEN c.total_cents ELSE 0 END), 0)
                       * tk.tax_rate_bp / 10000.0) AS INTEGER) AS tax_cents,
            COALESCE(SUM(c.total_cents), 0)
              + CAST(ROUND(COALESCE(SUM(CASE WHEN c.taxable = 1 THEN c.total_cents ELSE 0 END), 0)
                       * tk.tax_rate_bp / 10000.0) AS INTEGER) AS total_cents
        FROM tickets tk
        LEFT JOIN ticket_line_calc c ON c.ticket_id = tk.id
        GROUP BY tk.id;

        CREATE VIEW ticket_paid AS
        SELECT tk.id AS ticket_id,
               COALESCE(SUM(p.amount_cents), 0) AS paid_cents
        FROM tickets tk
        LEFT JOIN payments p ON p.ticket_id = tk.id
        GROUP BY tk.id;
        """
    };
}

/// <summary>Shop-wide preferences, stored as key/value so adding one never needs a migration.</summary>
public class SettingsStore
{
    private readonly Db _db;

    public SettingsStore(Db db) => _db = db;

    public const string ShopName = "shop.name";
    public const string ShopAddress = "shop.address";
    public const string ShopPhone = "shop.phone";
    public const string ShopEmail = "shop.email";
    public const string TaxRateBp = "tax.rate_bp";
    public const string LaborRateCents = "labor.rate_cents";
    public const string TicketPrefix = "ticket.prefix";
    public const string EstimateFooter = "estimate.footer";
    public const string WeekStartDay = "week.start_day";

    private static readonly Dictionary<string, string> Defaults = new()
    {
        [ShopName] = "My Repair Shop",
        [ShopAddress] = "",
        [ShopPhone] = "",
        [ShopEmail] = "",
        [TaxRateBp] = "0",
        [LaborRateCents] = "6500",
        [TicketPrefix] = "WD",
        [EstimateFooter] = "Estimate valid for 30 days. Parts and labor may change if additional problems are found.",
        [WeekStartDay] = "Monday"
    };

    /// <summary>Which day the shop's week rolls over on, used by the weekly takings report.</summary>
    public DayOfWeek GetWeekStart() =>
        Enum.TryParse<DayOfWeek>(Get(WeekStartDay), ignoreCase: true, out var d) ? d : DayOfWeek.Monday;

    public Dictionary<string, string> GetAll()
    {
        using var conn = _db.Open();
        var stored = conn.Query<(string Key, string Value)>("SELECT key, value FROM settings;")
                         .ToDictionary(r => r.Key, r => r.Value);

        var result = new Dictionary<string, string>(Defaults);
        foreach (var kv in stored) result[kv.Key] = kv.Value;
        return result;
    }

    public string Get(string key)
    {
        using var conn = _db.Open();
        var value = conn.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key;", new { key });
        return value ?? (Defaults.TryGetValue(key, out var d) ? d : "");
    }

    public int GetInt(string key)
    {
        var raw = Get(key);
        return int.TryParse(raw, out var n) ? n : 0;
    }

    public void Set(string key, string value)
    {
        using var conn = _db.Open();
        conn.Execute(
            "INSERT INTO settings(key, value) VALUES(@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new { key, value });
    }

    public void SetAll(IDictionary<string, string> values)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var kv in values)
        {
            conn.Execute(
                "INSERT INTO settings(key, value) VALUES(@key, @value) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = kv.Key, value = kv.Value }, tx);
        }
        tx.Commit();
    }
}
