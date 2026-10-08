using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Tidying up the same person entered more than once. Three records takes one go, and either the
/// whole set moves across or none of it does.
/// </summary>
public class MergeManyTests
{
    [Fact]
    public void Three_duplicates_fold_into_one_in_a_single_go()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa", Phone = "256-555-0142" });
        var dup1 = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Soza" });
        var dup2 = h.Customers.Insert(new Customer { FirstName = "H", LastName = "Sosa" });

        var t1 = h.Tickets.Create(new Ticket { CustomerId = dup1 });
        var t2 = h.Tickets.Create(new Ticket { CustomerId = dup2 });

        Assert.Null(h.Customers.MergeMany(new[] { dup1, dup2 }, keep));

        Assert.Null(h.Customers.Get(dup1));
        Assert.Null(h.Customers.Get(dup2));
        Assert.NotNull(h.Customers.Get(keep));

        // Both tickets followed their customer across.
        Assert.Equal(keep, h.Tickets.Get(t1)!.CustomerId);
        Assert.Equal(keep, h.Tickets.Get(t2)!.CustomerId);
    }

    [Fact]
    public void Everything_attached_to_every_duplicate_comes_across()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa" });
        var dup1 = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Soza", Phone = "256-555-0142" });
        var dup2 = h.Customers.Insert(new Customer { FirstName = "H", LastName = "Sosa", City = "Toney" });

        h.Customers.InsertEquipment(new Equipment { CustomerId = dup1, Make = "Toro" });
        h.Customers.InsertEquipment(new Equipment { CustomerId = dup2, Make = "Stihl" });
        h.Money.Insert(new Payment { CustomerId = dup1, AmountCents = 5000, Method = "Cash", PaidOn = "2026-10-01" });
        h.Schedule.Insert(new Appointment { CustomerId = dup2, Kind = "Delivery", ScheduledLocal = "2026-10-05 09:00" });

        h.Customers.MergeMany(new[] { dup1, dup2 }, keep);

        Assert.Equal(2, h.Customers.EquipmentFor(keep).Count);
        Assert.Single(h.Money.ForCustomer(keep));

        // Details filled in on a duplicate but blank on the survivor are carried over.
        var survivor = h.Customers.Get(keep)!;
        Assert.Equal("256-555-0142", survivor.Phone);
        Assert.Equal("Toney", survivor.City);
    }

    [Fact]
    public void The_record_being_kept_is_never_removed_even_if_it_is_in_the_list()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa" });
        var dup = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Soza" });

        Assert.Null(h.Customers.MergeMany(new[] { dup, keep }, keep));

        Assert.NotNull(h.Customers.Get(keep));
        Assert.Null(h.Customers.Get(dup));
    }

    [Fact]
    public void Merging_nothing_is_refused_rather_than_silently_doing_nothing()
    {
        using var h = new TestDb();
        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa" });

        Assert.NotNull(h.Customers.MergeMany(Array.Empty<long>(), keep));
        Assert.NotNull(h.Customers.MergeMany(new[] { keep }, keep));
    }

    [Fact]
    public void A_duplicate_that_has_already_gone_leaves_the_rest_untouched()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa" });
        var dup = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Soza" });
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = dup });

        // One id in the set no longer exists - the whole merge is refused rather than half done.
        Assert.NotNull(h.Customers.MergeMany(new[] { dup, 9999L }, keep));

        Assert.NotNull(h.Customers.Get(dup));
        Assert.Equal(dup, h.Tickets.Get(ticketId)!.CustomerId);
    }

    [Fact]
    public void One_duplicate_still_works_the_way_it_always_did()
    {
        using var h = new TestDb();

        var keep = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Sosa" });
        var dup = h.Customers.Insert(new Customer { FirstName = "Hector", LastName = "Soza", Phone = "256-555-0142" });
        var ticketId = h.Tickets.Create(new Ticket { CustomerId = dup });

        Assert.Null(h.Customers.Merge(dup, keep));

        Assert.Null(h.Customers.Get(dup));
        Assert.Equal(keep, h.Tickets.Get(ticketId)!.CustomerId);
        Assert.Equal("256-555-0142", h.Customers.Get(keep)!.Phone);
    }

    [Fact]
    public void A_customer_with_work_against_it_still_cannot_be_deleted_outright()
    {
        using var h = new TestDb();
        var customerId = h.NewCustomer();
        h.Tickets.Create(new Ticket { CustomerId = customerId });

        // The confirmation step is in the screen; this is the guard behind it.
        Assert.NotNull(h.Customers.TryDelete(customerId));
        Assert.NotNull(h.Customers.Get(customerId));
    }
}
