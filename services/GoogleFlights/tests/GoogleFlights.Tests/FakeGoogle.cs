using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>A stand-in for www.google.com that replays recorded answers and records what the client sent.</summary>
internal sealed class FakeGoogle : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _answers = new();

    public List<SentRequest> Sent { get; } = [];

    /// <summary>When set, every request waits for it before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public FakeGoogle Answer(HarEntry recorded) => Answer(() => Response(recorded));

    public FakeGoogle Answer(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? configure = null) => Answer(() =>
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        configure?.Invoke(r);
        return r;
    });

    public FakeGoogle Answer(Func<HttpResponseMessage> answer)
    {
        _answers.Enqueue(answer);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // The raw values as the client set them, read before anything parses them: what goes on the wire.
        var headers = request.Headers.NonValidated
            .Concat(request.Content?.Headers.NonValidated ?? default)
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Sent)
            Sent.Add(new SentRequest(request.RequestUri!, headers, body));
        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);
        if (!_answers.TryDequeue(out var answer))
            throw new InvalidOperationException("The fake Google has no more answers queued: the client sent more requests than the test expected.");
        return answer();
    }

    private static HttpResponseMessage Response(HarEntry recorded) => new((HttpStatusCode)recorded.Status)
    {
        Content = new StringContent(recorded.ResponseText, Encoding.UTF8, "application/json"),
    };
}

internal sealed record SentRequest(Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Rpc => Url.AbsolutePath.Split('/').Last() is var last && last == "batchexecute"
        ? System.Web.HttpUtility.ParseQueryString(Url.Query)["rpcids"]!
        : Url.AbsolutePath.Split('/').Last();

    /// <summary>The inner request inside <c>f.req</c>.</summary>
    public string InnerRequest
    {
        get
        {
            var freq = Uri.UnescapeDataString(Body.Split('&')[0]["f.req=".Length..]);
            using var doc = System.Text.Json.JsonDocument.Parse(freq);
            return doc.RootElement[0].ValueKind == System.Text.Json.JsonValueKind.Null
                ? doc.RootElement[1].GetString()!
                : doc.RootElement[0][0][1].GetString()!;
        }
    }
}
