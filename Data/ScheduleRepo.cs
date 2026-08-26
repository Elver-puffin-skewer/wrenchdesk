using Dapper;

namespace WrenchDesk.Data;

/// <summary>Pickups, deliveries and on-site jobs.</summary>
public class ScheduleRepo
{
    private readonly Db _db;

    public ScheduleRepo(Db db) => _db = db;

    private static string NowUtc() => DateTime.UtcNow.ToString("O");

    private const string RowSelect = """
        SELECT a.id, a.kind, a.scheduled_local, a.duration_min, a.address, a.status, a.notes,
               a.customer_id,
               TRIM(COALESCE(NULLIF(c.business_name, ''),
                             TRIM(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')))) AS customer_name,
               COALESCE(c.phone, '') AS customer_phone,
               a.ticket_id,
               COALESCE(tk.number, '') AS ticket_number
        FROM appointments a
        LEFT JOIN customers c ON c.id = a.customer_id
        LEFT JOIN tickets  tk ON tk.id = a.ticket_id
        """;

    /// <summary>Appointments between two dates inclusive. Compares on the date prefix of the stored local timestamp.</summary>
    public List<AppointmentRow> InRange(DateTime from, DateTime to)
    {
        using var conn = _db.Open();
        return conn.Query<AppointmentRow>($"""
            {RowSelect}
            WHERE SUBSTR(a.scheduled_local, 1, 10) BETWEEN @from AND @to
            ORDER BY a.scheduled_local;
            """, new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") }).ToList();
    }

    public List<AppointmentRow> ForDay(DateTime day) => InRange(day, day);

    /// <summary>Everything still scheduled from today forward — the default schedule view.</summary>
    public List<AppointmentRow> Upcoming(int days = 30)
    {
        using var conn = _db.Open();
        return conn.Query<AppointmentRow>($"""
            {RowSelect}
            WHERE a.status = 'Scheduled'
              AND SUBSTR(a.scheduled_local, 1, 10) BETWEEN @from AND @to
            ORDER BY a.scheduled_local;
            """, new
        {
            from = DateTime.Now.ToString("yyyy-MM-dd"),
            to = DateTime.Now.AddDays(days).ToString("yyyy-MM-dd")
        }).ToList();
    }

    public List<AppointmentRow> ForCustomer(long customerId)
    {
        using var conn = _db.Open();
        return conn.Query<AppointmentRow>(
            $"{RowSelect} WHERE a.customer_id = @customerId ORDER BY a.scheduled_local DESC;",
            new { customerId }).ToList();
    }

    public List<AppointmentRow> ForTicket(long ticketId)
    {
        using var conn = _db.Open();
        return conn.Query<AppointmentRow>(
            $"{RowSelect} WHERE a.ticket_id = @ticketId ORDER BY a.scheduled_local;",
            new { ticketId }).ToList();
    }

    public Appointment? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Appointment>("SELECT * FROM appointments WHERE id = @id;", new { id });
    }

    public AppointmentRow? GetRow(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<AppointmentRow>($"{RowSelect} WHERE a.id = @id;", new { id });
    }

    public long Insert(Appointment a)
    {
        using var conn = _db.Open();
        a.CreatedUtc = a.UpdatedUtc = NowUtc();
        return conn.ExecuteScalar<long>("""
            INSERT INTO appointments
                (customer_id, ticket_id, kind, scheduled_local, duration_min, address, status, notes, created_utc, updated_utc)
            VALUES
                (@CustomerId, @TicketId, @Kind, @ScheduledLocal, @DurationMin, @Address, @Status, @Notes, @CreatedUtc, @UpdatedUtc);
            SELECT last_insert_rowid();
            """, a);
    }

    public void Update(Appointment a)
    {
        using var conn = _db.Open();
        a.UpdatedUtc = NowUtc();
        conn.Execute("""
            UPDATE appointments SET
                customer_id = @CustomerId, ticket_id = @TicketId, kind = @Kind,
                scheduled_local = @ScheduledLocal, duration_min = @DurationMin,
                address = @Address, status = @Status, notes = @Notes, updated_utc = @UpdatedUtc
            WHERE id = @Id;
            """, a);
    }

    public void SetStatus(long id, string status)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE appointments SET status = @status, updated_utc = @now WHERE id = @id;",
            new { id, status, now = NowUtc() });
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM appointments WHERE id = @id;", new { id });
    }
}
