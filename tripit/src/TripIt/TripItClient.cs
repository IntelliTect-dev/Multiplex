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
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    /// <summary>
    /// Browser headers replayed from the captured SPA request when present in the session
    /// file. Akamai binds its bot cookies to the client fingerprint, so the user agent and
    /// client hints should match the browser that minted them.
    /// </summary>
    private static readonly string[] s_replayedSpaHeaders =
        ["user-agent", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "accept-language"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TripItSession _session;
    private readonly ILogger<TripItClient> _log;
    private readonly ISessionRefresher? _refresher;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public TripItClient(TripItSession session, ILogger<TripItClient>? log = null, ISessionRefresher? refresher = null)
    {
        _session = session;
        _log = log ?? NullLogger<TripItClient>.Instance;
        _refresher = refresher;
        var handler = new HttpClientHandler
        {
            UseCookies = false,                          // we set the Cookie header ourselves
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _ownsHttpClient = true;
        Trips = new TripsClient(this);
        Objects = new ObjectsClient(this);
        Lookup = new LookupClient(this);
    }

    /// <summary>
    /// Test/DI overload that accepts an externally-owned <see cref="HttpClient"/>.
    /// </summary>
    public TripItClient(TripItSession session, HttpClient httpClient, ILogger<TripItClient>? log = null,
        ISessionRefresher? refresher = null)
    {
        _session = session;
        _http = httpClient;
        _ownsHttpClient = false;
        _log = log ?? NullLogger<TripItClient>.Instance;
        _refresher = refresher;
        Trips = new TripsClient(this);
        Objects = new ObjectsClient(this);
        Lookup = new LookupClient(this);
    }

    /// <summary>Trips: list, get (with all objects), search.</summary>
    public TripsClient Trips { get; }

    /// <summary>Travel objects (flights, lodging, cars, …): list and get.</summary>
    public ObjectsClient Objects { get; }

    /// <summary>Reference lookups (airports, airlines, places).</summary>
    public LookupClient Lookup { get; }

    public string SessionSource => _session.Source ?? "(none)";
    public bool   IsConfigured  => _session.IsConfigured;
    public bool   CanRefreshSession => _refresher is not null;

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
    /// session is reloaded (and refreshed, if a <see cref="ISessionRefresher"/> is attached)
    /// and the request is retried once.
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

        EnsureConfigured();
        var queryList = query?.ToList();
        var cookieBefore = _session.Cookie;
        TripItAuthenticationException authFailure;
        try
        {
            return await SendOnceAsync(method, path, queryList, jsonBody, ct).ConfigureAwait(false);
        }
        catch (TripItAuthenticationException ex)
        {
            authFailure = ex;
        }
        if (!await TryRecoverSessionAsync(cookieBefore, authFailure, ct).ConfigureAwait(false))
        {
            throw authFailure;
        }
        _log.LogInformation("TripIt session refreshed; retrying {Method} {Path}", method, path);
        return await SendOnceAsync(method, path, queryList, jsonBody, ct).ConfigureAwait(false);
    }

    private void EnsureConfigured()
    {
        if (_session.IsConfigured) return;
        _session.Reload();
        if (!_session.IsConfigured)
        {
            throw new TripItAuthenticationException(
                "TripIt session is not configured. Run `node tools/extract-session.js` against a " +
                "signed-in browser (writes ~/.tripit-mcp/session.json), or set TRIPIT_COOKIE / TRIPIT_CSRF.");
        }
    }

    /// <summary>
    /// Re-reads the session source; if that yields nothing new and a refresher is attached,
    /// runs it and re-reads again. Returns true when a retry is worthwhile.
    /// </summary>
    private async Task<bool> TryRecoverSessionAsync(string cookieBefore, Exception ex, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may already have refreshed while we waited.
            if (_session.Cookie != cookieBefore) return true;

            _session.Reload();
            if (_session.IsConfigured && _session.Cookie != cookieBefore) return true;

            if (_refresher is null) return false;
            _log.LogWarning("TripIt authentication failed ({Message}); refreshing session.", ex.Message);
            try
            {
                await _refresher.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception refreshEx) when (refreshEx is not OperationCanceledException)
            {
                _log.LogError(refreshEx, "TripIt session refresh failed.");
                return false;
            }
            _session.Reload();
            return _session.IsConfigured;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string> SendOnceAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        object? jsonBody,
        CancellationToken ct)
    {
        var url = BaseUrl + path + BuildQuery(query);
        using var req = new HttpRequestMessage(method, url);
        ApplyHeaders(req);
        if (jsonBody is not null)
        {
            var payload = jsonBody as string ?? JsonSerializer.Serialize(jsonBody, TripItJson.Options);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        _log.LogDebug("{Method} {Url}", method, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Classify(path, (int)resp.StatusCode, resp.Content.Headers.ContentType?.MediaType, body);
        return body;
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        var spa = _session.SpaHeaders;
        string? Spa(string name) => spa.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        req.Headers.TryAddWithoutValidation("User-Agent", Spa("user-agent") ?? DefaultUserAgent);
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
        if (!string.IsNullOrEmpty(_session.CsrfToken))
        {
            req.Headers.Add("X-CSRF-Token-WA", _session.CsrfToken);
        }
        req.Headers.TryAddWithoutValidation("Cookie", _session.Cookie);
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
            throw new TripItApiException(status, path, ExtractError(body) ?? Preview(body));
        }
        if (looksHtml)
        {
            // A 200 with HTML is the SPA shell: the path is not an API endpoint.
            throw new TripItApiException(status, path, "Response was HTML, not JSON (path is not an API endpoint).");
        }
        if (ExtractError(body) is { } error)
        {
            throw new TripItApiException(status, path, error);
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
        _refreshLock.Dispose();
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
    public TripItApiException(int statusCode, string path, string detail)
        : base($"TripIt {path} -> {statusCode}: {detail}")
    {
        StatusCode = statusCode;
        Path = path;
        Detail = detail;
    }

    public int StatusCode { get; }
    public string Path { get; }
    public string Detail { get; }
}
