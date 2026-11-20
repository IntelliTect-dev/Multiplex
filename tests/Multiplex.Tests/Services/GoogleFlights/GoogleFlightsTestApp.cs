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
            services.AddSingleton(_ => new GoogleFlightsClient(
                new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 0 }, TimeProvider.System, Google));
        });
    }

    public McpTestClient Mcp() =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), token: null, "/google-flights/mcp");
}

/// <summary>Answers each request with the next queued recorded answer, and keeps what was sent.</summary>
public sealed class RecordedGoogle : HttpMessageHandler
{
    private readonly ConcurrentQueue<string> _answers = new();

    public ConcurrentQueue<string> SentInnerRequests { get; } = new();

    /// <summary>Queues entry <paramref name="index"/> of <c>google-flights-&lt;reference&gt;.har</c>.</summary>
    public RecordedGoogle Answer(string reference, int index)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "GoogleFlights", "References", $"google-flights-{reference}.har");
        using var har = JsonDocument.Parse(File.ReadAllText(path));
        _answers.Enqueue(har.RootElement.GetProperty("log").GetProperty("entries")[index]
            .GetProperty("response").GetProperty("content").GetProperty("text").GetString()!);
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
        SentInnerRequests.Enqueue(Inner(await request.Content!.ReadAsStringAsync(ct)));
        if (!_answers.TryDequeue(out var answer))
            throw new InvalidOperationException("No recorded answer is queued: the tool sent more requests than the test expected.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }

    private static string Inner(string body)
    {
        var freq = Uri.UnescapeDataString(body.Split('&')[0]["f.req=".Length..]);
        using var doc = JsonDocument.Parse(freq);
        return doc.RootElement[0].ValueKind == JsonValueKind.Null ? doc.RootElement[1].GetString()! : doc.RootElement[0][0][1].GetString()!;
    }
}
