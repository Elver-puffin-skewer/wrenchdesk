using Dapper;

namespace WrenchDesk.Data;

/// <summary>One thing being rung up, before the sale is committed.</summary>
public class CounterLine
{
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "Part";
    public long UnitCents { get; set; }
    public long QtyMilli { get; set; } = 1000;
    public bool Taxable { get; set; } = true;

    public decimal Qty
    {
        get => QtyMilli / 1000m;
        set => QtyMilli = (long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero);
    }

    /// <summary>Same arithmetic as a ticket line, because it becomes one.</summary>
    public long TotalCents
    {
        get
        {
            var raw = (long)Math.Round(QtyMilli * UnitCents / 1000m, MidpointRounding.AwayFromZero);
            return Kind == "Discount" ? -Math.Abs(raw) : raw;
        }
    }
}

/// <summary>What a counter sale came to.</summary>
public record CounterSaleResult(long TicketId, string Number, long TotalCents);

/// <summary>
/// Selling something across the counter.
///
/// A counter sale is a ticket that took the short way round rather than a second kind of record.
/// It gets the same lines, the same totals, the same payment and the same place in the week's
/// takings - which is what keeps one answer to "what did this shop sell" when stock tracking and
/// year-end reporting arrive. What is different is the path through it: no customer to look up,
/// no machine, no status to walk, and nothing written down until the money is actually taken.
/// </summary>
public class CounterSaleRepo
{
    private readonly Db _db;
    private readonly TicketRepo _tickets;
    private readonly MoneyRepo _money;
    private readonly SettingsStore _settings;

    /// <summary>The stand-in every counter sale is filed against, so a walk-in is not a contact.</summary>
    public const string CounterCustomerName = "Counter Sale";

    public CounterSaleRepo(Db db, TicketRepo tickets, MoneyRepo money, SettingsStore settings)
    {
        _db = db;
        _tickets = tickets;
        _money = money;
        _settings = settings;
    }

    /// <summary>
    /// The customer every counter sale hangs off, made once and reused.
    ///
    /// A ticket has to have a customer - the column has said so since the first release and is
    /// joined in nearly every query in the app. One shared record is a great deal safer than
    /// making that nullable and chasing the consequences through every screen.
    /// </summary>
    public long CounterCustomerId()
    {
        using var conn = _db.Open();

        var existing = conn.ExecuteScalar<long?>(
            "SELECT id FROM customers WHERE business_name = @name COLLATE NOCASE LIMIT 1;",
            new { name = CounterCustomerName });

        if (existing is not null) return existing.Value;

        var now = DateTime.UtcNow.ToString("O");
        return conn.ExecuteScalar<long>("""
            INSERT INTO customers (business_name, notes, created_utc, updated_utc)
            VALUES (@name, @notes, @now, @now);
            SELECT last_insert_rowid();
            """,
            new
            {
                name = CounterCustomerName,
                notes = "Walk-in sales are filed against this record so they do not each need a "
                      + "customer of their own. It is made by WrenchDesk; leave it be.",
                now
            });
    }

    /// <summary>
    /// Rings up the sale: one ticket, its lines, the payment, closed and dated, all at once.
    ///
    /// Nothing is written until this is called, so a sale abandoned half way through leaves no
    /// half-ticket behind for somebody to find and wonder about later.
    /// </summary>
    public CounterSaleResult? Ring(IEnumerable<CounterLine> lines, string method,
        string? soldTo = null, string? by = null, string? reference = null)
    {
        var items = lines.Where(l => l.TotalCents != 0 || !string.IsNullOrWhiteSpace(l.Description)).ToList();
        if (items.Count == 0) return null;

        var taxEnabled = _settings.GetBool(SettingsStore.TaxEnabled);
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var ticketId = _tickets.Create(new Ticket
        {
            CustomerId = CounterCustomerId(),
            Status = TicketStatus.Closed,
            TaxRateBp = taxEnabled ? _settings.GetInt(SettingsStore.TaxRateBp) : 0,
            IntakeOn = today,
            CompletedOn = today,
            ClosedOn = today,
            IsCounterSale = true,
            CounterName = (soldTo ?? "").Trim()
        }, by);

        var order = 10;
        foreach (var line in items)
        {
            _tickets.AddLine(new TicketLine
            {
                TicketId = ticketId,
                Kind = line.Kind,
                Description = line.Description,
                QtyMilli = line.QtyMilli,
                UnitCents = line.UnitCents,
                Taxable = taxEnabled && line.Taxable,
                SortOrder = order
            });
            order += 10;
        }

        var totals = _tickets.Totals(ticketId);

        // Paid in full, there and then - that is what a counter sale is.
        _money.Insert(new Payment
        {
            CustomerId = CounterCustomerId(),
            TicketId = ticketId,
            AmountCents = totals.TotalCents,
            Method = method,
            Reference = (reference ?? "").Trim(),
            PaidOn = today
        }, by);

        var number = _tickets.Get(ticketId)?.Number ?? "";
        return new CounterSaleResult(ticketId, number, totals.TotalCents);
    }

    /// <summary>Counter sales taken on a given day, newest first - the till roll for the counter.</summary>
    public List<TicketRow> ForDay(DateTime day)
    {
        using var conn = _db.Open();
        return conn.Query<TicketRow>("""
            SELECT tk.id, tk.number, tk.status, tk.intake_on, tk.promised_on,
                   tk.created_by, tk.updated_by, tk.is_counter_sale,
                   tk.customer_id,
                   CASE WHEN TRIM(tk.counter_name) <> '' THEN tk.counter_name ELSE 'Counter sale' END AS customer_name,
                   NULL AS equipment_id, '' AS equipment_name, '' AS complaint,
                   0 AS machine_count,
                   tt.total_cents, tp.paid_cents
            FROM tickets tk
            JOIN ticket_totals tt ON tt.ticket_id = tk.id
            JOIN ticket_paid  tp ON tp.ticket_id = tk.id
            WHERE tk.is_counter_sale = 1 AND tk.intake_on = @day
            ORDER BY tk.id DESC;
            """, new { day = day.ToString("yyyy-MM-dd") }).ToList();
    }

    /// <summary>What the counter took on a given day, separate from repair work.</summary>
    public long TakenOn(DateTime day)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long?>("""
            SELECT COALESCE(SUM(p.amount_cents), 0)
            FROM payments p
            JOIN tickets tk ON tk.id = p.ticket_id
            WHERE tk.is_counter_sale = 1 AND p.paid_on = @day;
            """, new { day = day.ToString("yyyy-MM-dd") }) ?? 0;
    }
}
