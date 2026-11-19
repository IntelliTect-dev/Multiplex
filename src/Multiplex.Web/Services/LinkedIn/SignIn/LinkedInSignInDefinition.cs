using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Web.Services.LinkedIn.SignIn;

/// <summary>
/// How LinkedIn's sign-in looks to the generic connect machinery. Recorded on 2026-11-19: the
/// sign-in starts at <c>/login</c>; LinkedIn may answer the password with "approve this sign-in in
/// the LinkedIn app" (a <c>/checkpoint/</c> page that polls for up to 15 minutes), after which the
/// page goes to <c>/feed/</c>. A signed-in page's API requests (<c>/voyager/api/…</c>) carry the
/// cookies, a <c>csrf-token</c> header equal to the <c>JSESSIONID</c> cookie, and LinkedIn's own
/// headers (<c>x-li-track</c>, <c>x-li-lang</c>); <c>/voyager/api/me</c> names the member.
/// </summary>
public sealed class LinkedInSignInDefinition(IConfiguration config) : ISiteSignInDefinition
{
    public const string SiteSlug = LinkedInToolContext.Site;
    internal const string WebOrigin = "https://www.linkedin.com";
    internal const string WebHost = "www.linkedin.com";

    /// <summary>LinkedIn plus the identity providers its sign-in page offers (Google and Microsoft).</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedHosts =
        [WebHost, "linkedin.com", "accounts.google.com", "login.microsoftonline.com", "login.live.com"];

    // Browser headers worth replaying from the page's own API requests. Never cookie or csrf-token,
    // and nothing page-specific (referer, x-li-page-instance) or request-specific (accept, content-type).
    private static readonly string[] s_replayed = ["user-agent", "accept-language", "x-li-lang", "x-li-track"];

    public string Slug => SiteSlug;

    public string DisplayName => "LinkedIn";

    public Uri SignInStartUrl { get; } = new(WebOrigin + "/login");

    public Uri RefreshUrl { get; } = new(WebOrigin + "/feed/");

    public string? SignInHint => "If LinkedIn asks you to approve the sign-in in its app, approve it there; this page carries on by itself.";

