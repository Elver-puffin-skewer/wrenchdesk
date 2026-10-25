using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Selling something across the counter. A counter sale is a ticket that took the short way
/// round, so it has to price, take payment and report exactly like one.
/// </summary>
public class CounterSaleTests
{
    [Fact]
    public void A_sale_becomes_one_closed_paid_ticket()
    {
        using var h = new TestDb();

        var result = h.Counter.Ring(
            new[] { Line("Air Filter", 1499), Line("Spark Plug", 499) }, "Cash", by: "Dale");

        Assert.NotNull(result);
        Assert.Equal(1998, result!.TotalCents);

        var ticket = h.Tickets.Get(result.TicketId)!;
        Assert.True(ticket.IsCounterSale);
        Assert.Equal(TicketStatus.Closed, ticket.Status);

        // The rules for closing a ticket apply here too: a completion date and money recorded.
        Assert.False(string.IsNullOrWhiteSpace(ticket.CompletedOn));
        Assert.Equal(1998, h.Tickets.PaidCents(result.TicketId));
    }

    [Fact]
    public void Nothing_is_written_until_the_money_is_taken()
    {
        using var h = new TestDb();

        // A sale somebody walked away from leaves no half-ticket behind to puzzle over.
        Assert.Null(h.Counter.Ring(Array.Empty<CounterLine>(), "Cash"));
        Assert.Empty(h.Tickets.Search(null, null, openOnly: false));
    }

    [Fact]
    public void Walk_ins_all_file_against_one_stand_in_customer()
    {
        using var h = new TestDb();

        h.Counter.Ring(new[] { Line("Air Filter", 1499) }, "Cash");
        h.Counter.Ring(new[] { Line("Deck Belt", 9000) }, "Card");

        // Two sales, one shared record - a walk-in does not become a contact to buy a filter.
        var counterCustomers = h.Customers.Search(CounterSaleRepo.CounterCustomerName, includeArchived: true);
        Assert.Single(counterCustomers);
        Assert.Equal(2, h.Tickets.ForCustomer(counterCustomers[0].Id).Count);
    }

    [Fact]
    public void A_name_can_go_on_the_receipt_without_making_a_customer()
    {
        using var h = new TestDb();

        var result = h.Counter.Ring(new[] { Line("Air Filter", 1499) }, "Cash", soldTo: "Bill Moore")!;

        Assert.Equal("Bill Moore", h.Tickets.Get(result.TicketId)!.CounterName);

        // Still only the one stand-in customer; Bill is not now a contact.
        Assert.Single(h.Customers.Search(null));
    }

    [Fact]
    public void The_money_lands_in_the_takings_for_today()
    {
        using var h = new TestDb();
        var today = DateTime.Today;

        h.Counter.Ring(new[] { Line("Air Filter", 1499) }, "Cash");

        // Completed and paid the same day, so both the old rule and the completion-date rule agree.
        Assert.Equal(1499, h.Money.TotalInRange(today, today));
        Assert.Equal(1499, h.Counter.TakenOn(today));
    }

    [Fact]
    public void Counter_takings_can_be_told_apart_from_repair_work()
    {
        using var h = new TestDb();
        var today = DateTime.Today;

        var customerId = h.NewCustomer();
        var repair = h.NewTicket(customerId);
        h.Money.Insert(new Payment
        {
            CustomerId = customerId, TicketId = repair,
            AmountCents = 20000, Method = "Check", PaidOn = today.ToString("yyyy-MM-dd")
        });

        h.Counter.Ring(new[] { Line("Air Filter", 1499) }, "Cash");

        Assert.Equal(21499, h.Money.TotalInRange(today, today));
        Assert.Equal(1499, h.Counter.TakenOn(today));
        Assert.Single(h.Counter.ForDay(today));
    }

