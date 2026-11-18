using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;
using PuppeteerSharp;
using TripIt.Json;

namespace Multiplex.Web.Services.TripIt.SignIn;

/// <summary>
/// How TripIt's sign-in looks to the generic connect machinery. Observed 2026-11-17 with a
/// fresh, signed-out profile: <c>/app/trips</c> sends a signed-out visitor to
/// <c>/account/login?redirect_url=…/app/trips</c>, and that page's only other sign-in method is
/// Google (<c>/account/signInGoogle</c> → <c>accounts.google.com</c>). Headless Chrome (whose
/// user agent says "HeadlessChrome") is served TripIt's "Access Denied" page on
/// <c>/app/trips</c> and <c>/account/signInGoogle</c>, although <c>/account/login</c> loads.
/// </summary>
public sealed class TripItSignInDefinition(IConfiguration config) : ISiteSignInDefinition
{
    public const string SiteSlug = "tripit";
    internal const string WebOrigin = "https://www.tripit.com";
    internal const string WebHost = "www.tripit.com";
    internal const string AppInfo = "web/0.0.2";
    internal const string ProfileApi = WebOrigin + "/api/v2/get/profile";

    /// <summary>TripIt itself plus the identity provider its login page links to (Google).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedHosts = [WebHost, "tripit.com", "accounts.google.com"];

    // Request headers worth replaying (TripItClient reads these names from spa_headers). Never cookie or CSRF.
    private static readonly string[] s_spaHeaderNames =
    [
        "user-agent", "accept-language", "x-tripit-app-info", "x-requested-with",
    ];

    public string Slug => SiteSlug;

    public string DisplayName => "TripIt";

    public Uri SignInStartUrl { get; } =
        new("https://www.tripit.com/account/login?redirect_url=https%3A%2F%2Fwww.tripit.com%2Fapp%2Ftrips");

    public Uri RefreshUrl { get; } = new("https://www.tripit.com/app/trips");

    public string? SignInHint => "Tick “Keep me signed in” on TripIt's page so this server can stay connected.";

    /// <summary><c>Multiplex:Sites:tripit:AllowedNavigationHosts</c> replaces the defaults when set.</summary>
    public IReadOnlyCollection<string> AllowedNavigationHosts
    {
        get
        {
            var configured = config.GetSection($"Multiplex:Sites:{SiteSlug}:AllowedNavigationHosts")
                .GetChildren().Select(c => c.Value?.Trim()).OfType<string>().Where(h => h.Length > 0).ToArray();
            return configured.Length > 0 ? configured : DefaultAllowedHosts;
        }
    }

    public async Task<SiteAccount?> TryGetSignedInAccountAsync(IPage page, CancellationToken ct)
    {
        if (!IsTripItPage(page.Url)) return null;

        // The session cookie is httpOnly, so read the jar over CDP rather than document.cookie.
        var cookies = await page.GetCookiesAsync(ProfileApi);
        if (!cookies.Any(c => c.Name == "it_session_id")) return null;
        var csrf = cookies.FirstOrDefault(c => c.Name == "it_wa_csrf")?.Value ?? "";

        // Without these headers TripIt answers 401 even for a signed-in browser.
        var result = await page.EvaluateFunctionAsync<ProfileFetch>(
            """
            async (csrf, appInfo) => {
              const r = await fetch('/api/v2/get/profile', {
                credentials: 'include',
                headers: {
                  'Accept': 'application/json',
                  'X-CSRF-Token-WA': csrf,
                  'X-Requested-With': 'XMLHttpRequest',
                  'X-TRIPIT-APP-INFO': appInfo,
                },
              });
              return { status: r.status, body: r.status === 200 ? await r.text() : '' };
            }
            """, csrf, AppInfo);
        return result is { Status: 200, Body: { Length: > 0 } body } ? ParseAccount(body) : null;
    }

    public Task<bool> IsSignedOutAsync(IPage page) =>
        Task.FromResult(Uri.TryCreate(page.Url, UriKind.Absolute, out var uri)
                        && uri.Host.EndsWith("tripit.com", StringComparison.OrdinalIgnoreCase)
                        && uri.AbsolutePath.StartsWith("/account/login", StringComparison.OrdinalIgnoreCase));

