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

    /// <summary>True when a service account key has been pasted in, which needs no sign-in at all.</summary>
    public bool UsesServiceAccount =>
        !string.IsNullOrWhiteSpace(_settings.Get(SettingsStore.GoogleServiceAccountJson));

    public bool IsConnected =>
        UsesServiceAccount || !string.IsNullOrWhiteSpace(_settings.Get(SettingsStore.GoogleTokenJson));

    /// <summary>
    /// The address the shop shares its calendar with. A service account is a robot account with its
    /// own email; giving that address access to the calendar is what lets WrenchDesk in — the same
    /// gesture as sharing with a colleague, and reversible the same way.
    /// </summary>
    public string ServiceAccountEmail
    {
        get
        {
            var json = _settings.Get(SettingsStore.GoogleServiceAccountJson);
            if (string.IsNullOrWhiteSpace(json)) return "";

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("client_email", out var email)
                    ? email.GetString() ?? ""
                    : "";
            }
            catch (System.Text.Json.JsonException)
            {
                return "";
            }
        }
    }

    /// <summary>
    /// Checks a pasted key file before saving it, so a wrong paste is caught here rather than
    /// surfacing later as an authentication failure. Returns null when the key looks usable.
    /// </summary>
    public static string? ValidateServiceAccountJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "Paste the contents of the key file you downloaded from Google.";

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return "That does not look like the key file. Open the .json file Google downloaded and "
                 + "copy everything in it, including the { and } at the ends.";
        }

        using (doc)
        {
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "service_account")
                return "That is the wrong kind of key file. It should say \"type\": \"service_account\" "
                     + "inside. A client ID file from the OAuth section will not work here.";

            if (!root.TryGetProperty("client_email", out _))
                return "That key file has no account address in it, so the calendar could not be shared with it.";

            if (!root.TryGetProperty("private_key", out _))
                return "That key file is missing its private key. Download it again from Google.";
        }

        return null;
    }

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
        // A service account needs no sign-in and no consent screen, so try it first.
        if (UsesServiceAccount) return CreateServiceAccountApi();

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

    /// <summary>
    /// Builds a client that authenticates as the service account. There is no browser step and
    /// nothing to renew — the key file is the credential, and it does not expire.
    /// </summary>
    private ICalendarApi CreateServiceAccountApi()
    {
        var json = _settings.Get(SettingsStore.GoogleServiceAccountJson);

        try
        {
            // Built from the two fields explicitly rather than handing Google the whole file:
            // the blanket loader is deprecated, and this makes it obvious what is actually used.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var email = root.GetProperty("client_email").GetString()
                ?? throw new InvalidOperationException("The key file has no account address in it.");
            var privateKey = root.GetProperty("private_key").GetString()
                ?? throw new InvalidOperationException("The key file has no private key in it.");

            var credential = new ServiceAccountCredential(
                new ServiceAccountCredential.Initializer(email) { Scopes = Scopes }
                    .FromPrivateKey(privateKey));

            var service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "WrenchDesk"
            });

            return new GoogleCalendarApi(service);
        }
        catch (Exception ex)
        {
            throw new CalendarAuthException(
                "The Google key file was not accepted. Check it was pasted whole, and that the "
              + "Calendar API is switched on for that project. " + ex.Message, ex);
        }
    }

    public void Disconnect()
    {
        _settings.SetAll(new Dictionary<string, string>
        {
            [SettingsStore.GoogleTokenJson] = "",
            [SettingsStore.GoogleServiceAccountJson] = "",
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

        // Set on every read, not just the first. Google ties a sync token to the parameters of the
        // request that produced it, so flipping this between calls asks for a different shape of
        // result than the token was issued for. It also expands repeating events into the
        // individual dates a shop actually cares about.
        request.SingleEvents = true;

        if (!string.IsNullOrWhiteSpace(syncToken))
        {
            // timeMin cannot be combined with a sync token — Google rejects the request.
            request.SyncToken = syncToken;
        }
        else
        {
            // A first read is bounded, or connecting an old calendar would import years of history.
            request.TimeMinDateTimeOffset = new DateTimeOffset(timeMin ?? DateTime.Now.AddDays(-30));
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

        // An all-day event carries dates, not timestamps, and its end date is exclusive.
        var start = data.IsAllDay
            ? new EventDateTime { Date = data.Start.ToString("yyyy-MM-dd") }
            : new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(data.Start, TimeZoneInfo.Local.GetUtcOffset(data.Start)),
                TimeZone = timeZone
            };

        var end = data.IsAllDay
            ? new EventDateTime { Date = (data.End <= data.Start ? data.Start.Date.AddDays(1) : data.End.Date).ToString("yyyy-MM-dd") }
            : new EventDateTime
            {
                DateTimeDateTimeOffset = new DateTimeOffset(data.End, TimeZoneInfo.Local.GetUtcOffset(data.End)),
                TimeZone = timeZone
            };

        return new Event
        {
            Summary = data.Summary,
            Description = data.Description,
            Location = data.Location,
            Status = data.IsCancelled ? "cancelled" : "confirmed",
            Start = start,
            End = end,
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
