using Dapper;

namespace WrenchDesk.Data;

/// <summary>
/// The shop's own list of parts and jobs it does over and over, so a ticket line is a click
/// rather than a retype. A repair shop fits the same air filter fifty times a season.
/// </summary>
public class QuickItemRepo
{
    private readonly Db _db;

    public QuickItemRepo(Db db) => _db = db;

    /// <summary>What shows on a ticket, in the order the shop put them in.</summary>
    public List<QuickItem> Active()
    {
        using var conn = _db.Open();
        return conn.Query<QuickItem>(
            "SELECT * FROM quick_items WHERE is_active = 1 ORDER BY sort_order, id;").ToList();
    }

    /// <summary>Everything, including items switched off, for the management screen.</summary>
    public List<QuickItem> All()
    {
        using var conn = _db.Open();
        return conn.Query<QuickItem>(
            "SELECT * FROM quick_items ORDER BY is_active DESC, sort_order, id;").ToList();
    }

    public QuickItem? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<QuickItem>("SELECT * FROM quick_items WHERE id = @id;", new { id });
    }

    public long Insert(QuickItem item)
    {
        using var conn = _db.Open();

        if (item.SortOrder == 0)
            item.SortOrder = (conn.ExecuteScalar<int?>("SELECT MAX(sort_order) FROM quick_items;") ?? 0) + 10;

        return conn.ExecuteScalar<long>("""
            INSERT INTO quick_items (name, kind, default_cents, sort_order, is_active)
            VALUES (@Name, @Kind, @DefaultCents, @SortOrder, @IsActive);
            SELECT last_insert_rowid();
            """, item);
    }

    public void Update(QuickItem item)
    {
        using var conn = _db.Open();
        conn.Execute("""
            UPDATE quick_items SET
                name = @Name, kind = @Kind, default_cents = @DefaultCents,
                sort_order = @SortOrder, is_active = @IsActive
            WHERE id = @Id;
            """, item);
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        conn.Execute("DELETE FROM quick_items WHERE id = @id;", new { id });
    }

    /// <summary>
    /// Swaps an item with its neighbour so the shop can put what they use most at the front.
    /// Sort values are spaced by ten, so a swap is just an exchange of the two numbers.
    /// </summary>
    public void Move(long id, bool up)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var items = conn.Query<QuickItem>(
            "SELECT * FROM quick_items ORDER BY sort_order, id;", transaction: tx).ToList();

        var index = items.FindIndex(i => i.Id == id);
        if (index < 0) return;

        var swapWith = up ? index - 1 : index + 1;
        if (swapWith < 0 || swapWith >= items.Count) return;

        var a = items[index];
        var b = items[swapWith];

        // Equal sort values would leave the order down to the id tie-break, so give them distinct ones.
        var (first, second) = (Math.Min(a.SortOrder, b.SortOrder), Math.Max(a.SortOrder, b.SortOrder));
        if (first == second) second = first + 10;

        conn.Execute("UPDATE quick_items SET sort_order = @order WHERE id = @id;",
            new { order = up ? first : second, id = a.Id }, tx);
        conn.Execute("UPDATE quick_items SET sort_order = @order WHERE id = @id;",
            new { order = up ? second : first, id = b.Id }, tx);

        tx.Commit();
    }

    /// <summary>
    /// What to start a line at. A price set on the item wins; otherwise the last price this shop
    /// actually charged for it, so a part that has been fitted before comes back at its real price
    /// without anyone maintaining a price list.
    /// </summary>
    public long StartingPriceFor(QuickItem item)
    {
        if (item.DefaultCents > 0) return item.DefaultCents;

        using var conn = _db.Open();
        return conn.ExecuteScalar<long?>("""
            SELECT unit_cents FROM ticket_lines
            WHERE description = @name AND unit_cents > 0
            ORDER BY id DESC
            LIMIT 1;
            """, new { name = item.Name }) ?? 0;
    }
}
