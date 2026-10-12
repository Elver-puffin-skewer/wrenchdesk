using WrenchDesk.Data;

namespace WrenchDesk.Tests;

/// <summary>
/// Passwords. Stored so that the database alone does not give anybody away, and checked so that
/// a wrong one is a wrong one.
/// </summary>
public class PasswordTests
{
    [Fact]
    public void The_password_itself_is_never_in_what_gets_stored()
    {
        var hash = UserRepo.HashPassword("blue mower 1947");

        Assert.DoesNotContain("blue mower 1947", hash);
        Assert.StartsWith("pbkdf2", hash);
    }

    [Fact]
    public void The_same_password_stores_differently_every_time()
    {
        // Each account gets its own salt, so two people who picked the same password do not
        // advertise that to anybody reading the table.
        Assert.NotEqual(UserRepo.HashPassword("same"), UserRepo.HashPassword("same"));
    }

    [Fact]
    public void A_correct_password_is_accepted_and_a_wrong_one_is_not()
    {
        var hash = UserRepo.HashPassword("deck belt");

        Assert.True(UserRepo.VerifyPassword("deck belt", hash));
        Assert.False(UserRepo.VerifyPassword("Deck Belt", hash));
        Assert.False(UserRepo.VerifyPassword("deck belt ", hash));
        Assert.False(UserRepo.VerifyPassword("", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$sha256$nonsense$aaa$bbb")]
    [InlineData("pbkdf2$sha256$1000$!!!$!!!")]
    public void Rubbish_in_the_stored_field_refuses_rather_than_throws(string stored)
    {
        // A hash damaged by hand editing must lock that account, not take the app down.
        Assert.False(UserRepo.VerifyPassword("anything", stored));
    }
}

/// <summary>Accounts, and what each kind of person is allowed to do.</summary>
public class UserAccountTests
{
    [Fact]
    public void A_fresh_shop_has_nobody_and_nothing_is_required()
    {
        using var h = new TestDb();

        Assert.False(h.Users.AnyExist());

        // Updating must never be the thing that stops the counter working.
        Assert.False(h.Settings.GetBool(SettingsStore.AuthRequired));
    }

    [Fact]
    public void An_account_signs_in_with_its_password()
    {
        using var h = new TestDb();
        Assert.Null(h.Users.Create("walt", "Walt", "spark plug", UserRole.Owner));

        Assert.NotNull(h.Users.Authenticate("walt", "spark plug"));
        Assert.Null(h.Users.Authenticate("walt", "wrong"));
        Assert.Null(h.Users.Authenticate("walt", null));
    }

    [Fact]
    public void An_account_with_no_password_signs_in_on_the_name_alone()
    {
        using var h = new TestDb();
        Assert.Null(h.Users.Create("dale", "Dale", null, UserRole.Tech));

        // Deliberate, and warned about where it is chosen: tapping a name is the whole point.
        var user = h.Users.Authenticate("dale", null);
        Assert.NotNull(user);
        Assert.False(user!.HasPassword);
    }

    [Fact]
    public void A_password_cannot_be_skipped_just_because_none_was_given()
    {
        using var h = new TestDb();
        h.Users.Create("walt", "Walt", "spark plug", UserRole.Owner);

        // The hole the passwordless option could easily have opened.
        Assert.Null(h.Users.Authenticate("walt", ""));
        Assert.Null(h.Users.Authenticate("walt", null));
    }

    [Fact]
    public void The_sign_in_name_does_not_care_about_capitals()
    {
        using var h = new TestDb();
        h.Users.Create("Walt", "Walt", "spark plug", UserRole.Owner);

        Assert.NotNull(h.Users.Authenticate("walt", "spark plug"));
        Assert.NotNull(h.Users.Authenticate("WALT", "spark plug"));

        // And the same name cannot be taken twice in a different case.
        Assert.NotNull(h.Users.Create("WALT", "Someone else", "other", UserRole.Tech));
    }

    [Fact]
    public void An_account_switched_off_cannot_sign_in()
    {
        using var h = new TestDb();
        h.Users.Create("dale", "Dale", null, UserRole.Tech);
        var dale = h.Users.ByUsername("dale")!;

        h.Users.UpdateDetails(dale.Id, "Dale", UserRole.Tech, isActive: false);

        Assert.Null(h.Users.Authenticate("dale", null));
    }

    [Fact]
    public void The_last_owner_cannot_be_removed_or_demoted()
    {
        using var h = new TestDb();
        h.Users.Create("walt", "Walt", "x", UserRole.Owner);
        var walt = h.Users.ByUsername("walt")!;

        // Otherwise nobody can ever hand out an account again.
        Assert.NotNull(h.Users.Delete(walt.Id));
        Assert.NotNull(h.Users.UpdateDetails(walt.Id, "Walt", UserRole.Tech, true));
        Assert.NotNull(h.Users.UpdateDetails(walt.Id, "Walt", UserRole.Owner, isActive: false));

        // With a second owner in place it is allowed.
        h.Users.Create("sue", "Sue", "y", UserRole.Owner);
        Assert.Null(h.Users.UpdateDetails(walt.Id, "Walt", UserRole.Tech, true));
    }

    [Fact]
    public void A_password_can_be_added_and_taken_away_again()
    {
        using var h = new TestDb();
        h.Users.Create("dale", "Dale", null, UserRole.Tech);
        var dale = h.Users.ByUsername("dale")!;

        h.Users.SetPassword(dale.Id, "new one");
        Assert.Null(h.Users.Authenticate("dale", null));
        Assert.NotNull(h.Users.Authenticate("dale", "new one"));

        h.Users.SetPassword(dale.Id, null);
        Assert.NotNull(h.Users.Authenticate("dale", null));
    }

    [Theory]
    [InlineData(UserRole.Owner, true, true, true, true)]
    [InlineData(UserRole.Admin, false, true, true, true)]
    [InlineData(UserRole.Tech, false, false, false, false)]
    public void Each_role_can_do_what_it_should(string role, bool users, bool money, bool settings, bool deletes)
    {
        Assert.Equal(users, UserRole.CanManageUsers(role));
        Assert.Equal(money, UserRole.CanSeeMoney(role));
        Assert.Equal(settings, UserRole.CanChangeSettings(role));
        Assert.Equal(deletes, UserRole.CanDeleteRecords(role));
    }

    [Fact]
    public void Somebody_with_no_account_at_all_can_do_nothing()
    {
        Assert.False(UserRole.CanManageUsers(null));
        Assert.False(UserRole.CanSeeMoney(null));
        Assert.False(UserRole.CanChangeSettings(null));
        Assert.False(UserRole.CanDeleteRecords(null));
        Assert.False(UserRole.CanSeeMoney("Nonsense"));
    }

    [Fact]
    public void An_account_cannot_be_made_with_a_role_that_does_not_exist()
    {
        using var h = new TestDb();

        Assert.NotNull(h.Users.Create("x", "X", "p", "Superuser"));
        Assert.NotNull(h.Users.Create("", "No name", "p", UserRole.Tech));
    }
}

/// <summary>The record of who did what.</summary>
public class AuditLogTests
{
    [Fact]
    public void An_entry_keeps_the_name_even_after_the_account_goes()
    {
        using var h = new TestDb();
        h.Users.Create("dale", "Dale", null, UserRole.Tech);
        var dale = h.Users.ByUsername("dale")!;

        h.Audit.Write(dale.Id, dale.Name, "Recorded a payment", AuditEntity.Payment, 7, "$120.00");
        h.Users.Delete(dale.Id);

        // A log that forgets who did something the moment they leave is not a log.
        var entry = h.Audit.Recent().Single();
        Assert.Equal("Dale", entry.UserName);
        Assert.Equal("$120.00", entry.Detail);
        Assert.Null(entry.UserId);
    }

    [Fact]
    public void Entries_come_back_newest_first()
    {
        using var h = new TestDb();

        h.Audit.Write(null, "Shop", "First");
        h.Audit.Write(null, "Shop", "Second");
        h.Audit.Write(null, "Shop", "Third");

        Assert.Equal(new[] { "Third", "Second", "First" }, h.Audit.Recent().Select(e => e.Action));
    }

    [Fact]
    public void The_log_can_be_narrowed_to_one_kind_of_thing()
    {
        using var h = new TestDb();

        h.Audit.Write(null, "Shop", "Recorded a payment", AuditEntity.Payment);
        h.Audit.Write(null, "Shop", "Deleted a customer", AuditEntity.Customer);
        h.Audit.Write(null, "Shop", "Signed in", AuditEntity.Session);

        Assert.Single(h.Audit.Recent(entity: AuditEntity.Payment));
        Assert.Equal(3, h.Audit.Count());
    }

    [Fact]
    public void One_records_whole_history_can_be_read_back_in_order()
    {
        using var h = new TestDb();

        h.Audit.Write(null, "Shop", "Moved a ticket to Approved", AuditEntity.Ticket, 42);
        h.Audit.Write(null, "Shop", "Moved a ticket to Closed", AuditEntity.Ticket, 42);
        h.Audit.Write(null, "Shop", "Moved a ticket to Closed", AuditEntity.Ticket, 99);

        var story = h.Audit.ForEntity(AuditEntity.Ticket, 42);

        Assert.Equal(2, story.Count);
        Assert.Equal("Moved a ticket to Approved", story[0].Action);
    }

    [Fact]
    public void An_entry_about_a_ticket_links_to_it()
    {
        using var h = new TestDb();
        h.Audit.Write(null, "Shop", "Moved a ticket to Closed", AuditEntity.Ticket, 42);

        Assert.Equal("/tickets/42", h.Audit.Recent().Single().Link);
    }
}
