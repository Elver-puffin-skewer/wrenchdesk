using Dapper;

namespace WrenchDesk.Data;

/// <summary>Customers and the equipment they own.</summary>
public class CustomerRepo
{
    private readonly Db _db;

    public CustomerRepo(Db db) => _db = db;

    private static string NowUtc() => DateTime.UtcNow.ToString("O");

    private const string PhoneMatchSql = @"
            SELECT * FROM customers
            WHERE is_archived = 0
              AND (
                   (LENGTH(digits_only(phone))     >= 10 AND SUBSTR(digits_only(phone),     -10) = @last10)
                OR (LENGTH(digits_only(phone_alt)) >= 10 AND SUBSTR(digits_only(phone_alt), -10) = @last10)
              )
            LIMIT 2;";

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

    /// <summary>
    /// What merging one customer into another would move, so it can be shown before anything
    /// happens rather than described afterwards.
    /// </summary>
    public (int Tickets, int Equipment, int Payments, int Appointments) MergePreview(long fromId)
    {
        using var conn = _db.Open();

        return (
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM tickets WHERE customer_id = @fromId;", new { fromId }),
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM equipment WHERE customer_id = @fromId;", new { fromId }),
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM payments WHERE customer_id = @fromId;", new { fromId }),
            conn.ExecuteScalar<int>("SELECT COUNT(*) FROM appointments WHERE customer_id = @fromId;", new { fromId }));
    }

    /// <summary>
    /// Folds a duplicate into the customer being kept: everything attached to it — machines,
    /// tickets, payments, stops — moves across, then the duplicate is removed.
    ///
    /// Entering the same person twice is easy to do at a counter, and once they have work against
    /// both records neither can simply be deleted without losing history. All of it happens in one
    /// transaction, so a failure half way leaves both records exactly as they were.
    /// </summary>
    public string? Merge(long fromId, long intoId)
    {
        if (fromId == intoId) return "That is the same customer.";

        using var conn = _db.Open();

        var from = conn.QuerySingleOrDefault<Customer>("SELECT * FROM customers WHERE id = @fromId;", new { fromId });
        var into = conn.QuerySingleOrDefault<Customer>("SELECT * FROM customers WHERE id = @intoId;", new { intoId });

        if (from is null) return "The duplicate no longer exists.";
        if (into is null) return "The customer to keep no longer exists.";

        using var tx = conn.BeginTransaction();

        conn.Execute("UPDATE equipment    SET customer_id = @intoId WHERE customer_id = @fromId;", new { fromId, intoId }, tx);
        conn.Execute("UPDATE tickets      SET customer_id = @intoId WHERE customer_id = @fromId;", new { fromId, intoId }, tx);
        conn.Execute("UPDATE payments     SET customer_id = @intoId WHERE customer_id = @fromId;", new { fromId, intoId }, tx);
        conn.Execute("UPDATE appointments SET customer_id = @intoId WHERE customer_id = @fromId;", new { fromId, intoId }, tx);

        // Anything filled in on the duplicate but blank on the survivor is worth keeping — it is
        // usually why the second record got made in the first place.
        conn.Execute("""
            UPDATE customers SET
                phone         = CASE WHEN TRIM(phone)         = '' THEN @Phone         ELSE phone         END,
                phone_alt     = CASE WHEN TRIM(phone_alt)     = '' THEN @PhoneAlt      ELSE phone_alt     END,
                email         = CASE WHEN TRIM(email)         = '' THEN @Email         ELSE email         END,
                address1      = CASE WHEN TRIM(address1)      = '' THEN @Address1      ELSE address1      END,
                address2      = CASE WHEN TRIM(address2)      = '' THEN @Address2      ELSE address2      END,
                city          = CASE WHEN TRIM(city)          = '' THEN @City          ELSE city          END,
                state         = CASE WHEN TRIM(state)         = '' THEN @State         ELSE state         END,
                zip           = CASE WHEN TRIM(zip)           = '' THEN @Zip           ELSE zip           END,
                business_name = CASE WHEN TRIM(business_name) = '' THEN @BusinessName  ELSE business_name END,
                notes         = TRIM(notes || CASE WHEN TRIM(@Notes) = '' THEN '' ELSE char(10) || @Notes END),
                updated_utc   = @now
            WHERE id = @intoId;
            """, new
        {
            from.Phone, from.PhoneAlt, from.Email, from.Address1, from.Address2,
            from.City, from.State, from.Zip, from.BusinessName, from.Notes,
            intoId, now = NowUtc()
        }, tx);

        conn.Execute("DELETE FROM customers WHERE id = @fromId;", new { fromId }, tx);
        tx.Commit();

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

    /// <summary>
    /// Finds the customer a calendar entry is about. Shops write these as "Bill Moore - 256-555-0142",
    /// so the phone number is the strongest signal and is tried first.
    ///
    /// Only ever returns a match when exactly one customer fits. Guessing wrong would attach a job
    /// to the wrong person's history, which is worse than leaving it unattached for someone to set.
    /// </summary>
    public Customer? MatchFromCalendarText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var digits in PhoneNumbersIn(text))
        {
            var byPhone = FindByPhone(digits);
            if (byPhone.Count == 1) return byPhone[0];
        }

        // No usable number: fall back to a whole-name match, which has to be unambiguous.
        var haystack = Normalise(text);
        if (haystack.Length == 0) return null;

        // Asking "does this entry mention you" has to be put to every customer, so this one
        // reads them all - but only the four fields the question needs, and with no ceiling on
        // how many. It used to borrow the search, whose LIMIT 500 quietly stopped matching
        // altogether for whoever sorted last once the books grew past that.
        var byName = NameCandidates()
            .Where(c =>
            {
                var full = Normalise($"{c.FirstName} {c.LastName}");
                var business = Normalise(c.BusinessName);

                return (full.Length >= 5 && haystack.Contains(full))
                    || (business.Length >= 4 && haystack.Contains(business));
            })
            .Take(2)
            .ToList();

        return byName.Count == 1 ? Get(byName[0].Id) : null;
    }

    /// <summary>Just enough of a customer to tell whether a calendar entry is naming them.</summary>
    private record NameCandidate(long Id, string FirstName, string LastName, string BusinessName);

    private List<NameCandidate> NameCandidates()
    {
        using var conn = _db.Open();
        return conn.Query<NameCandidate>(
            "SELECT id, first_name, last_name, business_name FROM customers WHERE is_archived = 0;")
            .ToList();
    }

    /// <summary>
    /// Customers reachable on a number, compared on the last ten digits so 256-555-0142 and
    /// (256) 555 0142 are the same number. Stops at two: the caller only needs to know whether
    /// exactly one person fits.
    /// </summary>
    private List<Customer> FindByPhone(string digits)
    {
        using var conn = _db.Open();
        return conn.Query<Customer>(PhoneMatchSql, new { last10 = digits[^10..] }).ToList();
    }

    /// <summary>Runs of 10 or 11 digits in the text, which is what a US phone number looks like once punctuation is stripped.</summary>
    private static IEnumerable<string> PhoneNumbersIn(string text)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"[\d][\d\-\.\s\(\)]{8,}[\d]"))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 10 and <= 11) yield return digits;
        }
    }

    private static string Normalise(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Trim();

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
