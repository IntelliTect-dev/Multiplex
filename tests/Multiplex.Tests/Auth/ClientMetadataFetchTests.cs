using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplex.Web.Auth.Clients;

namespace Multiplex.Tests.Auth;

/// <summary>
/// Claude identifies itself with a client ID metadata document that the server fetches while
/// the person is connecting. On a busy PC that fetch can take several seconds; a slow fetch must
/// not fail the connect, and a fetch that failed only for the moment must not be remembered.
/// </summary>
public sealed class ClientMetadataFetchTests
{
    private const string ClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    private const string Document = """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"none"}""";

    /// <summary>Answers each request with the next scripted step: a delay, then a status and body.</summary>
    private sealed class ScriptedDocuments : HttpMessageHandler
    {
        private readonly Queue<(TimeSpan Delay, HttpStatusCode Status, string Body)> _steps = new();
        public int Requests { get; private set; }

        public ScriptedDocuments Then(TimeSpan delay, HttpStatusCode status = HttpStatusCode.OK, string body = Document)
        {
            _steps.Enqueue((delay, status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var (delay, status, body) = _steps.Count > 1 ? _steps.Dequeue() : _steps.Peek();
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ClientMetadataDocuments Documents, TestClock Clock) Create(ScriptedDocuments handler)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        return (new ClientMetadataDocuments(new Factory(handler), clock, NullLogger<ClientMetadataDocuments>.Instance), clock);
    }

    [Fact]
    public async Task A_document_that_takes_several_seconds_still_lets_the_person_connect()
    {
        var (documents, _) = Create(new ScriptedDocuments().Then(TimeSpan.FromSeconds(7))); // over the old 5 s limit

        var lookup = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Null(lookup.Error);
        Assert.NotNull(lookup.Client);
    }

    [Fact]
    public async Task A_fetch_that_timed_out_is_not_remembered_so_trying_again_works()
    {
        var handler = new ScriptedDocuments().Then(TimeSpan.FromSeconds(3)).Then(TimeSpan.Zero);
        var (documents, _) = Create(handler);
        documents.FetchTimeout = TimeSpan.FromSeconds(1);

        var first = await documents.GetAsync(ClientId, CancellationToken.None);
        var second = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Contains("timed out", first.Error);
        Assert.Null(second.Error);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task When_a_refetch_fails_for_the_moment_the_last_good_document_is_used()
    {
        var handler = new ScriptedDocuments()
            .Then(TimeSpan.Zero)
            .Then(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable, "down");
        var (documents, clock) = Create(handler);

        Assert.NotNull((await documents.GetAsync(ClientId, CancellationToken.None)).Client);
        clock.Advance(TimeSpan.FromMinutes(30)); // past the 10-minute cache
        var later = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Null(later.Error);
        Assert.NotNull(later.Client);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task A_definite_failure_is_still_remembered_for_a_minute()
    {
        var handler = new ScriptedDocuments().Then(TimeSpan.Zero, HttpStatusCode.NotFound, "no");
        var (documents, _) = Create(handler);

        var first = await documents.GetAsync(ClientId, CancellationToken.None);
        var second = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Contains("404", first.Error);
        Assert.Equal(first.Error, second.Error);
        Assert.Equal(1, handler.Requests);
    }
}
