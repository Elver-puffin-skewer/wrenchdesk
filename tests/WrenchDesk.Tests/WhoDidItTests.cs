using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// A record carrying its own answer to "who did this", rather than that living only in a log
/// somebody has to go and read.
/// </summary>
public class WhoDidItTests
{
    [Fact]
    public void A_ticket_remembers_who_wrote_it_up()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        var ticket = h.Tickets.Get(ticketId)!;
        Assert.Equal("Dale", ticket.CreatedBy);
        Assert.Equal("Dale", ticket.UpdatedBy);
    }

    [Fact]
    public void Changing_a_ticket_records_who_changed_it_without_losing_who_started_it()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        var ticket = h.Tickets.Get(ticketId)!;
        ticket.Notes = "Customer rang about it";
        h.Tickets.Update(ticket, by: "Sue");

        var after = h.Tickets.Get(ticketId)!;
        Assert.Equal("Dale", after.CreatedBy);
        Assert.Equal("Sue", after.UpdatedBy);
    }

    [Fact]
    public void A_save_that_does_not_say_who_leaves_the_last_name_alone()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        // A screen that forgot to pass a name must not wipe out the answer.
        var ticket = h.Tickets.Get(ticketId)!;
        h.Tickets.Update(ticket);

        Assert.Equal("Dale", h.Tickets.Get(ticketId)!.UpdatedBy);
    }

    [Fact]
    public void Moving_a_ticket_through_the_shop_records_who_moved_it()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        h.Tickets.SetStatus(ticketId, TicketStatus.Ready, by: "Sue");

        var ticket = h.Tickets.Get(ticketId)!;
        Assert.Equal("Sue", ticket.UpdatedBy);
        Assert.Equal("Dale", ticket.CreatedBy);
    }

    [Fact]
    public void A_status_change_with_no_name_keeps_the_one_already_there()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        h.Tickets.SetStatus(ticketId, TicketStatus.Approved);

        Assert.Equal("Dale", h.Tickets.Get(ticketId)!.UpdatedBy);
    }

    [Fact]
    public void A_payment_remembers_who_took_the_money()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.NewTicket(customerId);

        h.Money.Insert(new Payment
        {
            CustomerId = customerId, TicketId = ticketId,
            AmountCents = 9000, Method = "Cash", PaidOn = "2026-10-11"
        }, by: "Dale");

        // The one most worth being able to answer for.
        Assert.Equal("Dale", h.Money.ForTicket(ticketId).Single().TakenBy);
    }

    [Fact]
    public void The_ticket_list_can_say_whose_ticket_it_was()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.Tickets.Create(new Ticket { CustomerId = customerId }, by: "Dale");

        Assert.Equal("Dale", h.Tickets.ForCustomer(customerId).Single().CreatedBy);
    }

    [Fact]
    public void Work_from_before_any_of_this_simply_has_no_name_on_it()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        // Nothing passed, the way every call looked before accounts existed.
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId });
        h.Money.Insert(new Payment { CustomerId = customerId, TicketId = ticketId, AmountCents = 100, PaidOn = "2026-10-11" });

        // Blank, not guessed. The shop did not record it then and inventing somebody is worse.
        Assert.Equal("", h.Tickets.Get(ticketId)!.CreatedBy);
        Assert.Equal("", h.Money.ForTicket(ticketId).Single().TakenBy);
    }

    [Fact]
    public void Stamping_a_name_changes_nothing_about_the_money()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, TaxRateBp = 888 }, by: "Dale");

        h.AddLine(ticketId, "Part", 2, 12.99m, taxable: true, description: "Filter");
        h.Money.Insert(new Payment { CustomerId = customerId, TicketId = ticketId, AmountCents = 1000, PaidOn = "2026-10-11" }, by: "Sue");

        var totals = h.Tickets.Totals(ticketId);
        Assert.Equal(2598, totals.SubtotalCents);
        Assert.Equal(231, totals.TaxCents);
        Assert.Equal(2829, totals.TotalCents);
        Assert.Equal(1000, totals.PaidCents);
    }

    [Fact]
    public void A_tickets_history_reads_in_the_order_it_happened()
    {
        using var h = new TestDb();
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = h.NewCustomer() }, by: "Dale");

        h.Audit.Write(null, "Dale", "Wrote up a ticket", AuditEntity.Ticket, ticketId);
        h.Audit.Write(null, "Sue", "Recorded a payment", AuditEntity.Payment, ticketId);
        h.Audit.Write(null, "Sue", "Moved a ticket to Closed", AuditEntity.Ticket, ticketId);

        var story = h.Audit.ForEntity(AuditEntity.Ticket, ticketId);

        Assert.Equal(new[] { "Wrote up a ticket", "Moved a ticket to Closed" }, story.Select(e => e.Action));
        Assert.Equal(new[] { "Dale", "Sue" }, story.Select(e => e.UserName));
    }
}
