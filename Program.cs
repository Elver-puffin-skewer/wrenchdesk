using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<CustomerRepo>();
builder.Services.AddScoped<TicketRepo>();
builder.Services.AddScoped<MoneyRepo>();
builder.Services.AddScoped<ScheduleRepo>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<GoogleAuthService>();
builder.Services.AddScoped<CalendarSyncService>();
builder.Services.AddHostedService<BackupBackgroundService>();
builder.Services.AddHostedService<CalendarSyncBackgroundService>();

var app = builder.Build();

// Bring the schema up to date before anything can serve a request.
app.Services.GetRequiredService<Db>().Migrate();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ---- Static assets ----
// Served from resources compiled into the .exe rather than a wwwroot folder, so the program
// really is a single file. In development the files on disk are picked up first by
// UseStaticFiles above; these endpoints are what a published build uses.

app.MapGet("/app.css", () => EmbeddedAsset("app.css", "text/css"));
app.MapGet("/favicon.ico", () => EmbeddedAsset("favicon.ico", "image/x-icon"));

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

    return Results.Redirect(auth.BuildAuthorizationUrl(GoogleRedirectUri(request)));
});

app.MapGet("/google/callback", async (HttpRequest request, GoogleAuthService auth,
    string? code, string? error, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"/settings?google=denied&detail={Uri.EscapeDataString(error)}");

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

// ---- Startup banner + browser launch ----

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    var db = app.Services.GetRequiredService<Db>();
    var localUrl = $"http://localhost:{port}";

    Console.WriteLine();
    Console.WriteLine("  WrenchDesk is running.");
    Console.WriteLine($"  On this PC:      {localUrl}");

    if (lanEnabled)
    {
        foreach (var ip in LocalAddresses())
            Console.WriteLine($"  Phone / tablet:  http://{ip}:{port}");
    }

    Console.WriteLine($"  Data file:       {db.DatabasePath}");
    Console.WriteLine($"  Backups:         {db.BackupDirectory}");
    Console.WriteLine();
    Console.WriteLine("  Leave this window open while the shop is using it. Close it to stop.");
    Console.WriteLine();

    if (builder.Configuration.GetValue("WrenchDesk:OpenBrowser", true))
    {
        try
        {
            Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Headless or locked-down machines just show the URL above instead.
            Console.WriteLine($"  (Could not open a browser automatically: {ex.Message})");
        }
    }
});

app.Run();

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

/// <summary>LAN addresses this PC can be reached at, so the tablet URL can be printed on startup.</summary>
static IEnumerable<string> LocalAddresses()
{
    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up) continue;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

        foreach (var addr in nic.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(addr.Address)) continue;
            yield return addr.Address.ToString();
        }
    }
}