    [Fact]
    public void Tax_follows_the_shops_setting_like_any_other_ticket()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.TaxEnabled, "true");
        h.Settings.Set(SettingsStore.TaxRateBp, "1000");

        var result = h.Counter.Ring(new[] { Line("Air Filter", 1000) }, "Cash")!;

        Assert.Equal(1100, result.TotalCents);
        Assert.Equal(1100, h.Tickets.PaidCents(result.TicketId));
    }

    [Fact]
    public void A_shop_that_does_not_charge_tax_is_not_charged_any()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.TaxEnabled, "false");
        h.Settings.Set(SettingsStore.TaxRateBp, "1000");

        var result = h.Counter.Ring(new[] { Line("Air Filter", 1000) }, "Cash")!;

        Assert.Equal(1000, result.TotalCents);
        Assert.Equal(0, h.Tickets.Get(result.TicketId)!.TaxRateBp);
    }

    [Fact]
    public void The_sale_records_who_rang_it_up()
    {
        using var h = new TestDb();

        var result = h.Counter.Ring(new[] { Line("Air Filter", 1499) }, "Cash", by: "Dale")!;

        Assert.Equal("Dale", h.Tickets.Get(result.TicketId)!.CreatedBy);
        Assert.Equal("Dale", h.Money.ForTicket(result.TicketId).Single().TakenBy);
    }

    [Fact]
    public void A_discount_on_the_counter_subtracts_the_way_it_does_on_a_ticket()
    {
        using var h = new TestDb();

        var result = h.Counter.Ring(new[]
        {
            Line("Deck Belt", 9000),
            new CounterLine { Description = "Goodwill", Kind = "Discount", UnitCents = 1000, Taxable = true }
        }, "Cash")!;

        Assert.Equal(8000, result.TotalCents);
    }

    [Fact]
    public void Quantity_is_carried_through_at_the_price_charged()
    {
        using var h = new TestDb();

        var line = Line("Oil - Quart", 799);
        line.Qty = 3;

        var result = h.Counter.Ring(new[] { line }, "Cash")!;

        Assert.Equal(2397, result.TotalCents);
    }

    private static CounterLine Line(string name, long cents) =>
        new() { Description = name, Kind = "Part", UnitCents = cents, Taxable = true };
}

/// <summary>Finding an item from whatever the scanner or the person typed.</summary>
public class CounterLookupTests
{
    [Fact]
    public void A_scanned_code_finds_its_item()
    {
        using var h = new TestDb();
        var item = h.QuickItems.All().First(i => i.Name == "Air Filter");
        item.Code = "0401234567";
        h.QuickItems.Update(item);

        Assert.Equal("Air Filter", h.QuickItems.FindForCounter("0401234567")?.Name);
    }

    [Fact]
    public void A_typed_name_finds_it_too()
    {
        using var h = new TestDb();

        Assert.Equal("Deck Belt", h.QuickItems.FindForCounter("deck belt")?.Name);
        Assert.Equal("Deck Belt", h.QuickItems.FindForCounter("DECK")?.Name);
    }

    [Fact]
    public void Something_that_fits_two_items_is_refused_rather_than_guessed()
    {
        using var h = new TestDb();

        // "Belt" is on the deck belt, the drive belt and the PTO belt. Ringing up the wrong part
        // is worse than asking the person to tap the one they meant.
        Assert.Null(h.QuickItems.FindForCounter("belt"));
    }

    [Fact]
    public void Nothing_matching_returns_nothing()
    {
        using var h = new TestDb();

        Assert.Null(h.QuickItems.FindForCounter("carburettor for a submarine"));
        Assert.Null(h.QuickItems.FindForCounter(""));
        Assert.Null(h.QuickItems.FindForCounter(null));
    }

    [Fact]
    public void An_item_switched_off_is_not_offered()
    {
        using var h = new TestDb();
        var item = h.QuickItems.All().First(i => i.Name == "Air Filter");
        item.IsActive = false;
        h.QuickItems.Update(item);

        Assert.Null(h.QuickItems.FindForCounter("Air Filter"));
    }
}
