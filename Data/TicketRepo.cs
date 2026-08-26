using Dapper;

namespace WrenchDesk.Data;

/// <summary>
/// Tickets are the single record for a job. One starts life as an Estimate and moves through
/// the shop by changing status, so the estimate and the finished repair stay the same record
/// and the customer's history reads as one story.
/// </summary>
public class TicketRepo
{
    private readonly Db _db;
    private readonly SettingsStore _settings;

    public TicketRepo(Db db, SettingsStore settings)
    {
        _db = db;
        _settings = settings;
    }

    private static string NowUtc() => DateTime.UtcNow.ToString("O");
    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private const string RowSelect = """
        SELECT tk.id, tk.number, tk.status, tk.complaint, tk.intake_on, tk.promised_on,
               tk.customer_id, tk.equipment_id,
               TRIM(COALESCE(NULLIF(c.business_name, ''),
                             TRIM(c.first_name || ' ' || c.last_name))) AS customer_name,
               TRIM(COALESCE(e.year, '') || ' ' || COALESCE(e.make, '') || ' ' || COALESCE(e.model, '')) AS equipment_name,
               tt.total_cents,
               tp.paid_cents
        FROM tickets tk
        JOIN customers c       ON c.id = tk.customer_id
        LEFT JOIN equipment e  ON e.id = tk.equipment_id
        JOIN ticket_totals tt  ON tt.ticket_id = tk.id
        JOIN ticket_paid tp    ON tp.ticket_id = tk.id
        """;

    public List<TicketRow> Search(string? term, string? status, bool openOnly)
    {
        using var conn = _db.Open();

        var clauses = new List<string>();
        var param = new DynamicParameters();

        if (openOnly)
            clauses.Add($"tk.status IN ({string.Join(",", TicketStatus.Open.Select(s => $"'{s}'"))})");

        if (!string.IsNullOrWhiteSpace(status))
        {
            clauses.Add("tk.status = @status");
            param.Add("status", status);
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            clauses.Add("""
                (tk.number LIKE @q OR tk.complaint LIKE @q OR tk.diagnosis LIKE @q
                 OR c.first_name LIKE @q OR c.last_name LIKE @q OR c.business_name LIKE @q
                 OR c.phone LIKE @q
                 OR e.make LIKE @q OR e.model LIKE @q OR e.serial LIKE @q)
                """);
            param.Add("q", $"%{term.Trim()}%");
        }

        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        return conn.Query<TicketRow>($"{RowSelect} {where} ORDER BY tk.id DESC LIMIT 500;", param).ToList();
    }

    public List<TicketRow> ForCustomer(long customerId)
    {
        using var conn = _db.Open();
        return conn.Query<TicketRow>($"{RowSelect} WHERE tk.customer_id = @customerId ORDER BY tk.id DESC;",
            new { customerId }).ToList();
    }

    public List<TicketRow> ForEquipment(long equipmentId)
    {
        using var conn = _db.Open();
        return conn.Query<TicketRow>($"{RowSelect} WHERE tk.equipment_id = @equipmentId ORDER BY tk.id DESC;",
            new { equipmentId }).ToList();
    }

    public List<TicketRow> OpenBoard()
    {
        using var conn = _db.Open();
        var inList = string.Join(",", TicketStatus.Open.Select(s => $"'{s}'"));
        return conn.Query<TicketRow>($"{RowSelect} WHERE tk.status IN ({inList}) ORDER BY tk.id DESC;").ToList();
    }

