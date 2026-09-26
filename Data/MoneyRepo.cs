using Dapper;

namespace WrenchDesk.Data;

/// <summary>
/// Payments taken in, plus the daily and weekly rollups. "Money brought in" means cash actually
/// received on a given day — not what was invoiced — because that is the number the shop counts up.
/// </summary>
public class MoneyRepo
{
    private readonly Db _db;
    private readonly SettingsStore _settings;

    public MoneyRepo(Db db, SettingsStore settings)
    {
        _db = db;
        _settings = settings;
    }

    private static string NowUtc() => DateTime.UtcNow.ToString("O");
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");

    /// <summary>
    /// The day a payment counts as takings on.
    ///
    /// The shop reckons a job's money to the week the work was finished, not the week the machine
    /// came in or the week a deposit happened to be handed over. So a payment against a ticket
    /// that has been completed counts on that completion date; anything else - a payment on a job
    /// still open, or money not attached to a ticket at all - counts on the day it was taken,
    /// because there is no completion date to use and the cash is real either way.
    ///
    /// Every dated money query goes through this one expression. Two of them working it out
    /// differently is how a dashboard and a report end up disagreeing about the same week.
    /// </summary>
    private const string RevenueDate = "COALESCE(NULLIF(TRIM(tk.completed_on), ''), p.paid_on)";

    /// <summary>The join every dated money query needs, since the date now depends on the ticket.</summary>
    private const string RevenueFrom = "FROM payments p LEFT JOIN tickets tk ON tk.id = p.ticket_id";

    private const string RowSelect = """
        SELECT p.id, p.amount_cents, p.method, p.reference, p.note, p.paid_on,
               COALESCE(NULLIF(TRIM(tk.completed_on), ''), p.paid_on) AS revenue_on,
               p.customer_id,
               TRIM(COALESCE(NULLIF(c.business_name, ''),
                             TRIM(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')))) AS customer_name,
               p.ticket_id,
               COALESCE(tk.number, '') AS ticket_number
        FROM payments p
        LEFT JOIN customers c ON c.id = p.customer_id
        LEFT JOIN tickets  tk ON tk.id = p.ticket_id
        """;

    public List<PaymentRow> InRange(DateTime from, DateTime to)
    {
        using var conn = _db.Open();
        return conn.Query<PaymentRow>(
            $"{RowSelect} WHERE {RevenueDate} BETWEEN @from AND @to ORDER BY {RevenueDate} DESC, p.id DESC;",
            new { from = Iso(from), to = Iso(to) }).ToList();
    }

    public List<PaymentRow> ForTicket(long ticketId)
    {
        using var conn = _db.Open();
        return conn.Query<PaymentRow>(
            $"{RowSelect} WHERE p.ticket_id = @ticketId ORDER BY p.paid_on DESC, p.id DESC;",
            new { ticketId }).ToList();
    }

    public List<PaymentRow> ForCustomer(long customerId)
    {
        using var conn = _db.Open();
        return conn.Query<PaymentRow>(
            $"{RowSelect} WHERE p.customer_id = @customerId ORDER BY p.paid_on DESC, p.id DESC;",
            new { customerId }).ToList();
    }

