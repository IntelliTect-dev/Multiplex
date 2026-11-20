using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Tests.Identity;
using Multiplex.Tests.SignIn;
using Multiplex.Web.Identity;
using Multiplex.Web.Auth.Store;
using Multiplex.Web.Services.TripIt;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Services.TripIt;

/// <summary>
/// Multiplex with its real MCP pipeline and its real OAuth protection at <c>/tripit/mcp</c>:
/// each test account gets a bearer token through the actual register, authorize and token
/// endpoints (the connect pages' part is played by <see cref="IAuthorizationCompleter"/>),
/// with an in-memory auth store, in-memory session store and refresher, and a fake TripIt
/// behind the real pacing handler.
/// </summary>
/// <param name="signInSite">
/// When set, the connect pages stay in: TripIt's sign-in is a live sign-in (local Chromium,
/// headless) against this stub login site, and the tools read the sessions it stores from the
/// real encrypted store, so a connection is added the whole way a person adds one
/// (<see cref="ConnectThroughPagesAsync"/>).
/// </param>
public sealed class TripItTestHost(bool fakeTripIt = true, Uri? signInSite = null) : WebApplicationFactory<Program>
{
    private static readonly TimeSpan s_signInTimeout = TimeSpan.FromSeconds(90);
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "multiplex-tripit-tests-" + Guid.NewGuid().ToString("N"));

    public FakeSessionStore Store { get; } = new();
    public FakeRefresher Refresher { get; } = new();
    public FakeTripIt TripIt { get; } = new();
    public TimeProvider? Clock { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Multiplex:DataPath", _dataPath);
        builder.UseSetting("Multiplex:PublicBaseUrl", BaseUrl);
        builder.UseSetting("Multiplex:Auth:RegisterPermitLimit", "10000");
        builder.UseSetting("Multiplex:Auth:TokenPermitLimit", "10000");
        builder.UseSetting("Multiplex:Auth:AuthorizePermitLimit", "10000");
        builder.UseSetting(AllowedPeople.Key, TestPeople.AllowList);
        if (fakeTripIt)
        {
            builder.UseSetting($"{TripItServiceOptions.Section}:MinRequestSpacingMs", "0");
            builder.UseSetting($"{TripItServiceOptions.Section}:RequestJitterMs", "0");
        }
        if (signInSite is not null)
        {
            // Never a browser download, and never a visible window (TripIt's own default is headed).
            builder.UseSetting("Browser:ExecutablePath", ChromiumLocator.Path ?? Path.Combine(Path.GetTempPath(), "no-such-chrome.exe"));
            builder.UseSetting("SignIn:Headless", "true");
            builder.UseSetting($"Multiplex:Sites:{TripItAccountSessions.Site}:Headless", "true");
            builder.UseSetting("SignIn:PollInterval", "00:00:00.500");
        }
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthStore>();
            services.AddSingleton<IAuthStore, InMemoryAuthStore>();
            if (signInSite is null)
            {
                services.AddSingleton<ISiteSessionStore>(Store);
            }
            else
            {
                services.RemoveAll<ISiteSignInDefinition>();
                services.AddSingleton<ISiteSignInDefinition>(new StubSignInDefinition(signInSite, TripItAccountSessions.Site));
            }
            // Live runs have no browser to refresh from: a rejected session must not look like a sign-out.
            if (fakeTripIt) services.AddSingleton<ISiteSessionRefresher>(Refresher);
            if (fakeTripIt)
            {
                services.AddHttpClient(TripItServiceRegistration.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => TripIt);
            }
            if (Clock is { } clock) services.AddSingleton(clock);
        });
    }

    /// <summary>Stores <paramref name="person"/>'s session snapshot for <paramref name="account"/>, whose cookie is <paramref name="cookie"/>.</summary>
    public void SignIn(string account, string cookie, string person = TestPeople.Owner)
        => Store.Data[FakeSessionStore.Key(TestPeople.OwnerOf(person), account)] = Snapshot(cookie);

    public static string Snapshot(string cookie)
        => JsonSerializer.Serialize(new { cookie, csrf_token = "csrf-" + cookie, spa_headers = new Dictionary<string, string> { ["user-agent"] = "Test" }, captured_at = "2026-11-17T08:00:00Z" });

    public const string BaseUrl = "https://mcp.example.test";
    private const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";
    private readonly ConcurrentDictionary<string, Task<string>> _tokens = new();

    /// <summary>An MCP JSON-RPC client for one person's connection to one account (account null: no token at all).</summary>
    public McpTestClient McpFor(string? account, string person = TestPeople.Owner) =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
            account is null ? null : () => _tokens.GetOrAdd(person + "/" + account, _ => TokenForAsync(person, account)));

    /// <summary>Connects <paramref name="person"/> to <paramref name="account"/> the way Claude does and returns the access token.</summary>
    private async Task<string> TokenForAsync(string person, string account)
    {
        var (signedIn, cookie) = await TestPeople.SignInAsync(Services, person);
        using var http = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).WithCookie(cookie);
        var (clientId, verifier, pendingId) = await AuthorizeAsync(http);

        var completer = Services.GetRequiredService<IAuthorizationCompleter>();
        var back = await completer.CompleteAsync(pendingId, signedIn.Owner, new SiteAccount("tripit", account, [], account), CancellationToken.None);

        return await ExchangeAsync(http, clientId, verifier, back);
    }

    /// <summary>
    /// Adds one connection the whole way a person does (needs <c>signInSite</c>): the client
    /// registers and authorizes for <paramref name="person"/>, who is signed in to Multiplex; they press
    /// "Sign in" and sign in on the live page as <paramref name="email"/>; the client exchanges the code.
    /// Returns that connection's MCP client.
    /// </summary>
    public async Task<McpTestClient> ConnectThroughPagesAsync(string email, string password, string person = TestPeople.Owner)
    {
        var browser = new ConnectClient(this);
        await browser.SignInAsync(person);
        var (clientId, verifier, pendingId) = await AuthorizeAsync(browser.Http);

        var (start, _) = await browser.StartSignInAsync(pendingId, slug: TripItAccountSessions.Site);
        Assert.Equal(HttpStatusCode.SeeOther, start.StatusCode);
        var attempt = Services.GetRequiredService<LiveSignInRegistry>().Find(ConnectClient.AttemptIdOf(start.Headers.Location!))
            ?? throw new InvalidOperationException("Signing in did not lead to a live sign-in: " + start.Headers.Location);
        await using (var mirror = await browser.OpenMirrorAsync(attempt.Id, TripItAccountSessions.Site))
        {
            await mirror.WaitForStatusAsync("live", s_signInTimeout);
            await LiveMirrorTests.SignInAsync(attempt, mirror, email, password);
            var ready = await mirror.WaitForStatusAsync("ready", s_signInTimeout);
            var (done, _) = await browser.GetAsync(ready.GetProperty("next").GetString()!);
            Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
            await attempt.Completion.WaitAsync(s_signInTimeout);

            var token = await ExchangeAsync(browser.Http, clientId, verifier, done.Headers.Location!);
            return new McpTestClient(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), () => Task.FromResult(token));
        }
    }

    /// <summary>The session cookie the live sign-in stored for <paramref name="person"/>'s <paramref name="account"/>.</summary>
    public async Task<string> StoredCookieAsync(string account, string person = TestPeople.Owner)
    {
        var snapshot = await Services.GetRequiredService<ISiteSessionStore>()
            .GetAsync(TestPeople.OwnerOf(person), TripItAccountSessions.Site, account, CancellationToken.None)
            ?? throw new InvalidOperationException("No session is stored for " + account);
        return JsonDocument.Parse(snapshot).RootElement.GetProperty("cookie").GetString()!;
    }

    /// <summary>Registers a client like Claude and starts an authorization; returns where the connect page takes over.</summary>
    private static async Task<(string ClientId, string Verifier, string PendingId)> AuthorizeAsync(HttpClient http)
    {
        var registration = await http.PostAsync("/oauth/register", new StringContent(
            JsonSerializer.Serialize(new { client_name = "Claude", redirect_uris = new[] { ClaudeRedirect }, token_endpoint_auth_method = "none" }),
            Encoding.UTF8, "application/json"));
        registration.EnsureSuccessStatusCode();
        var clientId = JsonDocument.Parse(await registration.Content.ReadAsStringAsync()).RootElement.GetProperty("client_id").GetString()!;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = ClaudeRedirect, ["state"] = "s",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["resource"] = BaseUrl + "/tripit/mcp",
        });
        var connect = await http.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        var pendingId = QueryHelpers.ParseQuery(new Uri(new Uri(BaseUrl), connect.Headers.Location!).Query)["p"].ToString();
        return (clientId, verifier, pendingId);
    }

    /// <summary>Exchanges the code in <paramref name="callback"/> (the client's redirect URI) for an access token.</summary>
    private static async Task<string> ExchangeAsync(HttpClient http, string clientId, string verifier, Uri callback)
    {
        var code = QueryHelpers.ParseQuery(callback.Query)["code"].ToString();
        var token = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = ClaudeRedirect,
            ["client_id"] = clientId, ["code_verifier"] = verifier, ["resource"] = BaseUrl + "/tripit/mcp",
        }));
        token.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Sessions by person and account (<see cref="Key"/>), as the real store keeps them.</summary>