    public async Task<string> CaptureSnapshotAsync(IPage page, CancellationToken ct)
    {
        // Watch the SPA's own /api/ requests (the actual headers sent, from requestWillBeSentExtraInfo)
        // while the trips page loads, so replayed calls carry the same browser headers.
        var observer = new ApiHeaderObserver();
        page.Client.MessageReceived += observer.OnMessage;
        try
        {
            try
            {
                if (IsTripItPage(page.Url) && new Uri(page.Url).AbsolutePath.StartsWith("/app", StringComparison.Ordinal))
                    await page.ReloadAsync(new ReloadOptions { Timeout = 30_000, WaitUntil = [WaitUntilNavigation.DOMContentLoaded] });
                else
                    await page.GoToAsync(RefreshUrl.AbsoluteUri, new NavigationOptions { Timeout = 30_000, WaitUntil = [WaitUntilNavigation.DOMContentLoaded] });
            }
            catch (Exception ex) when (ex is NavigationException or TimeoutException)
            {
                // Still read the jar below; fallbacks cover missing headers.
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (observer.Headers is null && DateTime.UtcNow < deadline) await Task.Delay(250, ct);
        }
        finally
        {
            page.Client.MessageReceived -= observer.OnMessage;
        }

        var cookies = await page.GetCookiesAsync(ProfileApi);
        if (!cookies.Any(c => c.Name == "it_session_id"))
            throw new InvalidOperationException("TripIt's session cookie disappeared before it could be captured.");

        var observed = observer.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var csrf = observed.GetValueOrDefault("x-csrf-token-wa") is { Length: > 0 } headerCsrf
            ? headerCsrf
            : cookies.FirstOrDefault(c => c.Name == "it_wa_csrf")?.Value ?? "";

        var spa = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in observed)
        {
            var lower = name.ToLowerInvariant();
            if (s_spaHeaderNames.Contains(lower) || lower.StartsWith("sec-ch-ua", StringComparison.Ordinal))
                spa[lower] = value;
        }
        // Fallbacks from the browser itself when no SPA request was seen (e.g. the page was blocked).
        if (!spa.ContainsKey("user-agent") || !spa.ContainsKey("accept-language"))
        {
            var nav = await page.EvaluateExpressionAsync<NavigatorInfo>(
                "({ userAgent: navigator.userAgent, languages: (navigator.languages || []).join(',') })");
            spa.TryAdd("user-agent", nav.UserAgent ?? "");
            if (!string.IsNullOrEmpty(nav.Languages)) spa.TryAdd("accept-language", nav.Languages);
        }
        spa.TryAdd("x-requested-with", "XMLHttpRequest");
        spa.TryAdd("x-tripit-app-info", AppInfo);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cookie"] = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
            ["csrf_token"] = csrf,
            ["spa_headers"] = spa,
            ["captured_from"] = observer.Url ?? RefreshUrl.AbsoluteUri,
            ["captured_at"] = DateTimeOffset.UtcNow.ToString("O"),
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    private static bool IsTripItPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, WebHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the account from a <c>/api/v2/get/profile</c> response. Id: the profile's
    /// <c>@attributes.ref</c>, else <c>screen_name</c>. Emails: every
    /// <c>ProfileEmailAddresses.ProfileEmailAddress</c> address (one object or an array) whose
    /// <c>is_confirmed</c> is exactly true (TripIt sends the string <c>"true"</c>), so nobody can
    /// claim an allowlisted address by adding it.
    /// </summary>
    internal static SiteAccount? ParseAccount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var profile = TripItJson.AsList(root, "Profile").FirstOrDefault();
            if (profile.ValueKind != JsonValueKind.Object) return null;

            var reference = TripItJson.Obj(profile, "@attributes") is { } attributes ? TripItJson.Str(attributes, "ref") : null;
            reference ??= TripItJson.Str(profile, "ref");
            var screenName = TripItJson.Str(profile, "screen_name");
            var accountId = reference ?? screenName;
            if (string.IsNullOrWhiteSpace(accountId)) return null;

            var emails = new List<string>();
            if (TripItJson.Obj(profile, "ProfileEmailAddresses") is { } addresses)
            {
                foreach (var entry in TripItJson.AsList(addresses, "ProfileEmailAddress"))
                {
                    // Fail closed: a missing or odd flag ("1", "no", null) is not a confirmation.
                    if (TripItJson.Bool(entry, "is_confirmed") != true) continue;
                    if (TripItJson.Str(entry, "address") is { } address && !emails.Contains(address, StringComparer.OrdinalIgnoreCase))
                        emails.Add(address.Trim());
                }
            }

            var displayName = TripItJson.Str(profile, "public_display_name")
                              ?? TripItJson.Str(profile, "display_name")
                              ?? screenName
                              ?? "";
            return new SiteAccount(SiteSlug, accountId.Trim(), emails, displayName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ProfileFetch
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    private sealed class NavigatorInfo
    {
        [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
        [JsonPropertyName("languages")] public string? Languages { get; set; }
    }

    /// <summary>Joins requestWillBeSent (URL) with requestWillBeSentExtraInfo (headers as sent) for the first www.tripit.com/api/ call.</summary>
    private sealed class ApiHeaderObserver
    {
        private readonly ConcurrentDictionary<string, string> _urls = new();
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _extra = new();

        public Dictionary<string, string>? Headers { get; private set; }
        public string? Url { get; private set; }

        public void OnMessage(object? sender, MessageEventArgs e)
        {
            if (Headers is not null) return;
            try
            {
                var data = e.MessageData;
                switch (e.MessageID)
                {
                    case "Network.requestWillBeSent":
                        var url = data.GetProperty("request").GetProperty("url").GetString() ?? "";
                        if (url.StartsWith(WebOrigin + "/api/", StringComparison.OrdinalIgnoreCase))
                        {
                            var id = data.GetProperty("requestId").GetString()!;
                            _urls[id] = url.Split('?')[0];
                            TryJoin(id);
                        }
                        break;
                    case "Network.requestWillBeSentExtraInfo":
                        var requestId = data.GetProperty("requestId").GetString()!;
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var header in data.GetProperty("headers").EnumerateObject())
                        {
                            if (!header.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                                headers[header.Name] = header.Value.GetString() ?? "";
                        }
                        _extra[requestId] = headers;
                        TryJoin(requestId);
                        break;
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
            }
        }

        private void TryJoin(string id)
        {
            if (Headers is null && _urls.TryGetValue(id, out var url) && _extra.TryGetValue(id, out var headers))
            {
                Url = url;
                Headers = headers;
            }
        }
    }
}
