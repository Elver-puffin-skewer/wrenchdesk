using Dapper;

namespace WrenchDesk.Data;

/// <summary>Pickups, deliveries and on-site jobs.</summary>
public class ScheduleRepo
{
    private readonly Db _db;

    public ScheduleRepo(Db db) => _db = db;

    private static string NowUtc() => DateTime.UtcNow.ToString("O");

    private const string RowSelect = """
        SELECT a.id, a.kind, a.title, a.is_all_day, a.scheduled_local, a.duration_min,
               a.address, a.status, a.notes,
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
                (customer_id, ticket_id, kind, title, is_all_day, scheduled_local, duration_min,
                 address, status, notes, created_utc, updated_utc)
            VALUES
                (@CustomerId, @TicketId, @Kind, @Title, @IsAllDay, @ScheduledLocal, @DurationMin,
                 @Address, @Status, @Notes, @CreatedUtc, @UpdatedUtc);
            SELECT last_insert_rowid();
            """, a);
    }

    public void Update(Appointment a)
    {
        using var conn = _db.Open();
        a.UpdatedUtc = NowUtc();
        conn.Execute("""
            UPDATE appointments SET
                customer_id = @CustomerId, ticket_id = @TicketId, kind = @Kind, title = @Title,
                is_all_day = @IsAllDay, scheduled_local = @ScheduledLocal, duration_min = @DurationMin,
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

    /// <summary>
    /// Deletes an appointment, leaving a tombstone when it had reached Google — otherwise the
    /// event would stay on the shop calendar with nothing left here to remove it.
    /// </summary>
    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var googleEventId = conn.ExecuteScalar<string?>(
            "SELECT google_event_id FROM appointments WHERE id = @id;", new { id }, tx);

        if (!string.IsNullOrWhiteSpace(googleEventId))
        {
            conn.Execute("""
                INSERT INTO google_tombstones (google_event_id, deleted_utc)
                VALUES (@googleEventId, @now)
                ON CONFLICT(google_event_id) DO UPDATE SET deleted_utc = excluded.deleted_utc;
                """, new { googleEventId, now = NowUtc() }, tx);
        }

        conn.Execute("DELETE FROM appointments WHERE id = @id;", new { id }, tx);
        tx.Commit();
    }

    // ---- Google Calendar sync support ----

    /// <summary>Appointments that need pushing: never synced, or edited here since the last push.</summary>
    public List<Appointment> NeedingPush()
    {
        using var conn = _db.Open();
        return conn.Query<Appointment>("""
            SELECT * FROM appointments
            WHERE google_event_id = '' OR google_synced_utc <> updated_utc
            ORDER BY id;
            """).ToList();
    }

    public Appointment? GetByGoogleEventId(string googleEventId)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Appointment>(
            "SELECT * FROM appointments WHERE google_event_id = @googleEventId;", new { googleEventId });
    }

    /// <summary>Records the result of a successful push or pull without disturbing the shop's own edits.</summary>
    public void MarkSynced(long id, string googleEventId, string googleUpdated)
    {
        using var conn = _db.Open();
        conn.Execute("""
            UPDATE appointments
            SET google_event_id   = @googleEventId,
                google_updated    = @googleUpdated,
                google_synced_utc = updated_utc
            WHERE id = @id;
            """, new { id, googleEventId, googleUpdated });
    }

    /// <summary>
    /// Applies values that came from Google. Bumps updated_utc and immediately marks it synced,
    /// so a pulled change is not mistaken for a local edit and pushed straight back.
    /// </summary>
    public void ApplyFromGoogle(Appointment appointment, string googleUpdated)
    {
        using var conn = _db.Open();
        var now = NowUtc();

        conn.Execute("""
            UPDATE appointments SET
                kind = @Kind, title = @Title, is_all_day = @IsAllDay,
                scheduled_local = @ScheduledLocal, duration_min = @DurationMin,
                address = @Address, status = @Status, notes = @Notes,
                customer_id = @CustomerId,
                updated_utc = @now, google_synced_utc = @now, google_updated = @googleUpdated
            WHERE id = @Id;
            """, new
        {
            appointment.Id,
            appointment.Kind,
            appointment.Title,
            appointment.IsAllDay,
            appointment.ScheduledLocal,
            appointment.DurationMin,
            appointment.Address,
            appointment.Status,
            appointment.Notes,
            appointment.CustomerId,
            now,
            googleUpdated
        });
    }

    /// <summary>Inserts an appointment that originated in Google, already marked as in step with it.</summary>
    public long InsertFromGoogle(Appointment appointment, string googleUpdated)
    {
        using var conn = _db.Open();
        var now = NowUtc();

        return conn.ExecuteScalar<long>("""
            INSERT INTO appointments
                (customer_id, ticket_id, kind, title, is_all_day, scheduled_local, duration_min,
                 address, status, notes,
                 created_utc, updated_utc, google_event_id, google_synced_utc, google_updated)
            VALUES
                (@CustomerId, @TicketId, @Kind, @Title, @IsAllDay, @ScheduledLocal, @DurationMin,
                 @Address, @Status, @Notes,
                 @now, @now, @GoogleEventId, @now, @googleUpdated);
            SELECT last_insert_rowid();
            """, new
        {
            appointment.CustomerId,
            appointment.TicketId,
            appointment.Kind,
            appointment.Title,
            appointment.IsAllDay,
            appointment.ScheduledLocal,
            appointment.DurationMin,
            appointment.Address,
            appointment.Status,
            appointment.Notes,
            appointment.GoogleEventId,
            now,
            googleUpdated
        });
    }

    /// <summary>Removes an appointment because its Google event is gone — no tombstone, Google already knows.</summary>
    public void DeleteFromGoogle(long id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM appointments WHERE id = @id;", new { id });
    }

    public List<(string GoogleEventId, string DeletedUtc)> Tombstones()
    {
        using var conn = _db.Open();
        return conn.Query<(string GoogleEventId, string DeletedUtc)>(
            "SELECT google_event_id, deleted_utc FROM google_tombstones ORDER BY deleted_utc;").ToList();
    }

    public void ClearTombstone(string googleEventId)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM google_tombstones WHERE google_event_id = @googleEventId;", new { googleEventId });
    }

    /// <summary>Forgets every Google link, so a reconnect to a different calendar starts clean.</summary>
    public void ClearAllGoogleLinks()
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("UPDATE appointments SET google_event_id = '', google_synced_utc = '', google_updated = '';", transaction: tx);
        conn.Execute("DELETE FROM google_tombstones;", transaction: tx);
        tx.Commit();
    }
}
