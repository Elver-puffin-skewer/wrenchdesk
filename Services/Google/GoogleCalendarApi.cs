using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.Net;
using System.Text.Json;
using WrenchDesk.Data;

namespace WrenchDesk.Services.Google;

/// <summary>
/// Holds the OAuth tokens in the shop's own database rather than a file, so a backup carries the
/// connection with it and there is nothing extra to look after.
/// </summary>
public class SettingsDataStore : IDataStore
{
    private readonly SettingsStore _settings;

    public SettingsDataStore(SettingsStore settings) => _settings = settings;

    public Task StoreAsync<T>(string key, T value)
    {
        _settings.Set(SettingsStore.GoogleTokenJson, JsonSerializer.Serialize(value));
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        _settings.Set(SettingsStore.GoogleTokenJson, "");
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var json = _settings.Get(SettingsStore.GoogleTokenJson);
        if (string.IsNullOrWhiteSpace(json)) return Task.FromResult<T>(default!);

        try
        {
            return Task.FromResult(JsonSerializer.Deserialize<T>(json)!);
        }
        catch (JsonException)
        {
            return Task.FromResult<T>(default!);
        }
    }

    public Task ClearAsync()
    {
        _settings.Set(SettingsStore.GoogleTokenJson, "");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds the OAuth flow and hands back a ready calendar client.
///
/// The credentials belong to the shop's own Google Cloud project — nothing is baked into the app,
/// which is what lets this live in a public repo.
/// </summary>
public class GoogleAuthService
{
    private readonly SettingsStore _settings;

    /// <summary>Full calendar access: the sync both reads and writes events, and can create a calendar.</summary>
    private static readonly string[] Scopes = { CalendarService.Scope.Calendar };

    public GoogleAuthService(SettingsStore settings) => _settings = settings;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Get(SettingsStore.GoogleClientId)) &&
        !string.IsNullOrWhiteSpace(_settings.Get(SettingsStore.GoogleClientSecret));

    public bool IsConnected => !string.IsNullOrWhiteSpace(_settings.Get(SettingsStore.GoogleTokenJson));

    public GoogleAuthorizationCodeFlow CreateFlow() =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _settings.Get(SettingsStore.GoogleClientId).Trim(),
                ClientSecret = _settings.Get(SettingsStore.GoogleClientSecret).Trim()
            },
            Scopes = Scopes,
            DataStore = new SettingsDataStore(_settings)
        });

    /// <summary>
    /// The consent URL to send the browser to. "offline" plus "consent" is what produces a refresh
    /// token — without it the connection would die as soon as the first hour was up.
    /// </summary>
    public string BuildAuthorizationUrl(string redirectUri)
    {
        var flow = CreateFlow();
        var request = flow.CreateAuthorizationCodeRequest(redirectUri);

        if (request is GoogleAuthorizationCodeRequestUrl google)
        {
            google.AccessType = "offline";
            google.Prompt = "consent";
        }

        return request.Build().ToString();
    }

    public async Task ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var flow = CreateFlow();
        await flow.ExchangeCodeForTokenAsync("shop", code, redirectUri, ct);
    }

    /// <summary>Builds an authorised calendar client, or throws if the shop needs to reconnect.</summary>
    public async Task<ICalendarApi> CreateApiAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new CalendarAuthException("Google Calendar is not set up yet. Add the client ID and secret in Settings.");

        var flow = CreateFlow();
        var token = await flow.LoadTokenAsync("shop", ct);

        if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new CalendarAuthException("Not connected to Google Calendar yet.");

        var credential = new UserCredential(flow, "shop", token);

        if (credential.Token.IsStale)
        {
            try
            {
                if (!await credential.RefreshTokenAsync(ct))
                    throw new CalendarAuthException("Google refused to renew the connection. Reconnect in Settings.");
            }
            catch (TokenResponseException ex)
            {
                throw new CalendarAuthException(
                    "Google has revoked this connection. Reconnect in Settings. "
                  + "(If the OAuth consent screen is still in Testing, Google expires it every 7 days.)", ex);
            }
        }

        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "WrenchDesk"
        });

        return new GoogleCalendarApi(service);
    }

    public void Disconnect()
    {
        _settings.SetAll(new Dictionary<string, string>
        {
            [SettingsStore.GoogleTokenJson] = "",
            [SettingsStore.GoogleSyncToken] = "",
            [SettingsStore.GoogleSyncEnabled] = "false",
            [SettingsStore.GoogleCalendarId] = "",
            [SettingsStore.GoogleCalendarName] = "",
            [SettingsStore.GoogleNeedsReconnect] = "false",
            [SettingsStore.GoogleLastError] = ""
        });
    }
}

/// <summary>The real Google-backed calendar client.</summary>
public class GoogleCalendarApi : ICalendarApi
{
    private readonly CalendarService _service;

    /// <summary>Marks events this app created, so imports can be told from our own pushes.</summary>
    private const string AppointmentIdProperty = "wrenchdeskAppointmentId";

    public GoogleCalendarApi(CalendarService service) => _service = service;

    public async Task<IReadOnlyList<CalendarSummary>> ListCalendarsAsync(CancellationToken ct)
    {
        try
        {
            var list = await _service.CalendarList.List().ExecuteAsync(ct);

            return list.Items?
                .Select(c => new CalendarSummary(
                    c.Id,
                    c.Summary ?? c.Id,
                    c.Primary ?? false,
                    c.AccessRole is "owner" or "writer"))
                .OrderByDescending(c => c.IsPrimary)
                .ThenBy(c => c.Name)
                .ToList() ?? new List<CalendarSummary>();
        }
        catch (GoogleApiException ex) when (IsAuthFailure(ex))
        {
            throw new CalendarAuthException("Google rejected the connection. Reconnect in Settings.", ex);
        }
    }