    /// <summary><c>Multiplex:Sites:linkedin:AllowedNavigationHosts</c> replaces the defaults when set.</summary>
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
        if (!IsLinkedInPage(page.Url) || IsSignInPage(page.Url) || IsCheckpointPage(page.Url)) return null;
        var cookies = await page.GetCookiesAsync(WebOrigin + "/").ConfigureAwait(false);
        if (!cookies.Any(c => c.Name == "li_at")) return null;
        var csrf = CsrfOf(cookies);
        if (csrf.Length == 0) return null;
        var me = await FetchMeAsync(page, csrf).ConfigureAwait(false);
        return me is null ? null : ParseAccount(me);
    }

    public Task<bool> IsSignedOutAsync(IPage page) => Task.FromResult(IsSignInPage(page.Url));

    public async Task<string> CaptureSnapshotAsync(IPage page, CancellationToken ct)
    {
        // Watch the page's own /voyager/api/ requests (the headers actually sent) while the feed loads.
        var observer = new ApiHeaderObserver();
        page.Client.MessageReceived += observer.OnMessage;
        try
        {
            try
            {
                await page.GoToAsync(RefreshUrl.AbsoluteUri, new NavigationOptions
                {
                    Timeout = 30_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NavigationException or TimeoutException)
            {
                // Still read the jar below; the browser itself fills in missing headers.
            }
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (observer.Headers is null && DateTime.UtcNow < deadline) await Task.Delay(250, ct).ConfigureAwait(false);
        }
        finally
        {
            page.Client.MessageReceived -= observer.OnMessage;
        }

        var cookies = await page.GetCookiesAsync(WebOrigin + "/").ConfigureAwait(false);
        if (!cookies.Any(c => c.Name == "li_at"))
            throw new InvalidOperationException("LinkedIn's session cookie disappeared before it could be captured.");
        var csrf = CsrfOf(cookies);
        var me = await FetchMeAsync(page, csrf).ConfigureAwait(false);
        var account = (me is null ? null : ParseAccount(me))
                      ?? throw new InvalidOperationException("LinkedIn did not say which member is signed in.");

        var headers = new Dictionary<string, string>(observer.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        if (!headers.ContainsKey("user-agent") || !headers.ContainsKey("accept-language"))
        {
            var nav = await page.EvaluateExpressionAsync<NavigatorInfo>(
                "({ userAgent: navigator.userAgent, languages: (navigator.languages || []).join(',') })").ConfigureAwait(false);
            headers.TryAdd("user-agent", nav.UserAgent ?? "");
            if (!string.IsNullOrEmpty(nav.Languages)) headers.TryAdd("accept-language", nav.Languages);
        }

        return BuildSnapshot(string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")), csrf, account.AccountId, headers,
            DateTimeOffset.UtcNow);
    }

    /// <summary>The snapshot the LinkedIn service replays (see <see cref="LinkedInSession.TryFromSnapshotJson"/>).</summary>
    internal static string BuildSnapshot(string cookie, string csrf, string profileId, IReadOnlyDictionary<string, string> observed,
        DateTimeOffset capturedAt)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in observed)
        {
            var lower = name.ToLowerInvariant();
            if (s_replayed.Contains(lower) || lower.StartsWith("sec-ch-", StringComparison.Ordinal)) headers[lower] = value;
        }
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cookie"] = cookie,
            ["csrf_token"] = csrf.Trim('"'),
            ["profile_id"] = profileId,
            ["headers"] = headers,
            ["captured_at"] = capturedAt.ToString("O"),
        });
    }

    /// <summary>
    /// Reads the member from a <c>/voyager/api/me</c> answer: the <c>included</c> mini-profile named
    /// by <c>data.*miniProfile</c>; its <c>dashEntityUrn</c> (<c>urn:li:fsd_profile:…</c>) is the account id.
    /// </summary>
    internal static SiteAccount? ParseAccount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("*miniProfile", out var mini)
                || mini.GetString() is not { } miniUrn || !root.TryGetProperty("included", out var included)
                || included.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var item in included.EnumerateArray())
            {
                if (Str(item, "entityUrn") != miniUrn) continue;
                var dash = Str(item, "dashEntityUrn");
                if (dash is null || !dash.StartsWith("urn:li:fsd_profile:", StringComparison.Ordinal)) return null;
                var id = dash["urn:li:fsd_profile:".Length..];
                if (id.Length == 0) return null;
                var name = $"{Str(item, "firstName")} {Str(item, "lastName")}".Trim();
                return new SiteAccount(SiteSlug, id, [], name);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True only for LinkedIn's own sign-in pages. A <c>/checkpoint/</c> page (a security check or an
    /// app approval) is not one: treating it as signed out would end the connection.
    /// </summary>
    internal static bool IsSignInPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsLinkedInHost(uri.Host)) return false;
        var path = uri.AbsolutePath;
        return path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/uas/login", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/authwall", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A security check or app-approval page: the sign-in is not finished while one is showing.</summary>
    internal static bool IsCheckpointPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLinkedInHost(uri.Host)
        && uri.AbsolutePath.StartsWith("/checkpoint", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkedInPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && IsLinkedInHost(uri.Host);

    private static bool IsLinkedInHost(string host) =>
        host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase);

    private static string CsrfOf(IEnumerable<CookieParam> cookies) =>
        cookies.FirstOrDefault(c => c.Name == "JSESSIONID")?.Value?.Trim('"') ?? "";

    private static async Task<string?> FetchMeAsync(IPage page, string csrf)
    {
        var result = await page.EvaluateFunctionAsync<MeFetch>(
            """
            async (csrf) => {
              const r = await fetch('/voyager/api/me', {
                credentials: 'include',
                headers: {
                  'accept': 'application/vnd.linkedin.normalized+json+2.1',
                  'csrf-token': csrf,
                  'x-restli-protocol-version': '2.0.0',
                },
              });
              return { status: r.status, body: r.status === 200 ? await r.text() : '' };
            }
            """, csrf).ConfigureAwait(false);
        return result is { Status: 200, Body: { Length: > 0 } body } ? body : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed class MeFetch
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    private sealed class NavigatorInfo
    {
        [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
        [JsonPropertyName("languages")] public string? Languages { get; set; }
    }

    /// <summary>Joins requestWillBeSent (URL) with requestWillBeSentExtraInfo (headers as sent) for the first /voyager/api/ call.</summary>
    private sealed class ApiHeaderObserver
    {
        private readonly ConcurrentDictionary<string, bool> _api = new();
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _extra = new();

        public Dictionary<string, string>? Headers { get; private set; }

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
                        if (url.StartsWith(WebOrigin + "/voyager/api/", StringComparison.OrdinalIgnoreCase))
                        {
                            var id = data.GetProperty("requestId").GetString()!;
                            _api[id] = true;
                            TryJoin(id);
                        }
                        break;
                    case "Network.requestWillBeSentExtraInfo":
                        var requestId = data.GetProperty("requestId").GetString()!;
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var header in data.GetProperty("headers").EnumerateObject())
                        {
                            if (!LinkedInSession.IsSecretHeader(header.Name)) headers[header.Name] = header.Value.GetString() ?? "";
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
            if (Headers is null && _api.ContainsKey(id) && _extra.TryGetValue(id, out var headers)) Headers = headers;
        }
    }
}
