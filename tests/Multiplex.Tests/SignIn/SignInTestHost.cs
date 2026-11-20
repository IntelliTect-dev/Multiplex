using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Tests.Identity;
using Multiplex.Web.Identity;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>All tests that drive a real Chromium run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SignInBrowserCollection
{
    public const string Name = "SignIn browser";
}

public sealed class SignInTestOptions
{
    /// <summary>The stub site the definition points at; null registers the definition against an unused address.</summary>
    public Uri? StubBaseUrl { get; set; }

    /// <summary>When set, replaces the real refresher.</summary>
    public FakeRefresher? Refresher { get; set; }

    /// <summary>Defaults to the local Chromium, else a path that does not exist (so no browser ever starts).</summary>
    public string ExecutablePath { get; set; } = ChromiumLocator.Path ?? Path.Combine(Path.GetTempPath(), "no-such-chrome.exe");

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Any further configuration.</summary>
    public Dictionary<string, string> Settings { get; } = [];
}

/// <summary>
/// Multiplex in-process with Auth/ faked, a throwaway data folder, the stub site's sign-in
/// definition and the test allowlist (<see cref="TestPeople"/>). Nothing touches the real %LOCALAPPDATA%.
/// </summary>
public sealed class SignInTestHost : IAsyncDisposable
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "multiplex-signin-tests", Guid.NewGuid().ToString("N"));
    public FakeCompleter Completer { get; } = new();
    public FakeRevoker Revoker { get; } = new();

    /// <summary>Every log entry the host writes.</summary>
    public LogCapture Logs { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }
    public StubSignInDefinition Definition { get; }

    public SignInTestHost(SignInTestOptions? options = null)
    {
        options ??= new SignInTestOptions();
        Definition = new StubSignInDefinition(options.StubBaseUrl ?? new Uri("http://127.0.0.1:9/"));
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Multiplex:DataPath", DataPath);
            b.UseSetting("Browser:ExecutablePath", options.ExecutablePath);
            b.UseSetting("SignIn:Headless", "true");
            b.UseSetting("SignIn:PollInterval", "00:00:00.500");
            b.UseSetting("SignIn:AttemptTimeout", options.AttemptTimeout.ToString("c"));
            b.UseSetting("SignIn:RefreshTimeout", "00:00:20");
            b.UseSetting(AllowedPeople.Key, TestPeople.AllowList);
            foreach (var (key, value) in options.Settings) b.UseSetting(key, value);

            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
                services.AddSingleton<ISiteSignInDefinition>(Definition);
                services.RemoveAll<IAuthorizationCompleter>();
                services.AddSingleton<IAuthorizationCompleter>(Completer);
                services.RemoveAll<IGrantRevoker>();
                services.AddSingleton<IGrantRevoker>(Revoker);
                if (options.Refresher is { } refresher)
                {
                    services.RemoveAll<ISiteSessionRefresher>();
                    services.AddSingleton<ISiteSessionRefresher>(refresher);
                }
            });
        });
    }

    /// <summary>Serves the app on a real loopback port (instead of the in-memory test server) so a real browser can open it.</summary>
    public Uri StartKestrel()
    {
        Factory.UseKestrel(0);
        Factory.StartServer();
        var address = Factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1").Replace("localhost", "127.0.0.1")))
            .First();
        return new Uri(address.GetLeftPart(UriPartial.Authority) + "/");
    }

    public IServiceProvider Services => Factory.Services;

    public T Get<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    /// <summary>The default test person's profiles for the stub site.</summary>
    public string ProfilesDirectory => Path.Combine(DataPath, "users", TestPeople.OwnerOf(), "sites", StubSignInDefinition.SiteSlug, "profiles");

    public string[] TempProfiles() =>
        Directory.Exists(ProfilesDirectory) ? Directory.GetDirectories(ProfilesDirectory, "temp-*") : [];

    public ConnectClient NewClient() => new(Factory);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        for (var attempt = 0; attempt < 20 && Directory.Exists(DataPath); attempt++)
        {
            try { Directory.Delete(DataPath, recursive: true); }
            catch (IOException) { await Task.Delay(250); }
            catch (UnauthorizedAccessException) { await Task.Delay(250); }
        }
    }
}

