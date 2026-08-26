using Dapper;

namespace WrenchDesk.Data;

/// <summary>Customers and the equipment they own.</summary>
public class CustomerRepo
{
    private readonly Db _db;

    public CustomerRepo(Db db) => _db = db;

    private static string NowUtc() => DateTime.UtcNow.ToString("O");

    public List<Customer> Search(string? term, bool includeArchived = false)
    {
        using var conn = _db.Open();

        var where = includeArchived ? "1=1" : "is_archived = 0";
        object param = new { };

        if (!string.IsNullOrWhiteSpace(term))
        {
            // Match on any of the fields the counter would actually search by.
            where += """
                 AND (first_name    LIKE @q
                   OR last_name     LIKE @q
                   OR business_name LIKE @q
                   OR phone         LIKE @q
                   OR phone_alt     LIKE @q
                   OR email         LIKE @q
                   OR address1      LIKE @q
                   OR city          LIKE @q
                   OR (first_name || ' ' || last_name) LIKE @q)
                """;
            param = new { q = $"%{term.Trim()}%" };
        }

        return conn.Query<Customer>(
            $"SELECT * FROM customers WHERE {where} ORDER BY last_name, first_name, business_name LIMIT 500;",
            param).ToList();
    }

    public Customer? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Customer>("SELECT * FROM customers WHERE id = @id;", new { id });
    }

    /// <summary>Lightweight list for dropdowns.</summary>
    public List<Customer> AllForPicker()
    {
        using var conn = _db.Open();
        return conn.Query<Customer>(
            "SELECT * FROM customers WHERE is_archived = 0 ORDER BY last_name, first_name, business_name;").ToList();
    }

    public long Insert(Customer c)
    {
        using var conn = _db.Open();
        c.CreatedUtc = c.UpdatedUtc = NowUtc();
        return conn.ExecuteScalar<long>("""
            INSERT INTO customers
                (first_name, last_name, business_name, phone, phone_alt, email,
                 address1, address2, city, state, zip, notes, is_archived, created_utc, updated_utc)
            VALUES
                (@FirstName, @LastName, @BusinessName, @Phone, @PhoneAlt, @Email,
                 @Address1, @Address2, @City, @State, @Zip, @Notes, @IsArchived, @CreatedUtc, @UpdatedUtc);
            SELECT last_insert_rowid();
            """, c);
    }

    public void Update(Customer c)
    {
        using var conn = _db.Open();
        c.UpdatedUtc = NowUtc();
        conn.Execute("""
            UPDATE customers SET
                first_name = @FirstName, last_name = @LastName, business_name = @BusinessName,
                phone = @Phone, phone_alt = @PhoneAlt, email = @Email,
                address1 = @Address1, address2 = @Address2, city = @City, state = @State, zip = @Zip,
                notes = @Notes, is_archived = @IsArchived, updated_utc = @UpdatedUtc
            WHERE id = @Id;
            """, c);
    }

    public void SetArchived(long id, bool archived)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE customers SET is_archived = @archived, updated_utc = @now WHERE id = @id;",
            new { id, archived, now = NowUtc() });
    }

    /// <summary>
    /// Hard delete, only allowed once nothing points at the customer. Returns a reason when it refuses,
    /// so the shop never loses a repair history by accident.
    /// </summary>
    public string? TryDelete(long id)
    {
        using var conn = _db.Open();
        var tickets = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tickets WHERE customer_id = @id;", new { id });
        if (tickets > 0)
            return $"This customer has {tickets} ticket(s). Archive them instead so the repair history is kept.";

        var payments = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM payments WHERE customer_id = @id;", new { id });
        if (payments > 0)
            return $"This customer has {payments} payment(s) on record. Archive them instead.";

        conn.Execute("DELETE FROM customers WHERE id = @id;", new { id });
        return null;
    }

    // ---- Equipment ----

    public List<Equipment> EquipmentFor(long customerId, bool includeArchived = false)
    {
        using var conn = _db.Open();
        var where = includeArchived ? "" : " AND is_archived = 0";
        return conn.Query<Equipment>(
            $"SELECT * FROM equipment WHERE customer_id = @customerId{where} ORDER BY is_archived, category, make, model;",
            new { customerId }).ToList();
    }

    public Equipment? GetEquipment(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<Equipment>("SELECT * FROM equipment WHERE id = @id;", new { id });
    }

    public long InsertEquipment(Equipment e)
    {
        using var conn = _db.Open();
        e.CreatedUtc = e.UpdatedUtc = NowUtc();
        return conn.ExecuteScalar<long>("""
            INSERT INTO equipment
                (customer_id, category, make, model, serial, engine_make, engine_model, engine_serial,
                 year, notes, is_archived, created_utc, updated_utc)
            VALUES
                (@CustomerId, @Category, @Make, @Model, @Serial, @EngineMake, @EngineModel, @EngineSerial,
                 @Year, @Notes, @IsArchived, @CreatedUtc, @UpdatedUtc);
            SELECT last_insert_rowid();
            """, e);
    }

    public void UpdateEquipment(Equipment e)
    {
        using var conn = _db.Open();
        e.UpdatedUtc = NowUtc();
        conn.Execute("""
            UPDATE equipment SET
                category = @Category, make = @Make, model = @Model, serial = @Serial,
                engine_make = @EngineMake, engine_model = @EngineModel, engine_serial = @EngineSerial,
                year = @Year, notes = @Notes, is_archived = @IsArchived, updated_utc = @UpdatedUtc
            WHERE id = @Id;
            """, e);
    }

    public void SetEquipmentArchived(long id, bool archived)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE equipment SET is_archived = @archived, updated_utc = @now WHERE id = @id;",
            new { id, archived, now = NowUtc() });
    }

    /// <summary>Lifetime billed and paid for one customer, for the header on their detail page.</summary>
    public (long BilledCents, long PaidCents, int TicketCount) LifetimeTotals(long customerId)
    {
        using var conn = _db.Open();

        // Declined estimates never became work, so they are not money the shop billed.
        var billed = conn.ExecuteScalar<long?>("""
            SELECT COALESCE(SUM(tt.total_cents), 0)
            FROM tickets tk
            JOIN ticket_totals tt ON tt.ticket_id = tk.id
            WHERE tk.customer_id = @customerId AND tk.status <> 'Declined';
            """, new { customerId }) ?? 0;

        var paid = conn.ExecuteScalar<long?>(
            "SELECT COALESCE(SUM(amount_cents), 0) FROM payments WHERE customer_id = @customerId;",
            new { customerId }) ?? 0;

        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM tickets WHERE customer_id = @customerId;", new { customerId });

        return (billed, paid, count);
    }
}
