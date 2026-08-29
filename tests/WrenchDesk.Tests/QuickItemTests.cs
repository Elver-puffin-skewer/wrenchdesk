using WrenchDesk.Data;

namespace WrenchDesk.Tests;

public class QuickItemTests
{
    [Fact]
    public void The_shop_starts_with_the_list_they_asked_for_in_their_own_order()
    {
        using var h = new TestDb();

        var items = h.QuickItems.Active();

        Assert.Equal(25, items.Count);

        // They said "from top to bottom is the most commonly used", so the order is the point.
        Assert.Equal("Air Filter", items[0].Name);
        Assert.Equal("Spark Plug", items[1].Name);
        Assert.Equal("Safety Switch", items[^1].Name);
    }

    [Fact]
    public void Jobs_are_labor_and_parts_are_parts()
    {
        using var h = new TestDb();
        var byName = h.QuickItems.Active().ToDictionary(i => i.Name, i => i.Kind);

        Assert.Equal("Labor", byName["Oil Change"]);
        Assert.Equal("Labor", byName["Sharpen Blades"]);
        Assert.Equal("Labor", byName["Clean Carburetor"]);

        Assert.Equal("Part", byName["Air Filter"]);
        Assert.Equal("Part", byName["Deck Belt"]);
        Assert.Equal("Part", byName["New Carburetor"]);
    }

    [Fact]
    public void An_item_with_no_price_set_starts_at_nothing_until_it_has_been_used()
    {
        using var h = new TestDb();
        var airFilter = h.QuickItems.Active().First(i => i.Name == "Air Filter");

        Assert.Equal(0, h.QuickItems.StartingPriceFor(airFilter));
    }

    [Fact]
    public void An_item_comes_back_at_what_it_was_last_charged()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());

        // Fitted once at $12.99, so the next one should not need retyping.
        h.AddLine(ticketId, "Part", 1, 12.99m, taxable: true, description: "Air Filter");

        var airFilter = h.QuickItems.Active().First(i => i.Name == "Air Filter");

        Assert.Equal(1299, h.QuickItems.StartingPriceFor(airFilter));
    }

    [Fact]
    public void The_most_recent_price_wins_when_it_has_changed()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        h.AddLine(h.NewTicket(customerId), "Part", 1, 12.99m, taxable: true, description: "Air Filter");
        h.AddLine(h.NewTicket(customerId), "Part", 1, 14.50m, taxable: true, description: "Air Filter");

        var airFilter = h.QuickItems.Active().First(i => i.Name == "Air Filter");

        // Supplier prices move; the latest is the useful one.
        Assert.Equal(1450, h.QuickItems.StartingPriceFor(airFilter));
    }

    [Fact]
    public void A_price_set_on_the_item_overrides_what_history_says()
    {
        using var h = new TestDb();
        h.AddLine(h.NewTicket(h.NewCustomer()), "Part", 1, 12.99m, taxable: true, description: "Air Filter");

        var airFilter = h.QuickItems.Active().First(i => i.Name == "Air Filter");
        airFilter.DefaultCents = 1599;
        h.QuickItems.Update(airFilter);

        Assert.Equal(1599, h.QuickItems.StartingPriceFor(h.QuickItems.Get(airFilter.Id)!));
    }

    [Fact]
    public void A_free_line_in_the_history_is_ignored_rather_than_treated_as_the_price()
    {
        using var h = new TestDb();

        // A line added and not yet priced should not teach the shop that the part is free.
        h.AddLine(h.NewTicket(h.NewCustomer()), "Part", 1, 0m, taxable: true, description: "Pulley");

        var pulley = h.QuickItems.Active().First(i => i.Name == "Pulley");

        Assert.Equal(0, h.QuickItems.StartingPriceFor(pulley));
    }

    [Fact]
    public void The_shop_can_add_their_own()
    {
        using var h = new TestDb();

        h.QuickItems.Insert(new QuickItem { Name = "Valve Adjustment", Kind = "Labor" });

        var added = h.QuickItems.Active().Single(i => i.Name == "Valve Adjustment");
        Assert.Equal("Labor", added.Kind);

        // Added to the end, since the seeded ones are ordered by how often they are reached for.
        Assert.Equal("Valve Adjustment", h.QuickItems.Active()[^1].Name);
    }

    [Fact]
    public void Switching_one_off_hides_it_from_tickets_but_keeps_it()
    {
        using var h = new TestDb();
        var tube = h.QuickItems.Active().First(i => i.Name == "Tube");

        tube.IsActive = false;
        h.QuickItems.Update(tube);

        Assert.DoesNotContain(h.QuickItems.Active(), i => i.Name == "Tube");
        Assert.Contains(h.QuickItems.All(), i => i.Name == "Tube");
    }

    [Fact]
    public void Moving_an_item_up_changes_the_order_on_tickets()
    {
        using var h = new TestDb();
        var before = h.QuickItems.Active();
        var second = before[1];

        h.QuickItems.Move(second.Id, up: true);

        var after = h.QuickItems.Active();
        Assert.Equal(second.Name, after[0].Name);
        Assert.Equal(before[0].Name, after[1].Name);
    }

    [Fact]
    public void Moving_past_either_end_does_nothing()
    {
        using var h = new TestDb();
        var items = h.QuickItems.Active();

        h.QuickItems.Move(items[0].Id, up: true);
        h.QuickItems.Move(items[^1].Id, up: false);

        var after = h.QuickItems.Active();
        Assert.Equal(items[0].Name, after[0].Name);
        Assert.Equal(items[^1].Name, after[^1].Name);
        Assert.Equal(items.Count, after.Count);
    }

    [Fact]
    public void Removing_one_leaves_the_rest_alone()
    {
        using var h = new TestDb();
        var relay = h.QuickItems.Active().First(i => i.Name == "Relay");

        h.QuickItems.Delete(relay.Id);

        Assert.Equal(24, h.QuickItems.Active().Count);
        Assert.DoesNotContain(h.QuickItems.All(), i => i.Name == "Relay");
    }

    [Fact]
    public void Existing_shops_get_the_list_when_they_update()
    {
        // The list arrives as a migration, so a database created before this feature picks it up
        // on the next start rather than only helping brand-new installs.
        using var h = new TestDb();

        h.Db.Migrate();

        Assert.Equal(25, h.QuickItems.Active().Count);
    }
}