/// <summary>A phone browser, as far as the connect pages can tell: cookies, antiforgery tokens, no auto-redirects.</summary>
public sealed partial class ConnectClient
{
    private readonly WebApplicationFactory<Program> _factory;

    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }

    public ConnectClient(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        Http = factory.CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(Cookies));
    }

    public async Task<(HttpResponseMessage Response, string Html)> GetAsync(string path)
    {
        var response = await Http.GetAsync(path);
        return (response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Posts a form the way the page would, with the antiforgery token from <paramref name="pageHtml"/>.</summary>
    public async Task<(HttpResponseMessage Response, string Html)> PostAsync(string path, string pageHtml, params (string Name, string Value)[] fields)
    {
        var values = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).ToList();
        if (TokenOf(pageHtml) is { } token) values.Add(new("__RequestVerificationToken", token));
        var response = await Http.PostAsync(path, new FormUrlEncodedContent(values));
        return (response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Signs <paramref name="email"/> in to Multiplex on this browser (admitted through the real allowlist,
    /// with the app's own sign-in cookie), as if they had just come back from Microsoft.
    /// </summary>
    public async Task<MultiplexPerson> SignInAsync(string email = TestPeople.Owner)
    {
        var (person, cookie) = await TestPeople.SignInAsync(_factory.Services, email);
        var separator = cookie.IndexOf('=');
        Cookies.Add(new Uri("http://localhost"), new Cookie(cookie[..separator], cookie[(separator + 1)..]) { Path = "/" });
        return person;
    }

    /// <summary>Opens the connect page and presses its sign-in button; returns the POST's response.</summary>
    public async Task<(HttpResponseMessage Response, string Html)> StartSignInAsync(string pendingId, string slug = StubSignInDefinition.SiteSlug)
    {
        var (_, page) = await GetAsync($"/connect/{slug}?p={pendingId}");
        return await PostAsync($"/connect/{slug}/start?p={pendingId}", page);
    }

    public static string? TokenOf(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    public static string AttemptIdOf(Uri location) =>
        Regex.Match(location.OriginalString, @"[?&]a=([^&]+)").Groups[1].Value;

    public async Task<MirrorClient> OpenMirrorAsync(string attemptId, string slug = StubSignInDefinition.SiteSlug, string? origin = null)
    {
        var client = _factory.Server.CreateWebSocketClient();
        var path = $"/connect/{slug}/live";
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = Cookies.GetCookieHeader(new Uri("http://localhost" + path));
            if (origin is not null) request.Headers.Origin = origin;
        };
        var socket = await client.ConnectAsync(new Uri($"ws://localhost{path}?a={Uri.EscapeDataString(attemptId)}"), CancellationToken.None);
        return new MirrorClient(socket);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}

/// <summary>The test's end of the live WebSocket: collects frames and status messages, sends input.</summary>
public sealed class MirrorClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Task _receive;
    private readonly ConcurrentQueue<JsonElement> _messages = new();
    private int _frames;

    public MirrorClient(WebSocket socket)
    {
        _socket = socket;
        _receive = Task.Run(ReceiveAsync);
    }

    public int Frames => Volatile.Read(ref _frames);
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;
    public byte[]? LastFrame { get; private set; }
    public IReadOnlyCollection<JsonElement> Messages => _messages;

    private async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 20];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    LastFrame = message.ToArray();
                    Interlocked.Increment(ref _frames);
                }
                else
                {
                    _messages.Enqueue(JsonDocument.Parse(message.ToArray()).RootElement.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public Task SendAsync(object message) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in _messages)
                if (predicate(m)) return m;
            await Task.Delay(100);
        }
        throw new TimeoutException($"No message matching {what ?? "the condition"}; got: {string.Join(" | ", _messages.Select(m => m.GetRawText()))}");
    }

    public Task<JsonElement> WaitForStatusAsync(string state, TimeSpan? timeout = null) =>
        WaitForAsync(m => m.GetProperty("t").GetString() == "status" && m.GetProperty("s").GetString() == state, timeout, $"status '{state}'");

    public async Task WaitForFramesAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (Frames < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Only {Frames} frames arrived.");
            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }
        await _receive.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _socket.Dispose();
    }
}
