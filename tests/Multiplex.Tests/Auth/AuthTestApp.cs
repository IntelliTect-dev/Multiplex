using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Web.Auth;
using Multiplex.Web.Auth.Clients;
using Multiplex.Web.Auth.Store;

namespace Multiplex.Tests.Auth;

/// <summary>
/// The real app with an in-memory auth store, a controllable clock, stubbed client metadata documents,
/// and a second authenticated slug (<c>other</c>). The public base URL deliberately differs from the
/// test server's own host, so tests prove nothing is derived from the request.
/// </summary>
public class AuthTestApp : WebApplicationFactory<Program>
{
    public const string BaseUrl = "https://mcp.example.test";
    public const string TripItResource = BaseUrl + "/tripit/mcp";
    public const string OtherResource = BaseUrl + "/other/mcp";

    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public StubDocuments Documents { get; } = new();

    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Multiplex:PublicBaseUrl"] = BaseUrl,
        ["Multiplex:DataPath"] = Path.Combine(Path.GetTempPath(), "multiplex-tests", Guid.NewGuid().ToString("N")),
        ["Multiplex:Auth:RegisterPermitLimit"] = "10000",
        ["Multiplex:Auth:TokenPermitLimit"] = "10000",
        ["Multiplex:Auth:AuthorizePermitLimit"] = "10000",
        [Multiplex.Web.Identity.AllowedPeople.Key] = Identity.TestPeople.AllowList,
    };

    /// <summary>False keeps the app's own encrypted file store under <c>Multiplex:DataPath</c>.</summary>
    protected virtual bool InMemoryStore => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (InMemoryStore)
            {
                services.RemoveAll<IAuthStore>();
                services.AddSingleton<IAuthStore, InMemoryAuthStore>();
            }
            services.AddHttpClient(ClientMetadataDocuments.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Documents);
            services.AddMultiplexAuth(new ConfigurationBuilder().Build()).RequireAuthFor("other");
        });
    }

    /// <summary>A client that does not follow redirects, so authorize responses can be inspected.</summary>
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A client that does not follow redirects, signed in to Multiplex as <paramref name="email"/>.</summary>
    public async Task<HttpClient> SignedInClientAsync(string email = Identity.TestPeople.Owner)
    {
        var (_, cookie) = await Identity.TestPeople.SignInAsync(Services, email);
        return Identity.TestPeople.WithCookie(Client(), cookie);
    }
}

/// <summary>The app as deployed: encrypted file store and key ring under one data folder.</summary>
public sealed class FileStoreAuthTestApp(string dataPath) : AuthTestApp
{
    protected override bool InMemoryStore => false;

    protected override IDictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Multiplex:DataPath"] = dataPath;
            return settings;
        }
    }
}

public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Serves client metadata documents by URL instead of the network.</summary>
public sealed class StubDocuments : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _responses = new();
    private readonly ConcurrentQueue<string> _requested = new();

    public IEnumerable<string> Requested => _requested;

    public void Json(string url, string json) => _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public void Redirect(string url, string location) => _responses[url] = () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        _requested.Enqueue(url);
        return Task.FromResult(_responses.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