    public Payment? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Payment>("SELECT * FROM payments WHERE id = @id;", new { id });
    }

    public long Insert(Payment p)
    {
        using var conn = _db.Open();
        p.CreatedUtc = NowUtc();
        if (string.IsNullOrWhiteSpace(p.PaidOn)) p.PaidOn = Iso(DateTime.Now);

        return conn.ExecuteScalar<long>("""
            INSERT INTO payments (customer_id, ticket_id, amount_cents, method, reference, note, paid_on, created_utc)
            VALUES (@CustomerId, @TicketId, @AmountCents, @Method, @Reference, @Note, @PaidOn, @CreatedUtc);
            SELECT last_insert_rowid();
            """, p);
    }

    public void Update(Payment p)
    {
        using var conn = _db.Open();
        conn.Execute("""
            UPDATE payments SET
                customer_id = @CustomerId, ticket_id = @TicketId, amount_cents = @AmountCents,
                method = @Method, reference = @Reference, note = @Note, paid_on = @PaidOn
            WHERE id = @Id;
            """, p);
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM payments WHERE id = @id;", new { id });
    }

    public long TotalInRange(DateTime from, DateTime to)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long?>(
            $"SELECT COALESCE(SUM(p.amount_cents), 0) {RevenueFrom} WHERE {RevenueDate} BETWEEN @from AND @to;",
            new { from = Iso(from), to = Iso(to) }) ?? 0;
    }

    public long TotalForDay(DateTime day) => TotalInRange(day, day);

    /// <summary>Start of the week containing <paramref name="day"/>, honouring the shop's configured week start.</summary>
    public DateTime WeekStart(DateTime day)
    {
        var start = _settings.GetWeekStart();
        var delta = ((int)day.DayOfWeek - (int)start + 7) % 7;
        return day.Date.AddDays(-delta);
    }

    public (DateTime From, DateTime To) WeekRange(DateTime day)
    {
        var from = WeekStart(day);
        return (from, from.AddDays(6));
    }

    /// <summary>One bucket per calendar day across the range, including days with no takings.</summary>
    public List<MoneyBucket> DailyBuckets(DateTime from, DateTime to)
    {
        using var conn = _db.Open();
        var rows = conn.Query<(string PaidOn, long Total, int Count)>($"""
            SELECT {RevenueDate} AS revenue_on, COALESCE(SUM(p.amount_cents), 0) AS total, COUNT(*) AS count
            {RevenueFrom}
            WHERE {RevenueDate} BETWEEN @from AND @to
            GROUP BY {RevenueDate};
            """, new { from = Iso(from), to = Iso(to) })
            .ToDictionary(r => r.PaidOn, r => (r.Total, r.Count));

        var buckets = new List<MoneyBucket>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            var key = Iso(d);
            rows.TryGetValue(key, out var hit);
            buckets.Add(new MoneyBucket
            {
                Label = d.ToString("ddd MMM d"),
                StartOn = key,
                EndOn = key,
                TotalCents = hit.Total,
                PaymentCount = hit.Count
            });
        }
        return buckets;
    }

    /// <summary>One bucket per shop week, newest first — this is the weekly total the owner asked for.</summary>
    public List<MoneyBucket> WeeklyBuckets(int weeks, DateTime? endingOn = null)
    {
        var anchor = WeekStart(endingOn ?? DateTime.Now);
        var buckets = new List<MoneyBucket>();

        for (var i = 0; i < weeks; i++)
        {
            var start = anchor.AddDays(-7 * i);
            var end = start.AddDays(6);
            var payments = InRange(start, end);

            buckets.Add(new MoneyBucket
            {
                Label = $"{start:MMM d} – {end:MMM d}",
                StartOn = Iso(start),
                EndOn = Iso(end),
                TotalCents = payments.Sum(p => p.AmountCents),
                PaymentCount = payments.Count
            });
        }
        return buckets;
    }

    /// <summary>Split of a range by payment method, so the till can be reconciled against cash and checks.</summary>
    public List<(string Method, long TotalCents, int Count)> ByMethod(DateTime from, DateTime to)
    {
        using var conn = _db.Open();
        // Reconciling the till for a week has to cover the same payments that week's total does,
        // so this splits the same set by method rather than a set of its own.
        return conn.Query<(string Method, long TotalCents, int Count)>($"""
            SELECT p.method, COALESCE(SUM(p.amount_cents), 0) AS total_cents, COUNT(*) AS count
            {RevenueFrom}
            WHERE {RevenueDate} BETWEEN @from AND @to
            GROUP BY p.method
            ORDER BY total_cents DESC;
            """, new { from = Iso(from), to = Iso(to) }).ToList();
    }

    /// <summary>Total still owed across every ticket that is not declined.</summary>
    public long OutstandingCents()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long?>("""
            SELECT COALESCE(SUM(tt.total_cents - tp.paid_cents), 0)
            FROM tickets tk
            JOIN ticket_totals tt ON tt.ticket_id = tk.id
            JOIN ticket_paid   tp ON tp.ticket_id = tk.id
            WHERE tk.status <> 'Declined' AND tt.total_cents > tp.paid_cents;
            """) ?? 0;
    }

    /// <summary>Payments in a range as CSV, for a bookkeeper or a tax return.</summary>
    public string ExportCsv(DateTime from, DateTime to)
    {
        var rows = InRange(from, to);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Date,Paid On,Amount,Method,Customer,Ticket,Reference,Note");

        foreach (var r in rows.OrderBy(r => r.RevenueOn).ThenBy(r => r.Id))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                // Date is the day it counts as takings - the job's completion date where there is
                // one. Paid On is the day the money actually changed hands, so the two can be
                // told apart when they differ.
                Csv(r.RevenueOn),
                Csv(r.PaidOn),
                (r.AmountCents / 100m).ToString("0.00"),
                Csv(r.Method),
                Csv(r.CustomerName),
                Csv(r.TicketNumber),
                Csv(r.Reference),
                Csv(r.Note)
            }));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Characters a spreadsheet reads as the start of a formula rather than as text.
    /// The last two are tab and carriage return, which do the same thing.
    /// </summary>
    private static readonly char[] FormulaStarters = { '=', '+', '-', '@', (char)9, (char)13 };

    /// <summary>
    /// One field of the export.
    ///
    /// Excel and Google Sheets treat a cell beginning = + - or @ as a formula, and this file is
    /// written to be opened in exactly those. References and notes are free text typed at the
    /// counter, and anyone who can reach WrenchDesk on the shop wifi can type them, so anything
    /// that would be run rather than read gets a leading apostrophe. The cost is that a note
    /// genuinely starting with a dash shows that apostrophe in the spreadsheet; a bookkeeper
    /// seeing a stray mark beats one opening a file that does something.
    /// </summary>
    private static string Csv(string? value)
    {
        value ??= "";

        if (value.Length > 0 && FormulaStarters.Contains(value[0]))
            value = "'" + value;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
