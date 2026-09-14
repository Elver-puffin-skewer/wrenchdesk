using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// A ticket parked on Waiting on Parts used to say nothing about which part, from whom, or when
/// it was due, which is how a machine ends up sitting three weeks on a carburettor nobody
/// remembered was backordered.
/// </summary>
public class PartsOnOrderTests
{
    [Fact]
    public void A_part_only_reaches_the_board_once_it_is_ordered()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 1, each: 42m, taxable: true, description: "Carburettor");

        Assert.Empty(h.Tickets.PartsOnOrder());

        var line = h.Tickets.Lines(ticketId).Single();
        line.OrderedOn = Days(-3);
        line.Supplier = "Stens";
        h.Tickets.UpdateLine(line);

        var board = h.Tickets.PartsOnOrder();
        Assert.Single(board);
        Assert.Equal("Carburettor", board[0].Description);
        Assert.Equal("Stens", board[0].Supplier);
        Assert.Equal(3, board[0].DaysWaiting);
    }

    [Fact]
    public void A_part_that_has_arrived_comes_off_the_board()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 1, each: 42m, taxable: true, description: "Deck belt");

        var line = h.Tickets.Lines(ticketId).Single();
        line.OrderedOn = Days(-5);
        h.Tickets.UpdateLine(line);
        Assert.Single(h.Tickets.PartsOnOrder());

        h.Tickets.MarkPartArrived(line.Id);

        Assert.Empty(h.Tickets.PartsOnOrder());
        Assert.False(h.Tickets.Lines(ticketId).Single().IsOnOrder);
    }

    [Fact]
    public void Past_the_day_promised_counts_as_late()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 1, each: 10m, taxable: true, description: "Spindle");

        var line = h.Tickets.Lines(ticketId).Single();
        line.OrderedOn = Days(-10);
        line.ExpectedOn = Days(-2);
        h.Tickets.UpdateLine(line);

        Assert.True(h.Tickets.PartsOnOrder().Single().IsOverdue);

        line.ExpectedOn = Days(+2);
        h.Tickets.UpdateLine(line);
        Assert.False(h.Tickets.PartsOnOrder().Single().IsOverdue);
    }

    [Fact]
    public void A_supplier_who_would_not_say_when_is_flagged_rather_than_called_late()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 1, each: 10m, taxable: true, description: "Fuel pump");

        var line = h.Tickets.Lines(ticketId).Single();
        line.OrderedOn = Days(-9);
        h.Tickets.UpdateLine(line);

        var row = h.Tickets.PartsOnOrder().Single();
        Assert.True(row.NoDatePromised);
        Assert.False(row.IsOverdue);
    }

    [Fact]
    public void A_declined_ticket_stops_cluttering_the_board()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 1, each: 10m, taxable: true, description: "Blade");

        var line = h.Tickets.Lines(ticketId).Single();
        line.OrderedOn = Days(-1);
        h.Tickets.UpdateLine(line);
        Assert.Single(h.Tickets.PartsOnOrder());

        h.Tickets.SetStatus(ticketId, TicketStatus.Declined);
        Assert.Empty(h.Tickets.PartsOnOrder());
    }

    [Fact]
    public void Order_details_survive_a_round_trip_through_the_database()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.AddLine(ticketId, "Part", qty: 2, each: 15m, taxable: true, description: "Fuel line");

        var line = h.Tickets.Lines(ticketId).Single();
        line.Supplier = "Rotary";
        line.OrderedOn = "2026-09-01";
        line.ExpectedOn = "2026-09-08";
        h.Tickets.UpdateLine(line);

        var again = h.Tickets.Lines(ticketId).Single();
        Assert.Equal("Rotary", again.Supplier);
        Assert.Equal("2026-09-01", again.OrderedOn);
        Assert.Equal("2026-09-08", again.ExpectedOn);
        Assert.Null(again.ArrivedOn);
    }

    private static string Days(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd");
}

/// <summary>Where a machine is standing, so it can be found on a full lot in July.</summary>
public class MachineLocationTests
{
    [Fact]
    public void Each_machine_on_a_ticket_keeps_its_own_place()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.NewTicket(customerId);

        var mower = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Toro" });
        var saw = h.Customers.InsertEquipment(new Equipment { CustomerId = customerId, Make = "Stihl" });

        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = mower, Location = "Bay 3" });
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, EquipmentId = saw, Location = "Back shelf" });

        var machines = h.Tickets.Machines(ticketId);

        // Two machines off one visit routinely end up in different places.
        Assert.Equal("Bay 3", machines[0].Location);
        Assert.Equal("Back shelf", machines[1].Location);
    }

    [Fact]
    public void Moving_a_machine_is_remembered()
    {
        using var h = new TestDb();
        var ticketId = h.NewTicket(h.NewCustomer());
        h.Tickets.AddMachine(new TicketEquipment { TicketId = ticketId, Location = "Front lot" });

        var machine = h.Tickets.Machines(ticketId).Single();
        machine.Location = "Bay 1";
        h.Tickets.UpdateMachine(machine);

        Assert.Equal("Bay 1", h.Tickets.Machines(ticketId).Single().Location);
    }
}
