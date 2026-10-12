using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using WrenchDesk.Data;

namespace WrenchDesk.Services;

/// <summary>
/// Who is using the shop right now.
///
/// Signing in is optional - a shop can run exactly as it always did, with no accounts at all -
/// so this is the one place that answers the question either way. With sign-in switched off
/// everybody is the shop itself, with an owner's run of the place; with it on, this is whoever
/// the cookie says it is. Nothing else in the app has to know which of those is true.
/// </summary>
public class CurrentUser
{
    private readonly AuthenticationStateProvider _auth;
    private readonly UserRepo _users;
    private readonly SettingsStore _settings;

    private User? _resolved;
    private bool _hasResolved;

    public CurrentUser(AuthenticationStateProvider auth, UserRepo users, SettingsStore settings)
    {
        _auth = auth;
        _users = users;
        _settings = settings;
    }

    /// <summary>Whether anybody has to sign in at all.</summary>
    public bool SignInRequired => _settings.GetBool(SettingsStore.AuthRequired);

    /// <summary>
    /// The account standing in for a shop that has not switched sign-in on. It is not in the
    /// database and never will be - it exists so every permission check has a real answer.
    /// </summary>
    public static User TheShop { get; } = new()
    {
        Id = 0,
        Username = "shop",
        DisplayName = "Shop",
        Role = UserRole.Owner
    };

    public async Task<User> GetAsync()
    {
        if (_hasResolved) return _resolved ?? TheShop;

        _hasResolved = true;

        if (!SignInRequired)
        {
            _resolved = TheShop;
            return _resolved;
        }

        var state = await _auth.GetAuthenticationStateAsync();
        var id = state.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (long.TryParse(id, out var userId))
        {
            var user = _users.Get(userId);

            // An account switched off or removed mid-session stops being signed in.
            if (user is { IsActive: true }) _resolved = user;
        }

        return _resolved ?? TheShop;
    }

    public async Task<string> RoleAsync() => (await GetAsync()).Role;

    public async Task<bool> CanSeeMoneyAsync() => UserRole.CanSeeMoney(await RoleAsync());
    public async Task<bool> CanChangeSettingsAsync() => UserRole.CanChangeSettings(await RoleAsync());
    public async Task<bool> CanManageUsersAsync() => UserRole.CanManageUsers(await RoleAsync());
    public async Task<bool> CanDeleteRecordsAsync() => UserRole.CanDeleteRecords(await RoleAsync());

    /// <summary>True when this is the stand-in rather than somebody who actually signed in.</summary>
    public async Task<bool> IsTheShopAsync() => (await GetAsync()).Id == 0;

    /// <summary>The claims a signed-in account is carried around by.</summary>
    public static ClaimsPrincipal ClaimsFor(User user) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }, "WrenchDesk"));
}

/// <summary>
/// Writes to the audit log on behalf of whoever is signed in, so no call site has to work out
/// who that was. With sign-in switched off entries are still written, attributed to the shop -
/// a log with gaps in it where the accounts were off is worse than useless.
/// </summary>
public class AuditWriter
{
    private readonly AuditRepo _audit;
    private readonly CurrentUser _current;

    public AuditWriter(AuditRepo audit, CurrentUser current)
    {
        _audit = audit;
        _current = current;
    }

    public async Task WriteAsync(string action, string entity = "", long? entityId = null, string detail = "")
    {
        var user = await _current.GetAsync();
        _audit.Write(user.Id == 0 ? null : user.Id, user.Name, action, entity, entityId, detail);
    }
}
