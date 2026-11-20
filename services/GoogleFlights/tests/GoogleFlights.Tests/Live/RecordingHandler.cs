using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Multiplex.Sites.GoogleFlights.Tests.Live;

/// <summary>
/// Saves every live exchange with Google, in full, as a HAR in the repository's gitignored capture store
/// (<c>.har-captures/www.google.com/&lt;stamp&gt;-live/raw.har</c> in the main checkout). A refusal or a block
/// is the evidence the client's classifier is built from, so nothing is dropped, and redirects are kept
/// as answered, never followed.
/// </summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly Lock _lock = new();
    private readonly JsonArray _entries = [];
    private readonly string _file;

    public RecordingHandler(HttpMessageHandler inner, string description) : base(inner)
    {
        var dir = Path.Combine(CaptureStore(), "www.google.com", $"{DateTime.Now:yyyy-MM-dd-HHmmss}-live");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CAPTURE.md"),
            $"# issue #89: {description}\n\nDirect HTTP requests made by the client's live tests, not a browser recording. Redirects were not followed.\n");
        _file = Path.Combine(dir, "raw.har");
        Folder = dir;
    }

    public string Folder { get; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var requestHeaders = Headers(request.Headers.NonValidated.Concat(request.Content?.Headers.NonValidated ?? default));
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var clock = Stopwatch.StartNew();
        var response = await base.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        clock.Stop();

        var entry = new JsonObject
        {
            ["startedDateTime"] = started.ToString("O"),
            ["time"] = clock.ElapsedMilliseconds,
            ["request"] = new JsonObject
            {
                ["method"] = request.Method.Method,
                ["url"] = request.RequestUri!.ToString(),
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = requestHeaders,
                ["postData"] = new JsonObject { ["mimeType"] = "application/x-www-form-urlencoded;charset=UTF-8", ["text"] = body },
                ["cookies"] = new JsonArray(), ["queryString"] = new JsonArray(), ["headersSize"] = -1, ["bodySize"] = body.Length,
            },
            ["response"] = new JsonObject
            {
                ["status"] = (int)response.StatusCode,
                ["statusText"] = response.ReasonPhrase,
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = Headers(response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated)),
                ["content"] = new JsonObject { ["size"] = text.Length, ["mimeType"] = response.Content.Headers.ContentType?.ToString() ?? "", ["text"] = text },
                ["redirectURL"] = response.Headers.Location?.ToString() ?? "",
                ["cookies"] = new JsonArray(), ["headersSize"] = -1, ["bodySize"] = text.Length,
            },
            ["cache"] = new JsonObject(),
            ["timings"] = new JsonObject { ["send"] = 0, ["wait"] = clock.ElapsedMilliseconds, ["receive"] = 0 },
        };
        lock (_lock)
        {
            _entries.Add(entry);
            var har = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["version"] = "1.2",
                    ["creator"] = new JsonObject { ["name"] = "google-flights live tests", ["version"] = "0.1" },
                    ["entries"] = JsonNode.Parse(_entries.ToJsonString()),
                },
            };
            File.WriteAllText(_file, har.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // The body was read; give the client a fresh copy.
        var copy = new HttpResponseMessage(response.StatusCode) { RequestMessage = request, ReasonPhrase = response.ReasonPhrase };
        foreach (var h in response.Headers)
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        copy.Content = new StringContent(text);
        foreach (var h in response.Content.Headers)
            copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Dispose();
        return copy;
    }

    private static JsonArray Headers(IEnumerable<KeyValuePair<string, System.Net.Http.Headers.HeaderStringValues>> headers) =>
        new([.. headers.SelectMany(h => h.Value.Select(v => (JsonNode)new JsonObject { ["name"] = h.Key, ["value"] = v }))]);

    /// <summary>The main checkout's <c>.har-captures</c>: a worktree's copy would be deleted with the worktree.</summary>
    private static string CaptureStore()
    {
        var git = Process.Start(new ProcessStartInfo("git", "rev-parse --path-format=absolute --git-common-dir")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        })!;
        var commonDir = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        if (git.ExitCode != 0 || commonDir.Length == 0)
            throw new InvalidOperationException("Live tests record into the repository's capture store; run them from inside the repository.");
        return Path.Combine(Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'))!, ".har-captures");
    }
}
