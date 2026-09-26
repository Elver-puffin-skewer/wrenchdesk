using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Which week a job's money lands in. The shop reckons it to the week the work was finished,
/// not the week the machine came in or the week a deposit happened to change hands.
/// </summary>
public class RevenueDateTests
{
    private static readonly DateTime WeekAStart = new(2026, 9, 14);   // Mon
    private static readonly DateTime WeekAEnd = new(2026, 9, 20);
    private static readonly DateTime WeekBStart = new(2026, 9, 21);
    private static readonly DateTime WeekBEnd = new(2026, 9, 27);

    [Fact]
    public void Money_counts_in_the_week_the_job_was_finished_not_the_week_it_was_paid()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        // Paid when the machine was dropped off in week A; finished in week B.
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 150000, "2026-09-16");
        Complete(h, ticketId, "2026-09-24");

        Assert.Equal(0, h.Money.TotalInRange(WeekAStart, WeekAEnd));
        Assert.Equal(150000, h.Money.TotalInRange(WeekBStart, WeekBEnd));
    }

    [Fact]
    public void Money_on_a_job_not_finished_yet_still_counts_when_it_was_taken()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        // A deposit on a job still open has no completion date to count against, and the cash is
        // real either way - it must not vanish from the takings.
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 50000, "2026-09-16");

        Assert.Equal(50000, h.Money.TotalInRange(WeekAStart, WeekAEnd));
    }

    [Fact]
    public void Money_with_no_ticket_behind_it_counts_when_it_was_taken()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        h.Money.Insert(new Payment { CustomerId = customerId, TicketId = null, AmountCents = 2500, Method = "Cash", PaidOn = "2026-09-16" });

        Assert.Equal(2500, h.Money.TotalInRange(WeekAStart, WeekAEnd));
    }

    [Fact]
    public void Part_payments_across_two_weeks_both_land_on_the_completion_date()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 40000, "2026-09-16");
        Pay(h, customerId, ticketId, 110000, "2026-09-25");
        Complete(h, ticketId, "2026-09-24");

        Assert.Equal(0, h.Money.TotalInRange(WeekAStart, WeekAEnd));
        Assert.Equal(150000, h.Money.TotalInRange(WeekBStart, WeekBEnd));
    }

    [Fact]
    public void Every_screen_agrees_about_the_same_week()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 150000, "2026-09-16");
        Complete(h, ticketId, "2026-09-24");

        // The dashboard total, the day-by-day breakdown, the till split and the list of payments
        // all have to be the same money, or two screens disagree about one week.
        var total = h.Money.TotalInRange(WeekBStart, WeekBEnd);
        var daily = h.Money.DailyBuckets(WeekBStart, WeekBEnd).Sum(b => b.TotalCents);
        var byMethod = h.Money.ByMethod(WeekBStart, WeekBEnd).Sum(m => m.TotalCents);
        var listed = h.Money.InRange(WeekBStart, WeekBEnd).Sum(p => p.AmountCents);

        Assert.Equal(150000, total);
        Assert.Equal(150000, daily);
        Assert.Equal(150000, byMethod);
        Assert.Equal(150000, listed);
    }

    [Fact]
    public void Nothing_is_counted_twice_or_lost_across_all_time()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var finished = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, finished, 150000, "2026-09-16");
        Complete(h, finished, "2026-09-24");

        var open = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-15" });
        Pay(h, customerId, open, 50000, "2026-09-17");

        h.Money.Insert(new Payment { CustomerId = customerId, AmountCents = 2500, Method = "Cash", PaidOn = "2026-09-18" });

        // Moving which day money counts on must never change how much there is.
        var everything = h.Money.TotalInRange(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        Assert.Equal(150000 + 50000 + 2500, everything);
    }

    [Fact]
    public void A_payment_says_which_day_it_counted_on_when_that_is_not_the_day_it_was_paid()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 150000, "2026-09-16");
        Complete(h, ticketId, "2026-09-24");

        var row = h.Money.ForTicket(ticketId).Single();
        Assert.Equal("2026-09-16", row.PaidOn);
        Assert.Equal("2026-09-24", row.RevenueOn);
        Assert.True(row.CountedOnAnotherDay);
    }

    [Fact]
    public void The_export_carries_both_dates_so_a_bookkeeper_can_reconcile_it()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var ticketId = h.Tickets.Create(new Ticket { CustomerId = customerId, IntakeOn = "2026-09-14" });
        Pay(h, customerId, ticketId, 150000, "2026-09-16");
        Complete(h, ticketId, "2026-09-24");

        var csv = h.Money.ExportCsv(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));

        Assert.Contains("Date,Paid On,Amount,Method", csv);
        Assert.Contains("2026-09-24,2026-09-16,1500.00", csv);
    }

    private static void Pay(TestDb h, long customerId, long ticketId, long cents, string paidOn) =>
        h.Money.Insert(new Payment
        {
            CustomerId = customerId, TicketId = ticketId,
            AmountCents = cents, Method = "Cash", PaidOn = paidOn
        });

    private static void Complete(TestDb h, long ticketId, string completedOn)
    {
        var ticket = h.Tickets.Get(ticketId)!;
        ticket.CompletedOn = completedOn;
        h.Tickets.Update(ticket);
    }
}
