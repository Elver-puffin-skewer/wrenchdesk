using WrenchDesk.Data;
using WrenchDesk.Services;

namespace WrenchDesk.Tests;

/// <summary>
/// Two people writing at the same moment is the normal case here, not an exotic one: the counter
/// PC, a tablet at the bench and the house PC all reach the same database through the same
/// program. These pin the places where that used to go wrong.
/// </summary>
public class TicketNumberingConcurrencyTests
{
    [Fact]
    public void Tickets_created_at_the_same_moment_get_different_numbers()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        // Eight at once is well past what a two-person shop can do by hand, which is the point —
        // it makes the window between reading the highest number and inserting wide enough to
        // lose a race in, if the race were still there to lose.
        var ids = new long[8];
        Parallel.For(0, ids.Length, i =>
            ids[i] = h.Tickets.Create(new Ticket { CustomerId = customerId }));

        var numbers = ids.Select(id => h.Tickets.Get(id)!.Number).ToList();

        Assert.Equal(8, numbers.Distinct().Count());
        Assert.All(numbers, n => Assert.StartsWith("WSE-", n));
    }

    [Fact]
    public void Ticket_numbers_run_consecutively_under_load()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();

        var ids = new long[12];
        Parallel.For(0, ids.Length, i =>
            ids[i] = h.Tickets.Create(new Ticket { CustomerId = customerId }));

        // Whatever order they landed in, the allocated numbers should be one unbroken run —
        // a gap would mean two of them had read the same highest number.
        var suffixes = ids
            .Select(id => int.Parse(h.Tickets.Get(id)!.Number.Split('-')[1]))
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(Enumerable.Range(suffixes[0], suffixes.Count).ToList(), suffixes);
    }
}

/// <summary>
/// Clicking the desktop icon while WrenchDesk is already down by the clock must not start a
/// second copy that dies on the port the first one is serving.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void A_copy_already_running_is_detected()
    {
        // Stands in for the copy already in the notification area. The claim lives as long as
        // this handle is open, exactly as it does for a running program.
        using var alreadyRunning = new Mutex(initiallyOwned: false, @"Local\WrenchDesk-59901", out var createdNew);
        Assert.True(createdNew);

        Assert.False(SingleInstance.TryClaim(59901));
    }

    [Fact]
    public void A_free_port_is_claimed_and_stays_claimed()
    {
        Assert.True(SingleInstance.TryClaim(59902));

        // Asking again must not hand the claim away by building a second handle over the first.
        Assert.True(SingleInstance.TryClaim(59902));
    }
}
