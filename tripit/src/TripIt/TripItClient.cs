using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TripIt.Models;

namespace TripIt;

/// <summary>
/// HTTP client for the TripIt web SPA backend (api.tripit.com / www.tripit.com /api/v2/*).
/// Auth is performed by replaying the user's browser session: a Cookie header plus the
/// X-CSRF-Token-WA header.  Endpoints + parameters were derived from a HAR capture of the
/// live site under https://www.tripit.com/api/v2/.
/// </summary>
public sealed class TripItClient : IDisposable
{
    private const string BaseUrl  = "https://www.tripit.com";
    private const string Referer  = "https://www.tripit.com/app/trips";
    private const string AppInfo  = "web/0.0.2";
    private const string UA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                                    "(KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TripItSession _session;
    private readonly ILogger<TripItClient> _log;

    public TripItClient(TripItSession session, ILogger<TripItClient>? log = null)
    {
        _session = session;
        _log = log ?? NullLogger<TripItClient>.Instance;
        var handler = new HttpClientHandler
        {
            UseCookies = false,                          // we set the Cookie header ourselves
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Test/DI overload that accepts an externally-owned <see cref="HttpClient"/>.
    /// </summary>
    public TripItClient(TripItSession session, HttpClient httpClient, ILogger<TripItClient>? log = null)
    {
        _session = session;
        _http = httpClient;
        _ownsHttpClient = false;
        _log = log ?? NullLogger<TripItClient>.Instance;
    }

    public string SessionSource => _session.Source ?? "(none)";
    public bool   IsConfigured  => _session.IsConfigured;

    // ── Typed endpoint methods ───────────────────────────────────────────────

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
        var q = new List<KeyValuePair<string, string?>>
        {
            new("past",                          past ? "true" : "false"),
            new("isPast",                        past ? "true" : "false"),
            new("page_size",                     pageSize?.ToString()),
            new("page_num",                      pageNum?.ToString()),
            new("trip_permission_filter",        tripPermissionFilter),
            new("traveler",                      traveler),
            new("exclude_types",                 excludeTypes),
            new("should_sort_trips_by_date",     shouldSortTripsByDate?.ToString().ToLowerInvariant()),
        };
        var json = await SendRawAsync("/api/v2/list/trip", q, ct).ConfigureAwait(false);
        return Deserialize<ListResponse<Trip>>(json);
    }

    public async Task<Profile> GetProfileAsync(CancellationToken ct = default)
    {
        var json = await SendRawAsync("/api/v2/get/profile", null, ct).ConfigureAwait(false);
        return Deserialize<Profile>(json);
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

    // ── Raw escape hatch ─────────────────────────────────────────────────────

    /// <summary>
    /// Issues an authenticated GET against an arbitrary path under www.tripit.com and
    /// returns the raw JSON body. Public escape hatch for endpoints that do not yet have
    /// a typed wrapper. Same code path as the typed methods.
    /// </summary>
    public async Task<string> SendRawAsync(
        string path,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        CancellationToken ct = default)
    {
        if (!_session.IsConfigured)
        {
            throw new InvalidOperationException(
                "TripIt session is not configured. Set TRIPIT_COOKIE (and optionally TRIPIT_CSRF) " +
                "or place a session file at ~/.tripit-mcp/session.json. " +
                "See README for how to extract these from your browser.");
        }

        var qs = BuildQuery(query);
        var url = BaseUrl + path + qs;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UA);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        req.Headers.Referrer = new Uri(Referer);
        req.Headers.Add("Origin", BaseUrl);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.Add("X-TRIPIT-APP-INFO", AppInfo);
        if (!string.IsNullOrEmpty(_session.CsrfToken))
        {
            req.Headers.Add("X-CSRF-Token-WA", _session.CsrfToken);
        }
        req.Headers.Add("Cookie", _session.Cookie);

        _log.LogDebug("GET {Url}", url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if ((int)resp.StatusCode == 302 || (int)resp.StatusCode == 401)
        {
            throw new TripItAuthenticationException(
                $"TripIt returned {(int)resp.StatusCode} (likely session expired). " +
                "Refresh the cookie via Connect-TripIt or `node tools/extract-session.js`.");
        }
        if (!resp.IsSuccessStatusCode)
        {
            var preview = body.Length > 400 ? body[..400] + "..." : body;
            throw new HttpRequestException(
                $"TripIt {path} -> {(int)resp.StatusCode}: {preview}");
        }
        return body;
    }

    private static T Deserialize<T>(string json)
    {
        var result = JsonSerializer.Deserialize<T>(json, s_jsonOptions);
        if (result is null)
        {
            throw new InvalidOperationException("TripIt response deserialized to null.");
        }
        return result;
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string?>>? query)
    {
        if (query is null) return "";
        var sb = new StringBuilder();
        bool first = true;
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
/// Thrown when the TripIt backend returns an auth-failure status (302/401), typically because
/// the session cookie has expired.
/// </summary>
public sealed class TripItAuthenticationException : InvalidOperationException
{
    public TripItAuthenticationException(string message) : base(message) { }
}
