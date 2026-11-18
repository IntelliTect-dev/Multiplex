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
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();
        public int Requests { get; private set; }

        public ScriptedDocuments Then(TimeSpan delay, HttpStatusCode status = HttpStatusCode.OK, string body = Document)
        {
            _steps.Enqueue(async ct =>
            {
                await Task.Delay(delay, ct);
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
            return this;
        }

        public ScriptedDocuments ThenThrow(HttpRequestError error)
        {
            _steps.Enqueue(_ => throw new HttpRequestException(error, "scripted"));
            return this;
        }

        /// <summary>Headers arrive, then the connection drops while the body is read.</summary>
        public ScriptedDocuments ThenDropMidBody()
        {
            _steps.Enqueue(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) }));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return (_steps.Count > 1 ? _steps.Dequeue() : _steps.Peek())(ct);
        }

        private sealed class DroppingStream : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                ValueTask.FromException<int>(new IOException("The connection was reset."));
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
    public async Task A_withdrawn_document_is_not_brought_back_by_a_later_outage()
    {
        var handler = new ScriptedDocuments()
            .Then(TimeSpan.Zero)
            .Then(TimeSpan.Zero, HttpStatusCode.NotFound, "gone")
            .Then(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable, "down");
        var (documents, clock) = Create(handler);

        Assert.NotNull((await documents.GetAsync(ClientId, CancellationToken.None)).Client);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains("404", (await documents.GetAsync(ClientId, CancellationToken.None)).Error);
        clock.Advance(TimeSpan.FromMinutes(2));
        var during = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Null(during.Client);
    }

    [Fact]
    public async Task During_an_outage_the_fallback_is_reused_for_a_minute_without_waiting_again()
    {
        var handler = new ScriptedDocuments()
            .Then(TimeSpan.Zero)
            .Then(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable, "down");
        var (documents, clock) = Create(handler);

        await documents.GetAsync(ClientId, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.NotNull((await documents.GetAsync(ClientId, CancellationToken.None)).Client);
        Assert.NotNull((await documents.GetAsync(ClientId, CancellationToken.None)).Client);

        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task The_last_good_document_is_not_used_after_six_hours()
    {
        var handler = new ScriptedDocuments()
            .Then(TimeSpan.Zero)
            .Then(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable, "down");
        var (documents, clock) = Create(handler);

        await documents.GetAsync(ClientId, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(7));

        Assert.Null((await documents.GetAsync(ClientId, CancellationToken.None)).Client);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task A_host_that_cannot_work_is_remembered_rather_than_retried(HttpRequestError error)
    {
        var handler = new ScriptedDocuments().ThenThrow(error);
        var (documents, _) = Create(handler);

        await documents.GetAsync(ClientId, CancellationToken.None);
        await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.Equal(1, handler.Requests);
    }

    /// <summary>Counts requests that reach the production handler (which never reaches the network here).</summary>
    private sealed class Counting(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return base.SendAsync(request, ct);
        }
    }

    [Theory]
    [InlineData("https://localhost/oauth/client.json")]                     // resolves only to loopback: never public
    [InlineData("https://no-such-host.invalid/oauth/client.json")]          // .invalid never resolves (RFC 6761)
    public async Task Through_the_real_connection_a_host_that_cannot_work_is_remembered(string clientId)
    {
        var counting = new Counting(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ClientMetadataDocuments.ConnectToPublicAddressAsync,
        });
        var clock = new TestClock(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var documents = new ClientMetadataDocuments(new Factory(counting), clock, NullLogger<ClientMetadataDocuments>.Instance);

        var first = await documents.GetAsync(clientId, CancellationToken.None);
        var second = await documents.GetAsync(clientId, CancellationToken.None);

        Assert.NotNull(first.Error);
        Assert.Equal(first.Error, second.Error);
        Assert.Equal(1, counting.Requests);
    }

    [Fact]
    public async Task A_connection_dropped_mid_document_is_a_failure_for_the_moment_not_an_error()
    {
        var handler = new ScriptedDocuments().ThenDropMidBody().Then(TimeSpan.Zero);
        var (documents, _) = Create(handler);

        var first = await documents.GetAsync(ClientId, CancellationToken.None);
        var second = await documents.GetAsync(ClientId, CancellationToken.None);

        Assert.NotNull(first.Error);
        Assert.NotNull(second.Client);
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
