using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>
/// A fake www.amazon.com and image host. Out of the box it serves the fixtures: robots.txt, the
/// search results page for any <c>/s</c>, the popular product for any <c>/dp/</c>, and a tiny
/// JPEG for any picture. <see cref="On"/> overrides an address; the newest rule wins.
/// </summary>
public sealed class FakeAmazon : HttpMessageHandler
{
    public sealed record Request(Uri Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset At);

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private readonly List<(Func<Uri, bool> Matches, Func<Uri, HttpResponseMessage> Respond)> _rules = [];
    private readonly Lock _lock = new();
    private readonly List<Request> _requests = [];

    public TimeProvider Time { get; set; } = TimeProvider.System;

    public IReadOnlyList<Request> Requests { get { lock (_lock) return [.. _requests]; } }

    /// <summary>The requests for pages and pictures, without the robots.txt fetches.</summary>
    public IReadOnlyList<Request> Fetches => [.. Requests.Where(r => r.Url.AbsolutePath != "/robots.txt")];

    public FakeAmazon On(Func<Uri, bool> matches, Func<Uri, HttpResponseMessage> respond)
    {
        _rules.Insert(0, (matches, respond));
        return this;
    }

    public FakeAmazon On(string pathPrefix, Func<HttpResponseMessage> respond) =>
        On(u => u.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal), _ => respond());

    public static HttpResponseMessage Html(string html, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Fixture(string name, int status = 200) => Html(AmazonFixture.Read(name), status);

    public static HttpResponseMessage Redirect(string location, int status = 302)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    public static HttpResponseMessage Status(int status, TimeSpan? retryAfter = null)
    {
        var response = Html("<html>error</html>", status);
        if (retryAfter is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    public static HttpResponseMessage Image(string type = "image/jpeg", byte[]? data = null)
    {
        var content = new ByteArrayContent(data ?? Jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_lock) _requests.Add(new Request(url, headers, Time.GetUtcNow()));

        foreach (var (matches, respond) in _rules.ToList())
        {
            if (matches(url)) return Task.FromResult(respond(url));
        }
        if (url.AbsolutePath == "/robots.txt")
        {
            return Task.FromResult(url.Host == "www.amazon.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AmazonFixture.Read("robots.txt"), Encoding.UTF8, "text/plain") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: GPTBot\nDisallow: /\n", Encoding.UTF8, "text/plain") });
        }
        if (url.Host != "www.amazon.com") return Task.FromResult(Image());
        if (url.AbsolutePath == "/s") return Task.FromResult(Fixture("search-results.html"));
        if (url.AbsolutePath.StartsWith("/dp/", StringComparison.Ordinal)) return Task.FromResult(Fixture("product-popular.html"));
        return Task.FromResult(Fixture("not-found.html", 404));
    }
}

/// <summary>An <see cref="AmazonClient"/> on a fake Amazon and a fake clock: every wait advances the clock at once.</summary>
public sealed class AmazonHarness
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 14, 5, 0, TimeSpan.Zero));
    public FakeAmazon Amazon { get; } = new();
    public CapturingLogger Log { get; } = new();
    public List<TimeSpan> Waits { get; } = [];
    public const string PublicUrl = "https://mcp.example.test";
    public AmazonClient Client { get; }

    public AmazonHarness(SiteGateOptions? options = null)
    {
        Amazon.Time = Time;
        var gate = new SiteGate(options ?? new SiteGateOptions { Site = "Amazon" }, Time, () => 0, (wait, _) =>
        {
            Waits.Add(wait);
            Time.Advance(wait);
            return Task.CompletedTask;
        });
        Client = new AmazonClient(new SingleHandlerFactory(Amazon), new AmazonGate(gate), new AmazonLinks(() => PublicUrl), Time, Log);
    }

    public Task<AmazonSearchAnswer> Search(string query = "usb c charging cable", AmazonSort sort = AmazonSort.Relevance, int pages = 1,
        int? minRatingsCount = null) =>
        Client.SearchAsync(new AmazonSearchRequest(query, sort, pages, MinRatingsCount: minRatingsCount), CancellationToken.None);

    public Task<(AmazonProduct Product, AmazonImage? Image)> Product(string product = "B088NRLMPV") =>
        Client.GetProductAsync(product, CancellationToken.None);

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

public sealed class CapturingLogger : ILogger<AmazonClient>
{
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines { get { lock (_lock) return [.. _lines]; } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) _lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
