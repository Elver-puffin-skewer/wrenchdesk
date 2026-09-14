using WrenchDesk.Data;
using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

/// <summary>
/// Working out who a calendar entry is about. Shops write these as "Bill Moore - 256-555-0142",
/// so the number is the strongest signal; the name is the fallback. Getting it wrong hangs a job
/// off the wrong person's history, so an uncertain match has to stay unattached.
/// </summary>
public class CustomerMatchingTests
{
    [Fact]
    public void Matches_on_a_phone_number_however_it_was_typed()
    {
        using var h = new TestDb();
        var id = h.Customers.Insert(new Customer
        {
            FirstName = "Bill", LastName = "Moore", Phone = "(256) 555-0142"
        });

        var match = h.Customers.MatchFromCalendarText("Pickup - Bill M, call 256.555.0142 first");

        Assert.NotNull(match);
        Assert.Equal(id, match!.Id);
    }

    [Fact]
    public void Matches_on_the_second_number_too()
    {
        using var h = new TestDb();
        var id = h.Customers.Insert(new Customer
        {
            FirstName = "Ada", LastName = "Prince", Phone = "", PhoneAlt = "256-555-0199"
        });

        var match = h.Customers.MatchFromCalendarText("Delivery 2565550199");

        Assert.Equal(id, match?.Id);
    }

    [Fact]
    public void Two_customers_on_one_number_match_nobody()
    {
        using var h = new TestDb();
        h.Customers.Insert(new Customer { FirstName = "Ann", LastName = "Reed", Phone = "256-555-0177" });
        h.Customers.Insert(new Customer { FirstName = "Joe", LastName = "Reed", Phone = "256-555-0177" });

        // Guessing between them would be worse than leaving it for someone to set by hand.
        Assert.Null(h.Customers.MatchFromCalendarText("Pickup 256-555-0177"));
    }

    [Fact]
    public void A_returned_match_is_a_whole_customer_record()
    {
        using var h = new TestDb();
        h.Customers.Insert(new Customer
        {
            FirstName = "Thaddeus", LastName = "Quimby", Address1 = "12 Mill Creek Rd", City = "Toney"
        });

        // The name path used to be able to hand back a half-filled record; the schedule reads the
        // address off it.
        var match = h.Customers.MatchFromCalendarText("Drop-off - Thaddeus Quimby, deck belt");

        Assert.Equal("12 Mill Creek Rd", match?.Address1);
        Assert.Equal("Toney", match?.City);
    }

    [Fact]
    public void Still_matches_a_customer_past_the_old_five_hundred_row_ceiling()
    {
        using var h = new TestDb();

        // The matcher used to borrow the customer search, which stops at 500 rows ordered by
        // surname. Anyone sorting after that was invisible to it, silently and forever.
        for (var i = 0; i < 520; i++)
            h.Customers.Insert(new Customer { FirstName = $"Filler{i:000}", LastName = $"Filler{i:000}" });

        var byName = h.Customers.Insert(new Customer { FirstName = "Thaddeus", LastName = "Zzyzx" });
        var byPhone = h.Customers.Insert(new Customer
        {
            FirstName = "Wilhelmina", LastName = "Zzzland", Phone = "256-555-0123"
        });

        // Both of them really are past the cut: the search the matcher used to lean on still
        // stops at 500 and still cannot see either of these two.
        var searchable = h.Customers.Search(null);
        Assert.Equal(500, searchable.Count);
        Assert.DoesNotContain(searchable, c => c.Id == byName || c.Id == byPhone);

        Assert.Equal(byName, h.Customers.MatchFromCalendarText("Pickup - Thaddeus Zzyzx mower")?.Id);
        Assert.Equal(byPhone, h.Customers.MatchFromCalendarText("Delivery (256) 555-0123")?.Id);
    }

    [Fact]
    public void An_entry_naming_nobody_stays_unattached()
    {
        using var h = new TestDb();
        h.Customers.Insert(new Customer { FirstName = "Bill", LastName = "Moore", Phone = "256-555-0142" });

        Assert.Null(h.Customers.MatchFromCalendarText("Dentist"));
    }
}

/// <summary>
/// The sign-in callback is a plain HTTP endpoint on a machine anyone on the shop wifi can reach,
/// so it has to be able to tell its own sign-in from somebody else's.
/// </summary>
public class OAuthStateTests
{
    [Fact]
    public void The_state_it_sent_out_is_accepted_once_and_then_spent()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        var url = auth.BeginAuthorization("http://localhost:5173/google/callback");
        var state = h.Settings.Get(SettingsStore.GoogleOAuthState);

        Assert.NotEmpty(state);

        // The state has to actually go to Google, or it comes back empty and the check below
        // would refuse every sign-in the shop ever attempted.
        Assert.Contains($"state={Uri.EscapeDataString(state)}", url);
        Assert.True(auth.ConsumeAuthorizationState(state));

        // Spent, so replaying the same callback gets nowhere.
        Assert.False(auth.ConsumeAuthorizationState(state));
    }

    [Fact]
    public void A_callback_carrying_something_else_is_refused()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        auth.BeginAuthorization("http://localhost:5173/google/callback");

        Assert.False(auth.ConsumeAuthorizationState("not-the-one-we-sent"));

        // A refused attempt spends the state too, rather than leaving it up for another go.
        Assert.Empty(h.Settings.Get(SettingsStore.GoogleOAuthState));
    }

    [Fact]
    public void A_callback_arriving_with_no_sign_in_under_way_is_refused()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        Assert.False(auth.ConsumeAuthorizationState("anything"));
        Assert.False(auth.ConsumeAuthorizationState(null));
    }
}
