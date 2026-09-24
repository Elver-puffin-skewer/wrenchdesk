using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Reordering the lines on a ticket, so the invoice reads in the order the work happened rather
/// than the order somebody happened to type it in.
/// </summary>
public class LineOrderTests
{
    [Fact]
    public void A_line_can_be_moved_up_and_back_down()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        h.AddLine(ticketId, "Part", 1, 10m, true, "Deck Belt");
        h.AddLine(ticketId, "Part", 1, 20m, true, "Spindle Assembly");
        h.AddLine(ticketId, "Labor", 1, 90m, false, "Carb rebuild");

        var spindle = h.Tickets.Lines(ticketId)[1];
        h.Tickets.MoveLine(spindle.Id, up: true);

        Assert.Equal(new[] { "Spindle Assembly", "Deck Belt", "Carb rebuild" }, Descriptions(h, ticketId));

        h.Tickets.MoveLine(spindle.Id, up: false);
        Assert.Equal(new[] { "Deck Belt", "Spindle Assembly", "Carb rebuild" }, Descriptions(h, ticketId));
    }

    [Fact]
    public void Every_kind_of_line_can_be_moved_not_just_parts()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        h.AddLine(ticketId, "Part", 1, 10m, true, "Blade");
        h.AddLine(ticketId, "Discount", 1, 5m, true, "Goodwill");
        h.AddLine(ticketId, "Fee", 1, 100m, true, "Welding");

        var fee = h.Tickets.Lines(ticketId)[2];
        h.Tickets.MoveLine(fee.Id, up: true);
        Assert.Equal(new[] { "Blade", "Welding", "Goodwill" }, Descriptions(h, ticketId));

        var discount = h.Tickets.Lines(ticketId).Single(l => l.Kind == "Discount");
        h.Tickets.MoveLine(discount.Id, up: true);
        Assert.Equal(new[] { "Blade", "Goodwill", "Welding" }, Descriptions(h, ticketId));
    }

    [Fact]
    public void Moving_past_either_end_does_nothing()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        h.AddLine(ticketId, "Part", 1, 10m, true, "First");
        h.AddLine(ticketId, "Part", 1, 20m, true, "Last");

        var lines = h.Tickets.Lines(ticketId);
        h.Tickets.MoveLine(lines[0].Id, up: true);
        h.Tickets.MoveLine(lines[1].Id, up: false);

        Assert.Equal(new[] { "First", "Last" }, Descriptions(h, ticketId));
    }

    [Fact]
    public void Lines_that_share_a_sort_value_still_swap()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        // Older tickets can hold lines that were never given spaced sort values.
        foreach (var name in new[] { "A", "B" })
        {
            var line = new TicketLine { TicketId = ticketId, Kind = "Part", Description = name, UnitCents = 100, SortOrder = 5 };
            line.Qty = 1;
            h.Tickets.AddLine(line);
        }

        var second = h.Tickets.Lines(ticketId)[1];
        h.Tickets.MoveLine(second.Id, up: true);

        Assert.Equal(new[] { "B", "A" }, Descriptions(h, ticketId));
    }

    [Fact]
    public void Moving_a_line_changes_nothing_about_the_money()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer(), taxRateBp: 888);

        h.AddLine(ticketId, "Part", 2, 12.99m, true, "Filter");
        h.AddLine(ticketId, "Labor", 1.5m, 65m, false, "Rebuild");

        var before = h.Tickets.Totals(ticketId);
        h.Tickets.MoveLine(h.Tickets.Lines(ticketId)[1].Id, up: true);
        var after = h.Tickets.Totals(ticketId);

        Assert.Equal(before.SubtotalCents, after.SubtotalCents);
        Assert.Equal(before.TaxCents, after.TaxCents);
        Assert.Equal(before.TotalCents, after.TotalCents);
    }

    private static string[] Descriptions(TestDb h, long ticketId) =>
        h.Tickets.Lines(ticketId).Select(l => l.Description).ToArray();
}

/// <summary>
/// Whether the shop is asked about sales tax at all. Off has to mean off on new work, without
/// disturbing what a finished invoice already says.
/// </summary>
public class TaxToggleTests
{
    [Fact]
    public void Charging_tax_is_on_unless_a_shop_turns_it_off()
    {
        using var h = new TestDb();

        // Nothing changes for a shop that already charges tax and never visits the setting.
        Assert.True(h.Settings.GetBool(SettingsStore.TaxEnabled));
    }

    [Fact]
    public void Turning_it_off_leaves_the_rate_on_file_for_turning_it_back_on()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.TaxRateBp, "888");
        h.Settings.Set(SettingsStore.TaxEnabled, "false");

        Assert.False(h.Settings.GetBool(SettingsStore.TaxEnabled));
        Assert.Equal(888, h.Settings.GetInt(SettingsStore.TaxRateBp));
    }

    [Fact]
    public void A_ticket_written_while_tax_was_on_keeps_its_tax_afterwards()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer(), taxRateBp: 888);
        h.AddLine(ticketId, "Part", 1, 100m, taxable: true, description: "Carburettor");

        var taxed = h.Tickets.Totals(ticketId);
        Assert.Equal(888, taxed.TaxCents);

        // The shop decides it does not charge tax after all. The finished ticket must still add up.
        h.Settings.Set(SettingsStore.TaxEnabled, "false");

        var after = h.Tickets.Totals(ticketId);
        Assert.Equal(888, after.TaxCents);
        Assert.Equal(10888, after.TotalCents);
        Assert.Equal(888, h.Tickets.Get(ticketId)!.TaxRateBp);
    }
}

/// <summary>
/// What a line says on the customer's copy. The kind is the shop's own filing decision and has no
/// business appearing on an invoice next to words the shop chose itself.
/// </summary>
public class PrintLabelTests
{
    [Theory]
    [InlineData("Fee", "Welding", "Welding")]
    [InlineData("Part", "Shoulder Spacer", "Shoulder Spacer")]
    [InlineData("Labor", "Carb rebuild", "Carb rebuild")]
    [InlineData("Discount", "Goodwill", "Goodwill")]
    public void A_line_the_shop_described_prints_those_words_and_nothing_else(
        string kind, string description, string expected)
    {
        var line = new TicketLine { Kind = kind, Description = description };

        // Used to come out as "Welding (Fee)".
        Assert.Equal(expected, line.PrintLabel);
    }

    [Theory]
    [InlineData("Labor", "Labor")]
    [InlineData("Fee", "Fee")]
    [InlineData("Part", "Part")]
    public void A_line_left_blank_falls_back_to_its_kind_once(string kind, string expected)
    {
        var line = new TicketLine { Kind = kind, Description = "" };

        // Used to come out as "Labor (Labor)".
        Assert.Equal(expected, line.PrintLabel);
    }

    [Fact]
    public void Stray_spacing_around_a_description_does_not_reach_the_invoice()
    {
        Assert.Equal("Welding", new TicketLine { Kind = "Fee", Description = "  Welding  " }.PrintLabel);
        Assert.Equal("Labor", new TicketLine { Kind = "Labor", Description = "   " }.PrintLabel);
    }
}
