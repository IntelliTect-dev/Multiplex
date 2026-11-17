using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripIt.Json;
using TripIt.Models;

namespace TripIt;

/// <summary>
/// HTTP client for the TripIt web SPA backend (<c>www.tripit.com/api/v2/*</c>).
/// Auth replays the user's browser session: a Cookie header plus the X-CSRF-Token-WA
/// header. See <c>docs/api-surface.md</c> for the endpoint reference.
///
/// Resource sub-clients: <see cref="Trips"/>, <see cref="Objects"/>, <see cref="Lookup"/>.
/// </summary>
public sealed partial class TripItClient : IDisposable
{
    private const string BaseUrl  = "https://www.tripit.com";
    private const string Referer  = "https://www.tripit.com/app/trips";
    private const string AppInfo  = "web/0.0.2";
    /// <summary>
    /// Sent only when the session carries no browser User-Agent: the client names itself and
    /// never invents a browser identity. (TripIt may refuse it; the session is then renewed.)
    /// </summary>
    private static readonly string s_ownUserAgent =
        $"Multiplex-TripIt/{typeof(TripItClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    /// <summary>
    /// Browser headers replayed from the captured SPA request when present in the session
    /// file. Akamai binds its bot cookies to the client fingerprint, so the user agent and
    /// client hints should match the browser that minted them. This presents the client to
    /// TripIt as that browser; it is an owner-accepted exception to the no-disguise stance
    /// (see docs/designs/6-tripit-mcp-server-spec.md, section 9).
    /// </summary>
    private static readonly string[] s_replayedSpaHeaders =
        ["user-agent", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "accept-language"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ITripItSessionProvider _sessions;
    private readonly ILogger<TripItClient> _log;
    private readonly string? _pacingKey;

    /// <summary>
    /// A client that owns its HTTP stack: <see cref="CreatePrimaryHandler"/> behind a
    /// <see cref="Http.PacingHandler"/> (one request at a time, polite spacing, throttling honoured).
    /// </summary>
    public TripItClient(ITripItSessionProvider sessions, ILogger<TripItClient>? log = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _log = log ?? NullLogger<TripItClient>.Instance;
        _http = new HttpClient(new Http.PacingHandler(new Http.RequestPacer(), CreatePrimaryHandler()))
        {
            Timeout = TimeSpan.FromSeconds(150),
        };
        _ownsHttpClient = true;
        Trips = new TripsClient(this);
        Objects = new ObjectsClient(this);
        Lookup = new LookupClient(this);
    }

    /// <summary>
    /// A client over an externally owned <see cref="HttpClient"/> (DI, tests). The caller's
    /// handler chain must not follow redirects or keep a cookie jar; see <see cref="CreatePrimaryHandler"/>.
    /// </summary>
    /// <param name="pacingKey">Sent as <see cref="Http.RequestPacer.KeyOption"/> so a shared pacer paces per account.</param>
    public TripItClient(ITripItSessionProvider sessions, HttpClient httpClient, ILogger<TripItClient>? log = null,
        string? pacingKey = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(httpClient);
        _sessions = sessions;
        _http = httpClient;
        _ownsHttpClient = false;
        _log = log ?? NullLogger<TripItClient>.Instance;
        _pacingKey = pacingKey;
        Trips = new TripsClient(this);
        Objects = new ObjectsClient(this);
        Lookup = new LookupClient(this);
    }

    /// <summary>A client for one fixed session that is never reloaded or refreshed.</summary>
    public TripItClient(TripItSession session, ILogger<TripItClient>? log = null)
        : this(new SnapshotSessionProvider(session), log)
    {
    }

    /// <summary>
    /// The primary handler every TripIt HTTP stack needs: no cookie jar (the Cookie header comes
    /// from the session snapshot, and a shared jar would mix accounts), no automatic redirects
    /// (a 302 means the session rotated) and transparent decompression.
    /// </summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>Trips: list, get (with all objects), search.</summary>
    public TripsClient Trips { get; }

    /// <summary>Travel objects (flights, lodging, cars, …): list and get.</summary>
    public ObjectsClient Objects { get; }

    /// <summary>Reference lookups (airports, airlines, places).</summary>
    public LookupClient Lookup { get; }

    public string SessionSource => _sessions.Current.Source ?? "(none)";
    public bool   IsConfigured  => _sessions.Current.IsConfigured;
    public bool   CanRefreshSession => _sessions.CanRefresh;

    /// <summary>When the current session was captured from the browser, if known.</summary>
    public DateTimeOffset? SessionCapturedAt => _sessions.Current.CapturedAt;

    // ── Account-level endpoints ──────────────────────────────────────────────

    /// <summary>
    /// Lists trips. Prefer <see cref="TripsClient.ListAsync"/>; kept for the PowerShell
    /// module's <c>Get-TripItTrip</c> and existing callers.
    /// </summary>
    public async Task<ListResponse<Trip>> ListTripsAsync(
        bool past = false,
        int? pageSize = null,
        int? pageNum = null,
        string? tripPermissionFilter = null,
        string? traveler = null,
        string? excludeTypes = null,
        bool? shouldSortTripsByDate = null,
        CancellationToken ct = default)
    {
        var q = TripsClient.BuildListQuery(past, pageSize, pageNum, tripPermissionFilter, traveler,
            excludeTypes, shouldSortTripsByDate);
        var json = await SendRawAsync("/api/v2/list/trip", q, ct).ConfigureAwait(false);
        return Deserialize<ListResponse<Trip>>(json);
    }

    public async Task<Profile> GetProfileAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
        // The profile arrives wrapped: { timestamp, num_bytes, Profile: {…} }.
        var el = doc.RootElement.TryGetProperty("Profile", out var p) ? TripItJson.AsList(p).FirstOrDefault() : doc.RootElement;
        return el.Deserialize<Profile>(TripItJson.Options)
               ?? throw new InvalidOperationException("TripIt profile deserialized to null.");
    }

