using System.Net;
using System.Text;
using System.Text.Json;
using TripIt.Auth;

namespace TripIt.UnitTests;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/>: maps a path (without query) to a queue of
/// responses and records every request so tests can assert on paths, methods and headers.
/// The last queued response for a path repeats.
/// </summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();

    public List<(HttpMethod Method, Uri Uri, string? Cookie, string? Body)> Requests { get; } = [];

    public FakeHandler On(string path, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        if (!_routes.TryGetValue(path, out var q)) _routes[path] = q = new();
        q.Enqueue(respond);
        return this;
    }

    public FakeHandler OnJson(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(path, _ => Json(json, status));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string html, HttpStatusCode status)
        => new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues("Cookie", out var cookies);
        Requests.Add((request.Method, request.RequestUri!, cookies?.FirstOrDefault(), body));
        var path = request.RequestUri!.AbsolutePath;
        if (_routes.TryGetValue(path, out var q) && q.Count > 0)
        {
            var respond = q.Count > 1 ? q.Dequeue() : q.Peek();
            return respond(request);
        }
        if (path == "/api/v2/get/profile")
        {
            // Default identity for tools that need "who am I"; tests may route it explicitly.
            return Json("""{"Profile":{"@attributes":{"ref":"p1"},"screen_name":"tester","public_display_name":"Test User"}}""");
        }
        return Json("""{"Error":{"code":"404","description":"no route in FakeHandler"}}""", HttpStatusCode.NotFound);
    }
}

internal static class FakeTripIt
{
    public static (TripItClient Client, FakeHandler Handler, FileSessionStore Store) Create(
        string cookie = "session_id=abc", Func<FileSessionStore, ISessionRefresher>? refresher = null)
    {
        var handler = new FakeHandler();
        var store = FileSessionStore.CreateTemp(cookie);
        var session = new TripItSession(store);
        var client = new TripItClient(session, new HttpClient(handler), null, refresher?.Invoke(store));
        return (client, handler, store);
    }
}

/// <summary>Session store over a temp JSON file; tests rewrite it to simulate re-extraction.</summary>
internal sealed class FileSessionStore : ISessionStore
{
    private FileSessionStore(string path) => FilePath = path;

    public string FilePath { get; }
    public string Description => FilePath;

    public static FileSessionStore CreateTemp(string cookie)
    {
        var store = new FileSessionStore(Path.Combine(Path.GetTempPath(), $"tripit-test-{Guid.NewGuid():N}.json"));
        store.Save(new SessionCredentials(cookie, "csrf"));
        return store;
    }

    public bool TryLoad(out SessionCredentials credentials)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        credentials = new(doc.RootElement.GetProperty("cookie").GetString()!,
            doc.RootElement.GetProperty("csrf_token").GetString()!);
        return credentials.Cookie.Length > 0;
    }

    public void Save(SessionCredentials credentials)
        => File.WriteAllText(FilePath, JsonSerializer.Serialize(new { cookie = credentials.Cookie, csrf_token = credentials.CsrfToken }));

    public void Clear() => Save(new SessionCredentials("", ""));
}

/// <summary>Refresher that rewrites the session file, as extract-session.js would.</summary>
internal sealed class FakeRefresher(FileSessionStore store, string newCookie) : ISessionRefresher
{
    public int Calls { get; private set; }

    public Task RefreshAsync(CancellationToken ct)
    {
        Calls++;
        store.Save(new SessionCredentials(newCookie, "csrf2"));
        return Task.CompletedTask;
    }
}
