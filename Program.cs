using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WrenchDesk.Components;
using WrenchDesk.Data;
using WrenchDesk.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddHostedService<BackupBackgroundService>();

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