    public async Task<TravelerProfile> GetTravelerProfileAsync(
        bool? excludeTemplates = null,
        CancellationToken ct = default)
    {
        var q = new List<KeyValuePair<string, string?>>
        {
            new("exclude_templates", excludeTemplates?.ToString().ToLowerInvariant()),
        };
        var json = await SendRawAsync("/api/v2/travelerProfile/get", q, ct).ConfigureAwait(false);
        return Deserialize<TravelerProfile>(json);
    }

    public async Task<ListResponse<ProAlert>> ListProAlertsAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync("/api/v2/listProAlerts", null, ct).ConfigureAwait(false);
        return Deserialize<ListResponse<ProAlert>>(json);
    }

    public async Task<PurchasedProductInfo> GetPurchasedProductsAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync("/api/v2/purchasedProductInfo", null, ct).ConfigureAwait(false);
        return Deserialize<PurchasedProductInfo>(json);
    }

    public async Task<AppConfig> GetAppConfigAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync("/api/v2/appConfig", null, ct).ConfigureAwait(false);
        return Deserialize<AppConfig>(json);
    }

    public async Task<GtmData> GetGtmDataAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync("/api/v2/gtmDataAsJson", null, ct).ConfigureAwait(false);
        return Deserialize<GtmData>(json);
    }

    // ── Transport ────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues an authenticated GET against an arbitrary path under www.tripit.com and
    /// returns the raw body. Public escape hatch for endpoints without a typed wrapper.
    /// </summary>
    public Task<string> SendRawAsync(
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, path, query, jsonBody: null, ct);

    /// <summary>Authenticated GET, parsed as JSON. Caller disposes the document.</summary>
    public async Task<JsonDocument> GetJsonAsync(
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, path, query, jsonBody: null, ct).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Sends an authenticated request and returns the response body. Outcomes are
    /// classified from status <i>and</i> body: an Akamai 403 HTML page and 302/401 raise
    /// <see cref="TripItAuthenticationException"/>; a JSON <c>Error</c> envelope or any other
    /// non-success raises <see cref="TripItApiException"/>. On an authentication failure the
    /// session provider is asked for a renewed session, and the request is retried once, only
    /// when the renewed cookie differs from the rejected one.
    /// </summary>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query,
        object? jsonBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            throw new ArgumentException("path must start with '/'", nameof(path));

        var queryList = query?.ToList();
        var session = await ConfiguredSessionAsync(ct).ConfigureAwait(false);
        TripItAuthenticationException authFailure;
        try
        {
            return await SendOnceAsync(session, method, path, queryList, jsonBody, ct).ConfigureAwait(false);
        }
        catch (TripItAuthenticationException ex)
        {
            authFailure = ex;
        }

        var renewed = await _sessions.RenewAsync(session, ct).ConfigureAwait(false);
        if (renewed is null || !renewed.IsConfigured || renewed.Cookie == session.Cookie)
        {
            throw authFailure;
        }
        _log.LogInformation("TripIt session renewed; retrying {Method} {Path}", method, path);
        return await SendOnceAsync(renewed, method, path, queryList, jsonBody, ct).ConfigureAwait(false);
    }

    private async Task<TripItSession> ConfiguredSessionAsync(CancellationToken ct)
    {
        var session = _sessions.Current;
        if (session.IsConfigured) return session;
        session = await _sessions.RenewAsync(session, ct).ConfigureAwait(false) ?? session;
        return session.IsConfigured
            ? session
            : throw new TripItAuthenticationException(
                "TripIt session is not configured. Run `node tools/extract-session.js` against a " +
                "signed-in browser (writes ~/.tripit-mcp/session.json), or set TRIPIT_COOKIE / TRIPIT_CSRF.");
    }

    private async Task<string> SendOnceAsync(
        TripItSession session,
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        object? jsonBody,
        CancellationToken ct)
    {
        var url = BaseUrl + path + BuildQuery(query);
        using var req = new HttpRequestMessage(method, url);
        ApplyHeaders(req, session);
        if (_pacingKey is not null) req.Options.Set(Http.RequestPacer.KeyOption, _pacingKey);
        if (jsonBody is not null)
        {
            var payload = jsonBody as string ?? JsonSerializer.Serialize(jsonBody, TripItJson.Options);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        _log.LogDebug("{Method} {Path}", method, path);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        if (status is < 200 or > 299 && _log.IsEnabled(LogLevel.Debug))
        {
            // The body never carries the session (cookies travel in headers, which are not logged).
            _log.LogDebug("TripIt {Method} {Path} answered {Status}: {Body}", method, path, status, Preview(body));
        }
        Classify(path, status, resp.Content.Headers.ContentType?.MediaType, body);
        return body;
    }

    private static void ApplyHeaders(HttpRequestMessage req, TripItSession session)
    {
        var spa = session.SpaHeaders;
        string? Spa(string name) => spa.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        req.Headers.TryAddWithoutValidation("User-Agent", Spa("user-agent") ?? s_ownUserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        req.Headers.TryAddWithoutValidation("Accept-Language", Spa("accept-language") ?? "en-US");
        foreach (var name in s_replayedSpaHeaders.Where(n => n.StartsWith("sec-ch-ua", StringComparison.Ordinal)))
        {
            if (Spa(name) is { } v) req.Headers.TryAddWithoutValidation(name, v);
        }
        req.Headers.Referrer = new Uri(Referer);
        req.Headers.Add("Origin", BaseUrl);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.Add("X-TRIPIT-APP-INFO", AppInfo);
        if (!string.IsNullOrEmpty(session.CsrfToken))
        {
            req.Headers.TryAddWithoutValidation("X-CSRF-Token-WA", session.CsrfToken);
        }
        req.Headers.TryAddWithoutValidation("Cookie", session.Cookie);
    }

    /// <summary>
    /// Classifies a response from its status and body. Public for unit testing.
    /// </summary>
    public static void Classify(string path, int status, string? mediaType, string body)
    {
        var looksHtml = (mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false)
                        || body.TrimStart().StartsWith('<');

        if (status is 302 or 401)
        {
            throw new TripItAuthenticationException(
                $"TripIt returned {status} for {path} (session expired or rotated). " +
                "Re-run `node tools/extract-session.js` against the signed-in browser.");
        }
        if (status == 403 && (looksHtml || body.Contains("edgesuite", StringComparison.OrdinalIgnoreCase)))
        {
            throw new TripItAuthenticationException(
                $"TripIt returned an Akamai 403 block page for {path}. This means the short-lived " +
                "bot-management cookies have aged out, not that the request is malformed. " +
                "Re-run `node tools/extract-session.js` against the signed-in browser.");
        }
        if (status is < 200 or > 299)
        {
            // Never copy the raw body into the exception: it surfaces in tool errors. The body
            // is logged at Debug by the caller instead.
            var tripItMessage = ExtractError(body);
            throw new TripItApiException(status, path,
                tripItMessage ?? $"no error description ({(looksHtml ? "HTML" : "non-JSON")} body, {body.Length} bytes)",
                tripItMessage);
        }
        if (looksHtml)
        {
            // A 200 with HTML is the SPA shell: the path is not an API endpoint.
            throw new TripItApiException(status, path, "Response was HTML, not JSON (path is not an API endpoint).");
        }
        if (ExtractError(body) is { } error)
        {
            throw new TripItApiException(status, path, error, error);
        }
    }

    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "Error", "error" })
            {
                if (!doc.RootElement.TryGetProperty(key, out var err)) continue;
                var first = TripItJson.AsList(err).FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    var code = TripItJson.Str(first, "code");
                    var desc = TripItJson.Str(first, "description") ?? TripItJson.Str(first, "detailed_error_code");
                    return string.Join(": ", new[] { code, desc }.Where(s => s is not null));
                }
                return err.ValueKind == JsonValueKind.String ? err.GetString() : err.GetRawText();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string Preview(string body) => body.Length > 400 ? body[..400] + "..." : body;

    internal static T Deserialize<T>(string json)
    {
        var result = JsonSerializer.Deserialize<T>(json, TripItJson.Options);
        return result ?? throw new InvalidOperationException("TripIt response deserialized to null.");
    }

    internal static string BuildQuery(IEnumerable<KeyValuePair<string, string?>>? query)
    {
        if (query is null) return "";
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in query)
        {
            if (kv.Value is null) continue;
            sb.Append(first ? '?' : '&');
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
            first = false;
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>
/// Thrown when the TripIt backend rejects the session: 302/401, or an Akamai 403 block page
/// (the short-lived bot cookies aged out). Remedy: re-extract the session.
/// </summary>
public sealed class TripItAuthenticationException : InvalidOperationException
{
    public TripItAuthenticationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when TripIt answers with a non-success status or a JSON <c>Error</c> envelope.
/// </summary>
public sealed class TripItApiException : Exception
{
    public TripItApiException(int statusCode, string path, string detail, string? tripItMessage = null)
        : base($"TripIt {path} -> {statusCode}: {detail}")
    {
        StatusCode = statusCode;
        Path = path;
        Detail = detail;
        TripItMessage = tripItMessage;
    }

    public int StatusCode { get; }
    public string Path { get; }

    /// <summary>What went wrong: TripIt's error description, or this library's own summary. Never the raw body.</summary>
    public string Detail { get; }

    /// <summary>The description from TripIt's JSON <c>Error</c> envelope, or null when it sent none.</summary>
    public string? TripItMessage { get; }
}

/// <summary>
/// A multi-step write stopped half way: the first step created <see cref="CreatedUuid"/> and a
/// follow-up step failed. Callers must not repeat the create (that would duplicate it); update
/// the created item instead.
/// </summary>
public sealed class TripItPartialWriteException : Exception
{
    public TripItPartialWriteException(string createdUuid, string message, Exception inner)
        : base(message, inner)
    {
        CreatedUuid = createdUuid;
    }

    public string CreatedUuid { get; }
}
