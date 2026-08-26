using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Pricing is computed twice — in C# for the ticket screen, and in the ticket_totals SQL view for
/// list screens and reports. If those two ever disagree, the shop sees one number on the ticket and
/// a different one on the invoice, so these tests pin them together.
/// </summary>
public class PricingTests
{
    [Fact]
    public void Line_total_multiplies_quantity_by_unit_price()
    {
        var line = new TicketLine { Kind = "Part", UnitCents = 1250 };
        line.Qty = 3;

        Assert.Equal(3750, line.TotalCents);
    }

    [Fact]
    public void Fractional_labor_hours_do_not_drift()
    {
        // 1.5 hours at $65/hr — the classic case that breaks with floating point.
        var line = new TicketLine { Kind = "Labor", UnitCents = 6500 };
        line.Qty = 1.5m;

        Assert.Equal(1500, line.QtyMilli);
        Assert.Equal(9750, line.TotalCents);
    }

    [Fact]
    public void Discount_always_subtracts_even_when_entered_positive()
    {
        var line = new TicketLine { Kind = "Discount", UnitCents = 1000 };
        line.Qty = 1;

        Assert.Equal(-1000, line.TotalCents);
    }

    [Fact]
    public void Discount_entered_negative_does_not_double_negate()
    {
        var line = new TicketLine { Kind = "Discount", UnitCents = -1000 };
        line.Qty = 1;

        Assert.Equal(-1000, line.TotalCents);
    }

    [Fact]
    public void Tax_applies_only_to_taxable_lines()
    {
        var lines = new List<TicketLine>
        {
            MakeLine("Part", qty: 1, unitCents: 10000, taxable: true),
            MakeLine("Labor", qty: 1, unitCents: 5000, taxable: false)
        };

        var totals = TicketTotals.From(lines, taxRateBp: 725, paidCents: 0);

        Assert.Equal(15000, totals.SubtotalCents);
        Assert.Equal(10000, totals.TaxableBaseCents);
        Assert.Equal(725, totals.TaxCents);
        Assert.Equal(15725, totals.TotalCents);
    }

    [Fact]
    public void Taxable_discount_reduces_the_tax_base()
    {
        var lines = new List<TicketLine>
        {
            MakeLine("Part", qty: 1, unitCents: 10000, taxable: true),
            MakeLine("Discount", qty: 1, unitCents: 2000, taxable: true)
        };

        var totals = TicketTotals.From(lines, taxRateBp: 1000, paidCents: 0);

        Assert.Equal(8000, totals.SubtotalCents);
        Assert.Equal(8000, totals.TaxableBaseCents);
        Assert.Equal(800, totals.TaxCents);
        Assert.Equal(8800, totals.TotalCents);
    }

    [Fact]
    public void Balance_is_total_minus_payments()
    {
        var lines = new List<TicketLine> { MakeLine("Part", 1, 5000, taxable: false) };
        var totals = TicketTotals.From(lines, taxRateBp: 0, paidCents: 2000);

        Assert.Equal(3000, totals.BalanceCents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(725)]
    [InlineData(1000)]
    public void Sql_view_totals_match_the_csharp_totals(int taxRateBp)
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.NewTicket(customerId, taxRateBp);

        h.AddLine(ticketId, "Labor", qty: 1.5m, each: 65m, taxable: false);
        h.AddLine(ticketId, "Part", qty: 2, each: 12.99m, taxable: true);
        h.AddLine(ticketId, "Fee", qty: 1, each: 7.50m, taxable: true);
        h.AddLine(ticketId, "Discount", qty: 1, each: 10m, taxable: true);

        var fromCSharp = TicketTotals.From(h.Tickets.Lines(ticketId), taxRateBp, paidCents: 0);

        // The list screens read the SQL view, so compare against what a row query returns.
        var row = h.Tickets.ForCustomer(customerId).Single();

        Assert.Equal(fromCSharp.TotalCents, row.TotalCents);
    }

    [Fact]
    public void Rounding_matches_between_sql_and_csharp_on_awkward_cents()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        // 8.875% on $19.99 lands on a half-cent, which is where the two engines could diverge.
        var ticketId = h.NewTicket(customerId, taxRateBp: 888);
        h.AddLine(ticketId, "Part", qty: 3, each: 19.99m, taxable: true);

        var fromCSharp = TicketTotals.From(h.Tickets.Lines(ticketId), 888, 0);
        var row = h.Tickets.ForCustomer(customerId).Single();

        Assert.Equal(fromCSharp.TotalCents, row.TotalCents);
    }

    private static TicketLine MakeLine(string kind, decimal qty, long unitCents, bool taxable)
    {
        var line = new TicketLine { Kind = kind, UnitCents = unitCents, Taxable = taxable };
        line.Qty = qty;
        return line;
    }
}

public class MoneyParsingTests
{
    [Theory]
    [InlineData("45", 4500)]
    [InlineData("45.50", 4550)]
    [InlineData("$45.50", 4550)]
    [InlineData("1,299.99", 129999)]
    [InlineData("  85  ", 8500)]
    [InlineData("0.05", 5)]
    public void Parses_the_ways_people_actually_type_money(string input, long expected)
    {
        Assert.True(Money.TryParse(input, out var cents));
        Assert.Equal(expected, cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Rejects_junk(string? input)
    {
        Assert.False(Money.TryParse(input, out _));
    }

    [Fact]
    public void Formats_as_us_currency()
    {
        Assert.Equal("$1,299.99", Money.Fmt(129999));
        Assert.Equal("$0.00", Money.Fmt(0));
    }
}