    public Ticket? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Ticket>("SELECT * FROM tickets WHERE id = @id;", new { id });
    }

    public List<TicketLine> Lines(long ticketId)
    {
        using var conn = _db.Open();
        return conn.Query<TicketLine>(
            "SELECT * FROM ticket_lines WHERE ticket_id = @ticketId ORDER BY sort_order, id;",
            new { ticketId }).ToList();
    }

    public long PaidCents(long ticketId)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long?>(
            "SELECT COALESCE(SUM(amount_cents), 0) FROM payments WHERE ticket_id = @ticketId;",
            new { ticketId }) ?? 0;
    }

    public TicketTotals Totals(long ticketId)
    {
        var ticket = Get(ticketId);
        if (ticket is null) return new TicketTotals();
        return TicketTotals.From(Lines(ticketId), ticket.TaxRateBp, PaidCents(ticketId));
    }

    /// <summary>
    /// Creates a ticket, allocating the next number inside the same transaction so two
    /// browser tabs writing at once cannot land on the same number.
    /// </summary>
    public long Create(Ticket t)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var prefix = _settings.Get(SettingsStore.TicketPrefix);
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "WD";

        // Highest numeric suffix already used with this prefix, so numbering survives edits and deletes.
        var highest = conn.ExecuteScalar<long?>("""
            SELECT MAX(CAST(SUBSTR(number, LENGTH(@prefix) + 2) AS INTEGER))
            FROM tickets
            WHERE number LIKE @prefix || '-%';
            """, new { prefix }, tx) ?? 1000;

        t.Number = $"{prefix}-{highest + 1}";
        t.CreatedUtc = t.UpdatedUtc = NowUtc();
        if (string.IsNullOrWhiteSpace(t.IntakeOn)) t.IntakeOn = Today();

        var id = conn.ExecuteScalar<long>("""
            INSERT INTO tickets
                (number, customer_id, equipment_id, status, complaint, diagnosis, notes,
                 tax_rate_bp, intake_on, promised_on, completed_on, closed_on, created_utc, updated_utc)
            VALUES
                (@Number, @CustomerId, @EquipmentId, @Status, @Complaint, @Diagnosis, @Notes,
                 @TaxRateBp, @IntakeOn, @PromisedOn, @CompletedOn, @ClosedOn, @CreatedUtc, @UpdatedUtc);
            SELECT last_insert_rowid();
            """, t, tx);

        tx.Commit();
        return id;
    }

    public void Update(Ticket t)
    {
        using var conn = _db.Open();
        t.UpdatedUtc = NowUtc();
        conn.Execute("""
            UPDATE tickets SET
                customer_id = @CustomerId, equipment_id = @EquipmentId, status = @Status,
                complaint = @Complaint, diagnosis = @Diagnosis, notes = @Notes,
                tax_rate_bp = @TaxRateBp, intake_on = @IntakeOn, promised_on = @PromisedOn,
                completed_on = @CompletedOn, closed_on = @ClosedOn, updated_utc = @UpdatedUtc
            WHERE id = @Id;
            """, t);
    }

    /// <summary>Moves a ticket's status and stamps the matching date so reports have something to sort on.</summary>
    public void SetStatus(long id, string status)
    {
        using var conn = _db.Open();
        var today = Today();

        var completedOn = status is TicketStatus.Ready or TicketStatus.Closed ? today : null;
        var closedOn = status is TicketStatus.Closed or TicketStatus.Declined ? today : null;

        conn.Execute("""
            UPDATE tickets SET
                status       = @status,
                completed_on = CASE WHEN @completedOn IS NOT NULL AND completed_on IS NULL
                                    THEN @completedOn ELSE completed_on END,
                closed_on    = CASE WHEN @closedOn IS NOT NULL THEN @closedOn ELSE closed_on END,
                updated_utc  = @now
            WHERE id = @id;
            """, new { id, status, completedOn, closedOn, now = NowUtc() });
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM tickets WHERE id = @id;", new { id });
    }

    // ---- Line items ----

    public long AddLine(TicketLine line)
    {
        using var conn = _db.Open();
        if (line.SortOrder == 0)
        {
            line.SortOrder = (conn.ExecuteScalar<int?>(
                "SELECT MAX(sort_order) FROM ticket_lines WHERE ticket_id = @TicketId;", line) ?? 0) + 10;
        }

        return conn.ExecuteScalar<long>("""
            INSERT INTO ticket_lines (ticket_id, sort_order, kind, description, qty_milli, unit_cents, taxable)
            VALUES (@TicketId, @SortOrder, @Kind, @Description, @QtyMilli, @UnitCents, @Taxable);
            SELECT last_insert_rowid();
            """, line);
    }

    public void UpdateLine(TicketLine line)
    {
        using var conn = _db.Open();
        conn.Execute("""
            UPDATE ticket_lines SET
                sort_order = @SortOrder, kind = @Kind, description = @Description,
                qty_milli = @QtyMilli, unit_cents = @UnitCents, taxable = @Taxable
            WHERE id = @Id;
            """, line);
    }

    public void DeleteLine(long lineId)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM ticket_lines WHERE id = @lineId;", new { lineId });
    }

    /// <summary>
    /// Copies a ticket into a fresh Estimate — handy when a customer brings the same machine
    /// back for the same job, or wants a previous quote re-issued.
    /// </summary>
    public long Duplicate(long sourceId)
    {
        var source = Get(sourceId) ?? throw new InvalidOperationException($"Ticket {sourceId} not found.");

        var copy = new Ticket
        {
            CustomerId = source.CustomerId,
            EquipmentId = source.EquipmentId,
            Status = TicketStatus.Estimate,
            Complaint = source.Complaint,
            Diagnosis = source.Diagnosis,
            Notes = source.Notes,
            TaxRateBp = source.TaxRateBp,
            IntakeOn = Today()
        };

        var newId = Create(copy);
        foreach (var line in Lines(sourceId))
        {
            line.TicketId = newId;
            AddLine(line);
        }
        return newId;
    }

    /// <summary>Counts per status, for the dashboard tiles.</summary>
    public Dictionary<string, int> StatusCounts()
    {
        using var conn = _db.Open();
        return conn.Query<(string Status, int Count)>(
            "SELECT status, COUNT(*) AS count FROM tickets GROUP BY status;")
            .ToDictionary(r => r.Status, r => r.Count);
    }

    /// <summary>Tickets that are finished but still owe money — the shop's receivables.</summary>
    public List<TicketRow> Unpaid()
    {
        using var conn = _db.Open();
        return conn.Query<TicketRow>($"""
            {RowSelect}
            WHERE tk.status <> 'Declined' AND tt.total_cents > tp.paid_cents
            ORDER BY tk.id DESC;
            """).ToList();
    }
}