public sealed class FakeSessionStore : ISiteSessionStore
{
    public ConcurrentDictionary<string, string> Data { get; } = new();
    public List<string> Writes { get; } = [];

    public static string Key(string owner, string accountId) => owner + "/" + accountId;

    /// <summary>The default test person's session for <paramref name="accountId"/>.</summary>
    public string this[string accountId] => Data[Key(TestPeople.OwnerOf(), accountId)];

    public Task<string?> GetAsync(string owner, string site, string accountId, CancellationToken ct)
        => Task.FromResult(site == "tripit" && Data.TryGetValue(Key(owner, accountId), out var json) ? json : null);

    public Task SetAsync(string owner, string site, string accountId, string snapshotJson, CancellationToken ct)
    {
        lock (Writes) Writes.Add(accountId);
        Data[Key(owner, accountId)] = snapshotJson;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        Data.TryRemove(Key(owner, accountId), out _);
        return Task.CompletedTask;
    }
}

/// <summary>Returns the scripted next snapshot per account; null means "signed out".</summary>
public sealed class FakeRefresher : ISiteSessionRefresher
{
    public ConcurrentDictionary<string, Func<string?>> Next { get; } = new();
    public ConcurrentDictionary<string, int> Calls { get; } = new();

    public Task<string?> RefreshAsync(string owner, string site, string accountId, CancellationToken ct)
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

/// <summary>Speaks MCP JSON-RPC over Streamable HTTP (stateless) to one service's address (default <c>/tripit/mcp</c>).</summary>
public sealed class McpTestClient(HttpClient http, Func<Task<string>>? token, string path = "/tripit/mcp")
{
    private int _id;

    public async Task<(HttpStatusCode Status, JsonElement? Message)> SendAsync(string method, object? parameters = null)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _id), method, @params = parameters });
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token());

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
