using WrenchDesk.Data;
using WrenchDesk.Services.Google;

namespace WrenchDesk.Tests;

/// <summary>
/// The key file is pasted in by hand from a downloaded .json, so the common mistakes — pasting the
/// wrong file, or only part of one — need to be caught at the point of paste with an explanation,
/// not hours later as an authentication failure nobody can interpret.
/// </summary>
public class ServiceAccountKeyTests
{
    private const string ValidKey = """
        {
          "type": "service_account",
          "project_id": "wrenchdesk-test",
          "private_key_id": "abc123",
          "private_key": "-----BEGIN PRIVATE KEY-----\nnotarealkey\n-----END PRIVATE KEY-----\n",
          "client_email": "wrenchdesk@wrenchdesk-test.iam.gserviceaccount.com",
          "client_id": "123456789"
        }
        """;

    [Fact]
    public void A_proper_key_file_is_accepted()
    {
        Assert.Null(GoogleAuthService.ValidateServiceAccountJson(ValidKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_pasted_asks_for_the_file(string? json)
    {
        var problem = GoogleAuthService.ValidateServiceAccountJson(json);

        Assert.NotNull(problem);
        Assert.Contains("key file", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_that_is_not_json_says_so_plainly()
    {
        var problem = GoogleAuthService.ValidateServiceAccountJson("wrenchdesk@x.iam.gserviceaccount.com");

        Assert.NotNull(problem);
        Assert.Contains("copy everything", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_oauth_client_file_is_rejected_with_the_difference_explained()
    {
        // Downloaded from the OAuth section rather than the service account — an easy mix-up,
        // since both arrive as a .json from the same Credentials page.
        var oauthClientFile = """
            { "installed": { "client_id": "123.apps.googleusercontent.com", "client_secret": "GOCSPX-x" } }
            """;

        var problem = GoogleAuthService.ValidateServiceAccountJson(oauthClientFile);

        Assert.NotNull(problem);
        Assert.Contains("service_account", problem);
    }

    [Fact]
    public void A_key_file_missing_its_private_key_is_rejected()
    {
        var truncated = """
            { "type": "service_account", "client_email": "x@y.iam.gserviceaccount.com" }
            """;

        var problem = GoogleAuthService.ValidateServiceAccountJson(truncated);

        Assert.NotNull(problem);
        Assert.Contains("private key", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_address_to_share_the_calendar_with_is_read_from_the_key()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.GoogleServiceAccountJson, ValidKey);

        var auth = new GoogleAuthService(h.Settings);

        Assert.True(auth.UsesServiceAccount);
        Assert.True(auth.IsConnected);
        Assert.Equal("wrenchdesk@wrenchdesk-test.iam.gserviceaccount.com", auth.ServiceAccountEmail);
    }

    [Fact]
    public void With_no_key_there_is_no_address_and_nothing_is_connected()
    {
        using var h = new TestDb();
        var auth = new GoogleAuthService(h.Settings);

        Assert.False(auth.UsesServiceAccount);
        Assert.False(auth.IsConnected);
        Assert.Equal("", auth.ServiceAccountEmail);
    }

    [Fact]
    public void Removing_the_connection_clears_the_key_as_well_as_the_sign_in()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.GoogleServiceAccountJson, ValidKey);
        h.Settings.Set(SettingsStore.GoogleSyncEnabled, "true");

        var auth = new GoogleAuthService(h.Settings);
        auth.Disconnect();

        Assert.False(auth.UsesServiceAccount);
        Assert.Equal("", h.Settings.Get(SettingsStore.GoogleServiceAccountJson));
        Assert.False(h.Settings.GetBool(SettingsStore.GoogleSyncEnabled));
    }

    [Fact]
    public void A_damaged_key_yields_no_address_rather_than_throwing()
    {
        using var h = new TestDb();
        h.Settings.Set(SettingsStore.GoogleServiceAccountJson, "{ not json at all");

        var auth = new GoogleAuthService(h.Settings);

        // Still counts as configured — the failure surfaces when it is used, with a real message.
        Assert.True(auth.UsesServiceAccount);
        Assert.Equal("", auth.ServiceAccountEmail);
    }
}
