using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Sites.GoogleFlights;
using Multiplex.Tests.Services.TripIt;

namespace Multiplex.Tests.Services.GoogleFlights;

/// <summary>
/// Multiplex with its real MCP pipeline at <c>/google-flights/mcp</c>, and a Google Flights client whose network
/// handler replays the committed, scrubbed references instead of reaching Google.
/// </summary>
public sealed class GoogleFlightsTestApp : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "multiplex-google-flights-tests-" + Guid.NewGuid().ToString("N"));

    public RecordedGoogle Google { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Multiplex:DataPath", _dataPath);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<GoogleFlightsClient>();
            // The day the references were recorded, so their dates are always "the future" to the client.
            services.AddSingleton(_ => new GoogleFlightsClient(
                new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 0 }, new RecordedDay(), Google));
        });
    }

    public McpTestClient Mcp() =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), token: null, "/google-flights/mcp");
}

/// <summary>A clock stopped on 2026-11-20, the day the direct-HTTP references were recorded.</summary>
public sealed class RecordedDay : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);
}

/// <summary>Answers each request with the next queued recorded answer, and keeps what was sent.</summary>
public sealed class RecordedGoogle : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _answers = new();

    /// <summary>What the client sent, in order: each RPC's inner request, or each page's link.</summary>
    public ConcurrentQueue<string> SentInnerRequests { get; } = new();

    /// <summary>Queues entry <paramref name="index"/> of <c>google-flights-&lt;reference&gt;.har</c>.</summary>
    public RecordedGoogle Answer(string reference, int index)
    {
        var text = RecordedAnswer(reference, index);
        _answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") });
        return this;
    }

    /// <summary>Queues <c>references/pages/google-flights-search-page-data-&lt;name&gt;.html</c>: a search page the client fetched.</summary>
    public RecordedGoogle AnswerPage(string name) => Page(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Services", "GoogleFlights", "References", "Pages", $"google-flights-search-page-data-{name}.html")));

    /// <summary>
    /// Queues a search page carrying entry <paramref name="index"/>'s recorded <c>GetShoppingResults</c> answer, embedded
    /// the way a search page embeds its first answer (data block <c>ds:1</c>, the same layout).
    /// </summary>
    public RecordedGoogle AnswerPageWith(string reference, int index)
    {
        var payload = RecordedAnswer(reference, index).Split('\n')
            .Where(l => l.StartsWith("[[\"wrb.fr\"", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l).RootElement[0][2].GetString()!)
            .Last(p => JsonDocument.Parse(p).RootElement is var e && (e[2].ValueKind == JsonValueKind.Array || e[3].ValueKind == JsonValueKind.Array));
        return Page($"<script class=\"ds:1\" nonce=\"redacted\">AF_initDataCallback({{key: 'ds:1', hash: '9', data:{payload}, sideChannel: {{}}}});</script>");
    }

    /// <summary>Queues a redirect, which the client must see and never follow.</summary>
    public RecordedGoogle AnswerRedirect(string location)
    {
        _answers.Enqueue(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri(location);
            return r;
        });
        return this;
    }

    public static string RecordedInnerRequest(string reference, int index)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "GoogleFlights", "References", $"google-flights-{reference}.har");
        using var har = JsonDocument.Parse(File.ReadAllText(path));
        var body = har.RootElement.GetProperty("log").GetProperty("entries")[index].GetProperty("request").GetProperty("postData").GetProperty("text").GetString()!;
        return Inner(body);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // A page fetch has no body: it is recorded by its link.
        SentInnerRequests.Enqueue(request.Content is null ? request.RequestUri!.AbsoluteUri : Inner(await request.Content.ReadAsStringAsync(ct)));
        if (!_answers.TryDequeue(out var answer))
            throw new InvalidOperationException("No recorded answer is queued: the tool sent more requests than the test expected.");
        return answer();
    }

    private RecordedGoogle Page(string html)
    {
        _answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        return this;
    }

    private static string RecordedAnswer(string reference, int index)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "GoogleFlights", "References", $"google-flights-{reference}.har");
        using var har = JsonDocument.Parse(File.ReadAllText(path));
        return har.RootElement.GetProperty("log").GetProperty("entries")[index]
            .GetProperty("response").GetProperty("content").GetProperty("text").GetString()!;
    }

    private static string Inner(string body)
    {
        var freq = Uri.UnescapeDataString(body.Split('&')[0]["f.req=".Length..]);
        using var doc = JsonDocument.Parse(freq);
        return doc.RootElement[0].ValueKind == JsonValueKind.Null ? doc.RootElement[1].GetString()! : doc.RootElement[0][0][1].GetString()!;
    }
}
