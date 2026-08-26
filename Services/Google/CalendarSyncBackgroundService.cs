using WrenchDesk.Data;

namespace WrenchDesk.Services.Google;

/// <summary>
/// Polls Google on the configured interval. Google can only push changes to a public HTTPS address,
/// which a shop PC behind a home router does not have — so the app asks, rather than being told.
/// Nothing happens until the shop connects an account and switches sync on.
/// </summary>
public class CalendarSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CalendarSyncBackgroundService> _log;

    public CalendarSyncBackgroundService(IServiceProvider services, ILogger<CalendarSyncBackgroundService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before reaching for the network.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(5);

            try
            {
                using var scope = _services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();

                var minutes = settings.GetInt(SettingsStore.GoogleSyncIntervalMin);
                interval = TimeSpan.FromMinutes(Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 240));

                var enabled = settings.GetBool(SettingsStore.GoogleSyncEnabled);
                var calendarId = settings.Get(SettingsStore.GoogleCalendarId);

                // A revoked connection needs a person, so stop hammering Google until they act.
                var needsReconnect = settings.GetBool(SettingsStore.GoogleNeedsReconnect);

                if (enabled && !needsReconnect && !string.IsNullOrWhiteSpace(calendarId))
                {
                    var auth = scope.ServiceProvider.GetRequiredService<GoogleAuthService>();
                    var sync = scope.ServiceProvider.GetRequiredService<CalendarSyncService>();

                    var api = await auth.CreateApiAsync(stoppingToken);
                    var report = await sync.SyncAsync(api, calendarId, stoppingToken);

                    if (report.TotalChanges > 0)
                        _log.LogInformation("Calendar sync: {Summary}", report.Describe());
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never take the app down over a calendar problem — the shop still needs to write tickets.
                _log.LogError(ex, "Calendar sync check failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
