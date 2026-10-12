using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace WrenchDesk.Data;

/// <summary>
/// What someone is allowed to do. Three of them, because a shop is three jobs: the person who
/// owns it, whoever runs the office and the money, and whoever is on the tools.
/// </summary>
public static class UserRole
{
    /// <summary>Everything, including who else has an account.</summary>
    public const string Owner = "Owner";

    /// <summary>The office: money, reports and settings, but not handing out accounts.</summary>
    public const string Admin = "Admin";

    /// <summary>The bench: tickets, customers, machines, parts and the schedule.</summary>
    public const string Tech = "Tech";

    public static readonly string[] All = { Owner, Admin, Tech };

    public static string Describe(string role) => role switch
    {
        Owner => "Everything, including adding and removing accounts",
        Admin => "Money, reports and settings - the office side - but not accounts",
        _ => "Tickets, customers, machines, parts and the schedule"
    };

    public static bool CanManageUsers(string? role) => role == Owner;
    public static bool CanSeeMoney(string? role) => role is Owner or Admin;
    public static bool CanChangeSettings(string? role) => role is Owner or Admin;

    /// <summary>Removing a customer or a ticket outright, as opposed to archiving or closing it.</summary>
    public static bool CanDeleteRecords(string? role) => role is Owner or Admin;
}

public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Empty when this account signs in without a password.</summary>
    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = UserRole.Tech;
    public bool IsActive { get; set; } = true;
    public string CreatedUtc { get; set; } = "";
    public string LastLoginUtc { get; set; } = "";

    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    public DateTime? LastLogin =>
        DateTime.TryParse(LastLoginUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime() : null;
}

/// <summary>
/// The shop accounts.
///
/// Passwords are PBKDF2-SHA256 with a salt of their own, using what the framework already has
/// rather than another dependency. An account with no password stores an empty hash and is signed
/// into by picking a name - which some shops will want, and which is warned about where it is
/// chosen rather than buried in a manual.
/// </summary>
public class UserRepo
{
    private readonly Db _db;

    public UserRepo(Db db) => _db = db;

    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>The dollar sign the hash parts are joined by, written this way to keep it out of SQL-looking text.</summary>
    private const char Separator = (char)36;

    public List<User> All()
    {
        using var conn = _db.Open();
        return conn.Query<User>("SELECT * FROM users ORDER BY is_active DESC, username;").ToList();
    }

    /// <summary>Accounts that can be picked on the sign-in screen.</summary>
    public List<User> Active()
    {
        using var conn = _db.Open();
        return conn.Query<User>("SELECT * FROM users WHERE is_active = 1 ORDER BY username;").ToList();
    }

    public User? Get(long id)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<User>("SELECT * FROM users WHERE id = @id;", new { id });
    }

    public User? ByUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<User>(
            "SELECT * FROM users WHERE username = @username;", new { username = username.Trim() });
    }

    /// <summary>True when nobody has an account yet, which is what puts the first-run screen up.</summary>
    public bool AnyExist()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM users;") > 0;
    }

    public int CountOwners()
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM users WHERE role = @owner AND is_active = 1;", new { owner = UserRole.Owner });
    }

    /// <summary>Creates an account. Returns a reason when it refuses, or null when it worked.</summary>
    public string? Create(string username, string displayName, string? password, string role)
    {
        username = (username ?? "").Trim();

        if (string.IsNullOrWhiteSpace(username)) return "Give the account a name to sign in with.";
        if (username.Length > 60) return "That name is too long.";
        if (!UserRole.All.Contains(role)) return "Pick what this person is allowed to do.";
        if (ByUsername(username) is not null) return "There is already an account called " + username + ".";

        using var conn = _db.Open();
        conn.Execute(
            "INSERT INTO users (username, display_name, password_hash, role, is_active, created_utc) " +
            "VALUES (@username, @displayName, @hash, @role, 1, @now);",
            new
            {
                username,
                displayName = (displayName ?? "").Trim(),
                hash = string.IsNullOrEmpty(password) ? "" : HashPassword(password),
                role,
                now = DateTime.UtcNow.ToString("O")
            });

        return null;
    }

    public string? UpdateDetails(long id, string displayName, string role, bool isActive)
    {
        if (!UserRole.All.Contains(role)) return "Pick what this person is allowed to do.";

        var user = Get(id);
        if (user is null) return "That account no longer exists.";

        // The last owner cannot stop being one, or nobody can hand out accounts ever again.
        var losingOwner = user.Role == UserRole.Owner && (role != UserRole.Owner || !isActive);
        if (losingOwner && CountOwners() <= 1)
            return "This is the only owner account. Make somebody else an owner first.";

        using var conn = _db.Open();
        conn.Execute(
            "UPDATE users SET display_name = @displayName, role = @role, is_active = @isActive WHERE id = @id;",
            new { id, displayName = (displayName ?? "").Trim(), role, isActive });

        return null;
    }

    /// <summary>Sets or clears a password. An empty one means this account signs in by picking a name.</summary>
    public string? SetPassword(long id, string? password)
    {
        if (Get(id) is null) return "That account no longer exists.";

        using var conn = _db.Open();
        conn.Execute("UPDATE users SET password_hash = @hash WHERE id = @id;",
            new { id, hash = string.IsNullOrEmpty(password) ? "" : HashPassword(password) });

        return null;
    }

    public string? Delete(long id)
    {
        var user = Get(id);
        if (user is null) return null;

        if (user.Role == UserRole.Owner && CountOwners() <= 1)
            return "This is the only owner account. It cannot be removed.";

        using var conn = _db.Open();
        conn.Execute("DELETE FROM users WHERE id = @id;", new { id });
        return null;
    }

    public void StampLogin(long id)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE users SET last_login_utc = @now WHERE id = @id;",
            new { id, now = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>
    /// Checks a sign-in. Returns the account when it is good, null otherwise.
    ///
    /// An account with no password is signed into by name alone - that is the whole point of the
    /// option - but one that does have a password is never let through without it.
    /// </summary>
    public User? Authenticate(string? username, string? password)
    {
        var user = ByUsername(username);
        if (user is null || !user.IsActive) return null;

        if (!user.HasPassword) return user;
        if (string.IsNullOrEmpty(password)) return null;

        return VerifyPassword(password, user.PasswordHash) ? user : null;
    }

    // ---- Hashing ----

    /// <summary>Stored as pbkdf2, sha256, iterations, salt and hash, joined by dollars, bytes in base64.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Join(Separator, "pbkdf2", "sha256", Iterations.ToString(),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;

        var parts = stored.Split(Separator);
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256") return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time, so a wrong password cannot be narrowed down by how long the answer took.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