    public async Task<CalendarSummary> CreateCalendarAsync(string name, CancellationToken ct)
    {
        var created = await _service.Calendars.Insert(new Calendar
        {
            Summary = name,
            TimeZone = IanaTimeZone()
        }).ExecuteAsync(ct);

        return new CalendarSummary(created.Id, created.Summary ?? name, false, true);
    }

    public async Task<CalendarEventData> CreateEventAsync(string calendarId, CalendarEventData data, CancellationToken ct)
    {
        try
        {
            var created = await _service.Events.Insert(ToGoogleEvent(data), calendarId).ExecuteAsync(ct);
            return FromGoogleEvent(created);
        }
        catch (GoogleApiException ex) when (IsAuthFailure(ex))
        {
            throw new CalendarAuthException("Google rejected the connection. Reconnect in Settings.", ex);
        }
    }

    public async Task<CalendarEventData?> UpdateEventAsync(
        string calendarId, string eventId, CalendarEventData data, CancellationToken ct)
    {
        try
        {
            var updated = await _service.Events.Update(ToGoogleEvent(data), calendarId, eventId).ExecuteAsync(ct);
            return FromGoogleEvent(updated);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Deleted in Google since we last looked — the caller decides whether to recreate it.
            return null;
        }
        catch (GoogleApiException ex) when (IsAuthFailure(ex))
        {
            throw new CalendarAuthException("Google rejected the connection. Reconnect in Settings.", ex);
        }
    }

    public async Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct)
    {
        try
        {
            await _service.Events.Delete(calendarId, eventId).ExecuteAsync(ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Already gone is the outcome we wanted.
        }
        catch (GoogleApiException ex) when (IsAuthFailure(ex))
        {
            throw new CalendarAuthException("Google rejected the connection. Reconnect in Settings.", ex);
        }
    }

    public async Task<CalendarChangePage> ListChangesAsync(
        string calendarId, string? syncToken, string? pageToken, DateTime? timeMin, CancellationToken ct)
    {
        var request = _service.Events.List(calendarId);
        request.MaxResults = 250;
        request.ShowDeleted = true;

        if (!string.IsNullOrWhiteSpace(syncToken))
        {
            request.SyncToken = syncToken;
        }
        else
        {
            // A first read is bounded, or connecting an old calendar would import years of history.
            request.TimeMinDateTimeOffset = new DateTimeOffset(timeMin ?? DateTime.Now.AddDays(-30));
            request.SingleEvents = true;
        }

        if (!string.IsNullOrWhiteSpace(pageToken)) request.PageToken = pageToken;

        Events response;
        try
        {
            response = await request.ExecuteAsync(ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
        {
            throw new SyncTokenExpiredException("The Google sync token is no longer valid.");
        }
        catch (GoogleApiException ex) when (IsAuthFailure(ex))
        {
            throw new CalendarAuthException("Google rejected the connection. Reconnect in Settings.", ex);
        }

        var events = response.Items?.Select(FromGoogleEvent).ToList() ?? new List<CalendarEventData>();
        return new CalendarChangePage(events, response.NextPageToken, response.NextSyncToken);
    }

    private static bool IsAuthFailure(GoogleApiException ex) =>
        ex.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static Event ToGoogleEvent(CalendarEventData data)
    {
        var timeZone = IanaTimeZone();

        return new Event
        {
            Summary = data.Summary,
            Description = data.Description,
            Location = data.Location,
            Status = data.IsCancelled ? "cancelled" : "confirmed",
            Start = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(data.Start, TimeZoneInfo.Local.GetUtcOffset(data.Start)),
                TimeZone = timeZone
            },
            End = new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(data.End, TimeZoneInfo.Local.GetUtcOffset(data.End)),
                TimeZone = timeZone
            },
            ExtendedProperties = data.WrenchDeskAppointmentId is null ? null : new Event.ExtendedPropertiesData
            {
                Private__ = new Dictionary<string, string>
                {
                    [AppointmentIdProperty] = data.WrenchDeskAppointmentId.Value.ToString()
                }
            }
        };
    }

    private static CalendarEventData FromGoogleEvent(Event ev)
    {
        var isAllDay = ev.Start?.DateTimeDateTimeOffset is null && !string.IsNullOrWhiteSpace(ev.Start?.Date);

        var start = ev.Start?.DateTimeDateTimeOffset?.LocalDateTime
                    ?? ParseDateOnly(ev.Start?.Date)
                    ?? DateTime.Now;

        var end = ev.End?.DateTimeDateTimeOffset?.LocalDateTime
                  ?? ParseDateOnly(ev.End?.Date)
                  ?? start.AddHours(1);

        long? appointmentId = null;
        var privateProps = ev.ExtendedProperties?.Private__;
        if (privateProps is not null
            && privateProps.TryGetValue(AppointmentIdProperty, out var raw)
            && long.TryParse(raw, out var parsed))
        {
            appointmentId = parsed;
        }

        return new CalendarEventData
        {
            Id = ev.Id ?? "",
            Summary = ev.Summary ?? "",
            Description = ev.Description ?? "",
            Location = ev.Location ?? "",
            Start = start,
            End = end,
            IsAllDay = isAllDay,
            IsCancelled = ev.Status == "cancelled",
            Updated = ev.UpdatedDateTimeOffset?.UtcDateTime.ToString("O") ?? "",
            WrenchDeskAppointmentId = appointmentId
        };
    }

    private static DateTime? ParseDateOnly(string? date) =>
        DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// Google wants IANA zone names; Windows reports its own. .NET can translate, and if it cannot,
    /// omitting the zone is safe because every timestamp we send carries an explicit offset.
    /// </summary>
    private static string? IanaTimeZone() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : null;
}
