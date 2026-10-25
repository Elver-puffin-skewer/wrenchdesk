using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WrenchDesk.Components;
using WrenchDesk.Data;
using WrenchDesk.Services;
using WrenchDesk.Services.Google;

// Setup runs before anything else: this single .exe is also its own installer, so the shop
// downloads one file and double-clicks it. Returns true when there is nothing left to do here —
// the work is finished, or the freshly installed copy has taken over.
if (SelfInstall.HandleStartup(args)) return;

// Our own switches are not configuration keys, and the command-line provider rejects bare flags.
var hostArgs = args
    .Where(a => !SelfInstall.SetupFlags.Contains(a.TrimStart('-', '/').ToLowerInvariant()))
    .ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

// The shop PC runs this as a plain double-clicked program, so everything binds explicitly
// rather than relying on launch profiles that only exist during development.
var port = builder.Configuration.GetValue("WrenchDesk:Port", 5173);
var lanEnabled = builder.Configuration.GetValue("WrenchDesk:AllowLanAccess", true);
builder.WebHost.UseUrls($"http://{(lanEnabled ? "0.0.0.0" : "127.0.0.1")}:{port}");

// A second copy cannot serve the same port, and the shop has no way to tell that a program in
// the notification area is already running. Hand the click to the copy that is already up
// rather than starting one that is about to fail on the bind.
if (OperatingSystem.IsWindows() && !SingleInstance.TryClaim(port))
{
    SingleInstance.HandOver($"http://localhost:{port}",
        builder.Configuration.GetValue("WrenchDesk:OpenBrowser", true));
    return;
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Signing in is optional, but the machinery for it is always present - a shop that switches it on
// should not need a different build, and one that leaves it off pays nothing for it being here.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "wrenchdesk.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // A month, sliding. The tablet at the bench is signed into once and then used, rather
        // than asking for a password twenty times a day.
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<CustomerRepo>();
builder.Services.AddScoped<TicketRepo>();
builder.Services.AddScoped<MoneyRepo>();
builder.Services.AddScoped<ScheduleRepo>();
builder.Services.AddScoped<QuickItemRepo>();
builder.Services.AddScoped<PhotoRepo>();
builder.Services.AddScoped<CounterSaleRepo>();
builder.Services.AddScoped<UserRepo>();
builder.Services.AddScoped<AuditRepo>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<GoogleAuthService>();
builder.Services.AddScoped<CalendarSyncService>();
builder.Services.AddHostedService<BackupBackgroundService>();
builder.Services.AddHostedService<CalendarSyncBackgroundService>();

var app = builder.Build();

// Bring the schema up to date before anything can serve a request. An update that changes the
// shape of the records copies them first; if that copy cannot be written the update does not go
// ahead, and the shop gets a sentence they can act on rather than a crash with no window.
try
{
    app.Services.GetRequiredService<Db>().Migrate();
}
catch (SchemaUpgradeBlockedException ex)
{
    if (OperatingSystem.IsWindows())
    {
        System.Windows.Forms.MessageBox.Show(ex.Message, "WrenchDesk could not start",
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
    }
    else
    {
        Console.Error.WriteLine(ex.Message);
    }

    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Only when there is a wwwroot to serve. A published build has none — its assets are compiled in
// and served by the endpoints below — and calling this anyway logs an alarming
// "WebRootPath was not found ... static files may be unavailable" warning that means nothing.
if (Directory.Exists(app.Environment.WebRootPath ?? "")) app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Everything behind the sign-in, including the plain endpoints. Putting this here rather than
// on the pages means the CSV export and the photo files are covered by the same rule as the
// screens - an export anybody on the wifi can fetch is not a locked door.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    var alwaysOpen =
        path.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/app.css", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase);

    if (alwaysOpen || context.User?.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    var settings = context.RequestServices.GetRequiredService<SettingsStore>();
    if (!settings.GetBool(SettingsStore.AuthRequired))
    {
        await next();
        return;
    }

    // Sign-in is on but nobody has an account yet, which would lock the shop out of its own
    // records. The first-run screen is the way back in.
    var users = context.RequestServices.GetRequiredService<UserRepo>();
    if (!users.AnyExist())
    {
        await next();
        return;
    }

    context.Response.Redirect("/login?from=" + Uri.EscapeDataString(path));
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ---- Static assets ----
// Served from resources compiled into the .exe rather than a wwwroot folder, so the program
// really is a single file. In development the files on disk are picked up first by
// UseStaticFiles above; these endpoints are what a published build uses.

app.MapGet("/app.css", () => EmbeddedAsset("app.css", "text/css"));
app.MapGet("/favicon.ico", () => EmbeddedAsset("favicon.ico", "image/x-icon"));

// Photos of machines, served from the Photos folder beside the database. The repository decides
// whether a name is one of ours before anything touches the disk, so a name dressed up to climb
// out of that folder gets a 404 like any other name it does not recognise.
app.MapGet("/photos/{fileName}", (string fileName, PhotoRepo photos) =>
{
    var path = photos.ResolveFile(fileName);

    return path is null
        ? Results.NotFound()
        : Results.File(path, PhotoRepo.ContentTypeFor(path));
});

// The shop's own logo is an optional drop-in, kept with their data so it survives an update.
app.MapGet("/logo.png", (Db db) =>
{
    var path = Path.Combine(db.DataDirectory, "logo.png");
    return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
});

// ---- File exports ----
// Plain endpoints rather than Blazor pages, so the browser gets a real download.

app.MapGet("/export/payments.csv", (MoneyRepo money, string? from, string? to) =>
{
    var start = ParseDate(from) ?? DateTime.Now.AddDays(-30);
    var end = ParseDate(to) ?? DateTime.Now;
    var csv = money.ExportCsv(start, end);

    return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv",
        $"payments-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.csv");
});

app.MapGet("/export/schedule.ics", (ScheduleRepo schedule, SettingsStore settings, int days) =>
{
    var window = days <= 0 ? 60 : days;
    var rows = schedule.InRange(DateTime.Now.AddDays(-7), DateTime.Now.AddDays(window));
    var ics = CalendarLinks.BuildIcs(rows, settings.Get(SettingsStore.ShopName));

    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar", "wrenchdesk-schedule.ics");
});

app.MapGet("/export/appointment/{id:long}.ics", (long id, ScheduleRepo schedule, SettingsStore settings) =>
{
    var row = schedule.GetRow(id);
    if (row is null) return Results.NotFound();

    var ics = CalendarLinks.BuildIcs(new[] { row }, settings.Get(SettingsStore.ShopName));
    return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar", $"appointment-{id}.ics");
});

// ---- Signing in ----
// Plain endpoints rather than Blazor components on purpose: issuing the cookie has to happen
// while the response can still carry a header, which is not true inside an interactive
// component. The sign-in screen posts a normal browser form to these.

app.MapPost("/auth/login", async (HttpContext http, UserRepo users, AuditRepo audit,
    [Microsoft.AspNetCore.Mvc.FromForm] string? username,
    [Microsoft.AspNetCore.Mvc.FromForm] string? password,
    [Microsoft.AspNetCore.Mvc.FromForm] string? from) =>
{
    var user = users.Authenticate(username, password);

    if (user is null)
    {
        audit.Write(null, username ?? "(unknown)", "Sign-in refused", AuditEntity.Session);

        // Deliberately vague: which half was wrong is not the signed-out visitor's business.
        return Results.Redirect("/login?bad=1");
    }

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        CurrentUser.ClaimsFor(user),
        new AuthenticationProperties { IsPersistent = true });

    users.StampLogin(user.Id);
    audit.Write(user.Id, user.Name, "Signed in", AuditEntity.Session);

    // Only ever back into this app, never to wherever a crafted link points.
    var target = !string.IsNullOrWhiteSpace(from) && from.StartsWith('/') && !from.StartsWith("//")
        ? from
        : "/";

    return Results.Redirect(target);
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext http, AuditRepo audit, UserRepo users) =>
{
    var name = http.User.Identity?.Name;
    var user = users.ByUsername(name);

    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    audit.Write(user?.Id, user?.Name ?? name ?? "", "Signed out", AuditEntity.Session);

    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapPost("/auth/first-run", async (HttpContext http, UserRepo users, SettingsStore settings,
    AuditRepo audit,
    [Microsoft.AspNetCore.Mvc.FromForm] string? username,
    [Microsoft.AspNetCore.Mvc.FromForm] string? displayName,
    [Microsoft.AspNetCore.Mvc.FromForm] string? password) =>
{
    // Only ever the very first account. Once one exists this door is shut, or anybody could walk
    // up and make themselves an owner.
    if (users.AnyExist()) return Results.Redirect("/login");

    var problem = users.Create(username ?? "", displayName ?? "", password, UserRole.Owner);
    if (problem is not null) return Results.Redirect("/login?setup=" + Uri.EscapeDataString(problem));

    settings.Set(SettingsStore.AuthRequired, "true");

    var created = users.ByUsername(username);
    if (created is not null)
    {
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CurrentUser.ClaimsFor(created),
            new AuthenticationProperties { IsPersistent = true });

        users.StampLogin(created.Id);
        audit.Write(created.Id, created.Name, "Created the first account and switched sign-in on",
            AuditEntity.User, created.Id);
    }

    return Results.Redirect("/");
}).DisableAntiforgery();

// ---- Google Calendar OAuth ----
// Google redirects a browser back here, which a Blazor component cannot receive, so these are
// plain endpoints. The redirect URI is always loopback on this PC, never the LAN address —
// Google only permits http:// on localhost, and it must match the Cloud console exactly.

static string GoogleRedirectUri(HttpRequest request) =>
    $"{request.Scheme}://localhost:{request.Host.Port ?? 80}/google/callback";

app.MapGet("/google/connect", (HttpRequest request, GoogleAuthService auth) =>
{
    if (!auth.IsConfigured)
        return Results.Redirect("/settings?google=notconfigured");

    return Results.Redirect(auth.BeginAuthorization(GoogleRedirectUri(request)));
});

app.MapGet("/google/callback", async (HttpRequest request, GoogleAuthService auth,
    string? code, string? state, string? error, CancellationToken ct) =>
{
    // Checked before anything else, and always consumed, so an attempt that got this far cannot
    // be replayed. Google returns the state alongside an error too, hence checking it first.
    var stateOk = auth.ConsumeAuthorizationState(state);

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"/settings?google=denied&detail={Uri.EscapeDataString(error)}");

    if (!stateOk)
        return Results.Redirect("/settings?google=badstate");

    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect("/settings?google=nocode");

    try
    {
        await auth.ExchangeCodeAsync(code, GoogleRedirectUri(request), ct);
        return Results.Redirect("/settings?google=connected");
    }
    catch (Exception ex)
    {
        return Results.Redirect($"/settings?google=failed&detail={Uri.EscapeDataString(ex.Message)}");
    }
});

// ---- Startup ----

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var database = app.Services.GetRequiredService<Db>();
var shopName = app.Services.GetRequiredService<SettingsStore>().Get(SettingsStore.ShopName);

var localUrl = $"http://localhost:{port}";
var lanUrls = lanEnabled ? NetworkInfo.LanUrls(port) : new List<string>();
var openBrowser = builder.Configuration.GetValue("WrenchDesk:OpenBrowser", true);

// Diagnostics only. There is no console in a normal run, so this attaches one on request.
var wantConsole = args.Any(a => a.TrimStart('-', '/').Equals("console", StringComparison.OrdinalIgnoreCase));
if (wantConsole) ConsoleWindow.Attach();

lifetime.ApplicationStarted.Register(() =>
{
    if (wantConsole)
    {
        Console.WriteLine();
        Console.WriteLine("  WrenchDesk is running.");
        Console.WriteLine($"  On this PC:      {localUrl}");
        foreach (var url in lanUrls) Console.WriteLine($"  Phone / tablet:  {url}");
        Console.WriteLine($"  Data file:       {database.DatabasePath}");
        Console.WriteLine();
    }

    if (openBrowser)
    {
        try
        {
            Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // A locked-down machine still has the tray icon to open it from.
        }
    }
});

// The tray icon needs its own STA thread with a message pump; the web host keeps the main one.
// Without it the program would run completely invisibly, with no way to stop it but Task Manager.
ShopTray? tray = null;
Thread? trayThread = null;

if (OperatingSystem.IsWindows() && builder.Configuration.GetValue("WrenchDesk:ShowTrayIcon", true))
{
    trayThread = new Thread(() =>
    {
        tray = new ShopTray(shopName, localUrl, lanUrls, database.DataDirectory,
            stopRequested: () => lifetime.StopApplication());

        if (openBrowser) tray.ShowStartedNotice();

        System.Windows.Forms.Application.Run(tray);

        // Closing the tray is how someone quits, so bring the web host down with it.
        lifetime.StopApplication();
    })
    {
        IsBackground = true,
        Name = "WrenchDesk tray"
    };

    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.Start();
}

app.Run();

// Stopping through Ctrl+C or a shutdown should take the icon away too, rather than leaving a
// dead badge in the notification area until someone hovers over it.
if (tray is not null) System.Windows.Forms.Application.Exit();

/// <summary>Reads a file compiled into the assembly, cached by the browser like any other asset.</summary>
static IResult EmbeddedAsset(string name, string contentType)
{
    var assembly = typeof(Program).Assembly;
    var stream = assembly.GetManifestResourceStream($"WrenchDesk.wwwroot.{name}");

    return stream is null
        ? Results.NotFound()
        : Results.Stream(stream, contentType);
}

static DateTime? ParseDate(string? value) =>
    DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d) ? d : null;
