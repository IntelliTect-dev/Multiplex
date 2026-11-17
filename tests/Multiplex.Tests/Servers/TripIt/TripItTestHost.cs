using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplex.Web.Servers.TripIt;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Servers.TripIt;

/// <summary>
/// Multiplex with its real MCP pipeline at <c>/tripit/mcp</c>, but with a test authentication
/// scheme (the account comes from an <c>X-Test-Account</c> header), in-memory session store
/// and refresher, and a fake TripIt behind the real pacing handler. It does not depend on the
/// Auth/ or SignIn/ parts.
/// </summary>
public sealed class TripItTestHost(bool fakeTripIt = true) : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "multiplex-tripit-tests-" + Guid.NewGuid().ToString("N"));

    public FakeSessionStore Store { get; } = new();
    public FakeRefresher Refresher { get; } = new();
    public FakeTripIt TripIt { get; } = new();
    public TimeProvider? Clock { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Multiplex:DataPath", _dataPath);
        if (fakeTripIt)
        {
            builder.UseSetting($"{TripItServerOptions.Section}:MinRequestSpacingMs", "0");
            builder.UseSetting($"{TripItServerOptions.Section}:RequestJitterMs", "0");
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuth.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.SchemeName, _ => { });
            services.AddSingleton<ISiteSessionStore>(Store);
            // Live runs have no browser to refresh from: a rejected session must not look like a sign-out.
            if (fakeTripIt) services.AddSingleton<ISiteSessionRefresher>(Refresher);
            if (fakeTripIt)
            {
                services.AddHttpClient(TripItServerRegistration.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => TripIt);
            }
            if (Clock is { } clock) services.AddSingleton(clock);
        });
    }

    /// <summary>Stores a session snapshot for <paramref name="account"/> whose cookie is <paramref name="cookie"/>.</summary>
    public void SignIn(string account, string cookie)
        => Store.Data[account] = Snapshot(cookie);

    public static string Snapshot(string cookie)
        => JsonSerializer.Serialize(new { cookie, csrf_token = "csrf-" + cookie, spa_headers = new Dictionary<string, string> { ["user-agent"] = "Test" }, captured_at = "2026-11-17T08:00:00Z" });

    /// <summary>An MCP JSON-RPC client for one account (null: unauthenticated).</summary>
    public McpTestClient McpFor(string? account) => new(CreateClient(), account);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Authenticates from <c>X-Test-Account</c>, putting the SiteClaims Auth/ would set.</summary>
public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Account", out var account) || string.IsNullOrEmpty(account))
            return Task.FromResult(AuthenticateResult.NoResult());
        var site = Request.Headers.TryGetValue("X-Test-Site", out var s) ? s.ToString() : "tripit";
        var identity = new ClaimsIdentity(
        [
            new Claim(SiteClaims.Site, site),
            new Claim(SiteClaims.AccountId, account.ToString()),
            new Claim(SiteClaims.ClientId, "test-client"),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class FakeSessionStore : ISiteSessionStore
{
    public ConcurrentDictionary<string, string> Data { get; } = new();
    public List<string> Writes { get; } = [];

    public Task<string?> GetAsync(string site, string accountId, CancellationToken ct)
        => Task.FromResult(site == "tripit" && Data.TryGetValue(accountId, out var json) ? json : null);

    public Task SetAsync(string site, string accountId, string snapshotJson, CancellationToken ct)
    {
        lock (Writes) Writes.Add(accountId);
        Data[accountId] = snapshotJson;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string site, string accountId, CancellationToken ct)
    {
        Data.TryRemove(accountId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Returns the scripted next snapshot per account; null means "signed out".</summary>
public sealed class FakeRefresher : ISiteSessionRefresher
{
    public ConcurrentDictionary<string, Func<string?>> Next { get; } = new();
    public ConcurrentDictionary<string, int> Calls { get; } = new();

    public Task<string?> RefreshAsync(string site, string accountId, CancellationToken ct)
    {
        Calls.AddOrUpdate(accountId, 1, (_, n) => n + 1);
        return Task.FromResult(Next.TryGetValue(accountId, out var next) ? next() : null);
    }
}

/// <summary>
/// A fake www.tripit.com. Each cookie names an account's data; a cookie listed in
/// <see cref="Rejected"/> gets the Akamai 403 block page.
/// </summary>
public sealed class FakeTripIt : HttpMessageHandler
{
    public const string BlockPage = "<HTML><HEAD><TITLE>Access Denied</TITLE></HEAD><BODY>Reference #18.x https://errors.edgesuite.net/18.x</BODY></HTML>";

    private readonly ConcurrentDictionary<string, Func<string, HttpRequestMessage, HttpResponseMessage>> _routes = new();

    public ConcurrentBag<string> Rejected { get; } = [];
    public ConcurrentQueue<(string Method, string Path, string? Cookie)> Requests { get; } = new();

    /// <summary>Answers <paramref name="path"/> with <paramref name="respond"/>(cookie, request).</summary>
    public void On(string path, Func<string, HttpRequestMessage, HttpResponseMessage> respond) => _routes[path] = respond;

    public void OnJson(string path, Func<string, string> json) => On(path, (cookie, _) => Json(json(cookie)));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? c.Single() : null;
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method.Method, path, cookie));
        if (cookie is null || Rejected.Contains(cookie))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(BlockPage, Encoding.UTF8, "text/html"),
            });
        }
        if (_routes.TryGetValue(path, out var respond)) return Task.FromResult(respond(cookie, request));
        if (path == "/api/v2/get/profile")
        {
            return Task.FromResult(Json($$$"""{"Profile":{"@attributes":{"ref":"ref-{{{cookie}}}"},"screen_name":"user-{{{cookie}}}","public_display_name":"User {{{cookie}}}"}}"""));
        }
        return Task.FromResult(Json("""{"Error":{"code":"404","description":"no such endpoint in FakeTripIt"}}""", HttpStatusCode.NotFound));
    }
}

/// <summary>Speaks MCP JSON-RPC over Streamable HTTP (stateless) to <c>/tripit/mcp</c>.</summary>
public sealed class McpTestClient(HttpClient http, string? account)
{
    private int _id;

    public async Task<(HttpStatusCode Status, JsonElement? Message)> SendAsync(string method, object? parameters = null)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _id), method, @params = parameters });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tripit/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (account is not null) request.Headers.Add("X-Test-Account", account);

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        // Streamable HTTP replies as SSE ("data: {json-rpc}") or as plain JSON.
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    public async Task<JsonElement> InitializeAsync()
    {
        var (status, message) = await SendAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "multiplex-tests", version = "1.0" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return message!.Value.GetProperty("result");
    }

    public async Task<IReadOnlyList<JsonElement>> ListToolsAsync()
    {
        var (status, message) = await SendAsync("tools/list");
        Assert.Equal(HttpStatusCode.OK, status);
        return [.. message!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()];
    }

    /// <summary>Calls a tool; returns whether it was a tool error and its text.</summary>
    public async Task<(bool IsError, string Text)> CallAsync(string tool, object? arguments = null)
    {
        var (status, message) = await SendAsync("tools/call", new { name = tool, arguments = arguments ?? new { } });
        Assert.Equal(HttpStatusCode.OK, status);
        var root = message!.Value;
        if (root.TryGetProperty("error", out var error)) return (true, error.GetProperty("message").GetString()!);
        var result = root.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }
}
