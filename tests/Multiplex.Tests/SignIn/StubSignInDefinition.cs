using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Tests.SignIn;

/// <summary>The stub site's sign-in definition, shaped like TripIt's: cookie check, then an in-page API call.</summary>
public sealed class StubSignInDefinition(Uri baseUrl) : ISiteSignInDefinition
{
    public const string SiteSlug = "stub";

    public string Slug => SiteSlug;
    public string DisplayName => "Stub Site";
    public Uri SignInStartUrl => new(baseUrl, "/login");
    public Uri RefreshUrl => new(baseUrl, "/app");
    public IReadOnlyCollection<string> AllowedNavigationHosts => ["127.0.0.1"];
    public string? SignInHint => "Stub hint.";

    public async Task<SiteAccount?> TryGetSignedInAccountAsync(IPage page, CancellationToken ct)
    {
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var url) || url.Host != "127.0.0.1") return null;
        var cookies = await page.GetCookiesAsync(baseUrl.AbsoluteUri);
        if (!cookies.Any(c => c.Name == StubLoginSite.CookieName)) return null;
        var result = await page.EvaluateFunctionAsync<Fetch>(
            "async () => { const r = await fetch('/api/profile'); return { status: r.status, body: r.status === 200 ? await r.text() : '' }; }");
        if (result.Status != 200) return null;
        using var doc = JsonDocument.Parse(result.Body!);
        var root = doc.RootElement;
        return new SiteAccount(SiteSlug, root.GetProperty("id").GetString()!,
            [.. root.GetProperty("emails").EnumerateArray().Select(e => e.GetString()!)], root.GetProperty("name").GetString()!);
    }

    public async Task<string> CaptureSnapshotAsync(IPage page, CancellationToken ct)
    {
        var cookies = await page.GetCookiesAsync(baseUrl.AbsoluteUri);
        return JsonSerializer.Serialize(new
        {
            cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
            captured_at = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    public Task<bool> IsSignedOutAsync(IPage page) =>
        Task.FromResult(Uri.TryCreate(page.Url, UriKind.Absolute, out var url) && url.AbsolutePath.StartsWith("/login", StringComparison.Ordinal));

    private sealed class Fetch
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }
}

/// <summary>A definition that is nothing but a slug (any string, even an invalid one); no browser ever reaches it.</summary>
public sealed class SlugOnlyDefinition(string slug) : ISiteSignInDefinition
{
    public string Slug => slug;
    public string DisplayName => "Slug " + slug;
    public Uri SignInStartUrl => new("http://127.0.0.1:9/login");
    public Uri RefreshUrl => new("http://127.0.0.1:9/app");
    public IReadOnlyCollection<string> AllowedNavigationHosts => ["127.0.0.1"];

    public Task<SiteAccount?> TryGetSignedInAccountAsync(IPage page, CancellationToken ct) => Task.FromResult<SiteAccount?>(null);
    public Task<string> CaptureSnapshotAsync(IPage page, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> IsSignedOutAsync(IPage page) => Task.FromResult(false);
}
