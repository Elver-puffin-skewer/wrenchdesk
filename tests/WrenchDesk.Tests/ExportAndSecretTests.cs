using WrenchDesk.Data;
using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

/// <summary>
/// The takings export is opened in Excel or Google Sheets by whoever does the books, so what
/// goes into it has to be read as text and not run as a formula.
/// </summary>
public class CsvExportTests
{
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+44 7700 900000")]
    [InlineData("-lookup")]
    [InlineData("@SUM(A1:A9)")]
    public void A_field_that_would_be_a_formula_is_written_as_text(string note)
    {
        using var h = new TestDb();
        var csv = ExportWithNote(h, note);

        // The apostrophe is what tells a spreadsheet the cell is text.
        Assert.Contains("'" + note.TrimStart(), csv.Replace("\"", ""));
        Assert.DoesNotContain("\n=", csv);
    }

    [Fact]
    public void Ordinary_notes_are_left_exactly_as_typed()
    {
        using var h = new TestDb();
        var csv = ExportWithNote(h, "Paid in full, thanks");

        Assert.Contains("Paid in full, thanks", csv);
        Assert.DoesNotContain("'Paid", csv);
    }

    [Fact]
    public void Commas_and_quotes_in_a_note_still_survive_the_round_trip()
    {
        using var h = new TestDb();
        var csv = ExportWithNote(h, "Said \"deck belt\", also blades");

        // Quoted field, with the inner quotes doubled the way a CSV reader expects.
        Assert.Contains("\"Said \"\"deck belt\"\", also blades\"", csv);
    }

    private static string ExportWithNote(TestDb h, string note)
    {
        var customerId = h.NewCustomer();
        var ticketId = h.NewTicket(customerId);

        h.Money.Insert(new Payment
        {
            CustomerId = customerId,
            TicketId = ticketId,
            AmountCents = 5000,
            Method = "Cash",
            Note = note,
            PaidOn = DateTime.Today.ToString("yyyy-MM-dd")
        });

        return h.Money.ExportCsv(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
    }
}

/// <summary>
/// The Google client secret is not shown on the Settings screen any more, which makes an empty
/// box mean "unchanged". Getting that wrong would disconnect the shop's calendar the next time
/// anybody saved the page.
/// </summary>
public class ClientSecretTests
{
    [Fact]
    public void Saving_with_the_box_left_empty_keeps_the_secret_on_file()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        h.Settings.Set(SettingsStore.GoogleClientSecret, "GOCSPX-the-real-one");

        Assert.Equal("GOCSPX-the-real-one", auth.ResolveClientSecret(""));
        Assert.Equal("GOCSPX-the-real-one", auth.ResolveClientSecret(null));
        Assert.Equal("GOCSPX-the-real-one", auth.ResolveClientSecret("   "));
    }

    [Fact]
    public void Typing_a_new_secret_replaces_the_old_one()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        h.Settings.Set(SettingsStore.GoogleClientSecret, "GOCSPX-old");

        Assert.Equal("GOCSPX-new", auth.ResolveClientSecret("  GOCSPX-new  "));
    }

    [Fact]
    public void Whether_a_secret_exists_is_answerable_without_handing_it_over()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        Assert.False(auth.HasClientSecret);

        h.Settings.Set(SettingsStore.GoogleClientSecret, "GOCSPX-something");
        Assert.True(auth.HasClientSecret);
    }
}
