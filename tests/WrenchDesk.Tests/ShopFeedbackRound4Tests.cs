using WrenchDesk.Data;
using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

/// <summary>
/// What a stop is called, and what kind of stop it is. Nobody typing into a phone at the counter
/// follows a convention, so the reading has to cope with what people actually write.
/// </summary>
public class StopHeadingTests
{
    [Fact]
    public void A_calendar_entrys_own_wording_is_what_shows()
    {
        var row = new AppointmentRow { Title = "William Burgess Delivery", Kind = "Other", CustomerName = "" };

        // The dashboard used to build its own label and show "Other" for this.
        Assert.Equal("William Burgess Delivery", row.Heading);
    }

    [Fact]
    public void A_stop_written_up_here_still_reads_from_its_kind_and_customer()
    {
        var row = new AppointmentRow { Title = "", Kind = "Delivery", CustomerName = "Dale Fenner" };

        Assert.Equal("Delivery — Dale Fenner", row.Heading);
    }

    [Fact]
    public void A_stop_with_nothing_on_it_still_says_something()
    {
        var row = new AppointmentRow { Title = "", Kind = "Pickup", CustomerName = "" };

        Assert.Equal("Pickup — (no customer)", row.Heading);
    }

    [Theory]
    [InlineData("Delivery — Bill Moore", "Delivery")]
    [InlineData("Pickup - Dale Fenner", "Pickup")]
    public void The_shops_own_convention_still_reads_first(string summary, string expected)
    {
        Assert.Equal(expected, AppointmentEventMapper.ParseKind(summary));
    }

    [Theory]
    [InlineData("William Burgess Delivery", "Delivery")]
    [InlineData("mower pickup 9am", "Pickup")]
    [InlineData("Bullard drop-off", "Drop-off")]
    public void The_word_is_read_wherever_somebody_put_it(string summary, string expected)
    {
        // Typed on a phone, in whatever order came naturally.
        Assert.Equal(expected, AppointmentEventMapper.ParseKind(summary));
    }

    [Theory]
    [InlineData("Redelivery scheduled")]
    [InlineData("Pickups and deliveries this week")]
    [InlineData("Dentist")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_unclear_is_left_alone_rather_than_guessed(string? summary)
    {
        // Two kinds in one line, or a word buried inside another, is a guess - and a stop filed
        // under the wrong kind is worse than one left for somebody to set.
        Assert.Null(AppointmentEventMapper.ParseKind(summary));
    }
}

/// <summary>Notes about the person, as opposed to notes about one of their machines.</summary>
public class CustomerNotesTests
{
    [Fact]
    public void A_note_about_the_customer_is_kept_on_the_customer()
    {
        using var h = new TestDb();
        var id = h.Customers.Insert(new Customer { FirstName = "Milton", LastName = "Bullard" });

        var customer = h.Customers.Get(id)!;
        customer.Notes = "Gate code 4417. Dog in the yard. Pays by check.";
        h.Customers.Update(customer);

        Assert.Equal("Gate code 4417. Dog in the yard. Pays by check.", h.Customers.Get(id)!.Notes);
    }

    [Fact]
    public void Customer_notes_and_machine_notes_are_separate_things()
    {
        using var h = new TestDb();
        var id = h.Customers.Insert(new Customer { FirstName = "Milton", LastName = "Bullard", Notes = "Gate code 4417" });
        var machineId = h.Customers.InsertEquipment(new Equipment
        {
            CustomerId = id, Make = "Craftsman", Model = "13APA1ZT093", Notes = "Deck needs the long belt"
        });

        Assert.Equal("Gate code 4417", h.Customers.Get(id)!.Notes);
        Assert.Equal("Deck needs the long belt", h.Customers.GetEquipment(machineId)!.Notes);
    }

    [Fact]
    public void A_note_survives_the_customer_being_merged_into_another()
    {
        using var h = new TestDb();
        var keep = h.Customers.Insert(new Customer { FirstName = "Milton", LastName = "Bullard" });
        var dup = h.Customers.Insert(new Customer { FirstName = "Milton", LastName = "Ballard", Notes = "Gate code 4417" });

        h.Customers.Merge(dup, keep);

        Assert.Contains("Gate code 4417", h.Customers.Get(keep)!.Notes);
    }
}
