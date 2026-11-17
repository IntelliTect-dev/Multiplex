using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using TripIt.Http;

namespace Multiplex.Tests.Services.TripIt;

/// <summary>
/// The per-account pacing handler, on a fake clock: spacing, one request in flight per
/// account, Retry-After and back-off for throttled GETs, and no retries for POSTs.
/// </summary>
public sealed class PacingHandlerTests
{
    private readonly ManualClock _clock = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly ScriptedHandler _inner = new();

    public PacingHandlerTests() => _inner.Clock = _clock;

    private HttpClient Client(TripItPacingOptions? options = null, double jitter = 0)
    {
        var pacer = new RequestPacer(options ?? new TripItPacingOptions(), _clock, () => jitter, (d, _) =>
        {
            lock (_waits) _waits.Add(d);
            _clock.Advance(d);
            return Task.CompletedTask;
        });
        return new HttpClient(new PacingHandler(pacer, _inner));
    }

    private static HttpRequestMessage Get(string account, string path = "/api/v2/get/profile")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://www.tripit.com" + path);
        request.Options.Set(RequestPacer.KeyOption, account);
        return request;
    }

    [Fact]
    public async Task Requests_of_one_account_are_spaced_by_at_least_the_minimum_plus_jitter()
    {
        var http = Client(new TripItPacingOptions { MinSpacing = TimeSpan.FromMilliseconds(250), MaxJitter = TimeSpan.FromMilliseconds(100) }, jitter: 0.5);

        await http.SendAsync(Get("a"));
        await http.SendAsync(Get("a"));

        Assert.Equal([TimeSpan.FromMilliseconds(300)], _waits);
        Assert.Equal(TimeSpan.FromMilliseconds(300), _inner.Starts[1] - _inner.Starts[0]);
    }

    [Fact]
    public async Task Different_accounts_do_not_wait_for_each_other()
    {
        var http = Client();

        await http.SendAsync(Get("a"));
        await http.SendAsync(Get("b"));

        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Only_one_request_per_account_is_in_flight()
    {
        var http = Client(new TripItPacingOptions { MinSpacing = TimeSpan.Zero, MaxJitter = TimeSpan.Zero });
        _inner.Gate = new TaskCompletionSource();

        var first = http.SendAsync(Get("a"));
        await _inner.EnteredA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = http.SendAsync(Get("a"));
        var other = http.SendAsync(Get("b"));
        await other.WaitAsync(TimeSpan.FromSeconds(10));   // another account is not held up

        Assert.Equal(1, _inner.InFlight("a"));              // the second request of "a" waits outside
        _inner.Gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _inner.MaxConcurrentFor("a"));
        Assert.Equal(3, _inner.Starts.Count);
    }

    [Fact]
    public async Task The_next_request_waits_until_the_previous_body_has_arrived()
    {
        var http = Client(new TripItPacingOptions { MinSpacing = TimeSpan.Zero, MaxJitter = TimeSpan.Zero });
        var body = new TaskCompletionSource();
        _inner.Respond = n => n == 0 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowContent(body.Task) } : Ok();

        var first = http.SendAsync(Get("a"), HttpCompletionOption.ResponseHeadersRead);
        await _inner.EnteredA.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = http.SendAsync(Get("a"), HttpCompletionOption.ResponseHeadersRead);
        await Task.Delay(200);

        Assert.Single(_inner.Starts);   // headers are in, but the first body is still downloading
        body.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, _inner.Starts.Count);
    }

    [Fact]
    public async Task Throttled_GET_honours_Retry_After_seconds()
    {
        var http = Client();
        _inner.Respond = n => n == 0 ? Throttled(HttpStatusCode.TooManyRequests, r => r.Delta = TimeSpan.FromSeconds(3)) : Ok();

        var response = await http.SendAsync(Get("a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TimeSpan.FromSeconds(3), _waits);
        Assert.Equal(2, _inner.Starts.Count);
    }

    [Fact]
    public async Task Throttled_GET_honours_Retry_After_date()
    {
        var http = Client();
        var until = _clock.GetUtcNow() + TimeSpan.FromSeconds(7);
        _inner.Respond = n => n == 0 ? Throttled(HttpStatusCode.ServiceUnavailable, r => r.Date = until) : Ok();

        var response = await http.SendAsync(Get("a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TimeSpan.FromSeconds(7), _waits);
    }

    [Fact]
    public async Task Throttled_GET_without_Retry_After_backs_off_1_2_4_seconds_then_gives_up()
    {
        var http = Client(new TripItPacingOptions { MinSpacing = TimeSpan.Zero, MaxJitter = TimeSpan.Zero });
        _inner.Respond = _ => Throttled(HttpStatusCode.ServiceUnavailable, null);

        var response = await http.SendAsync(Get("a"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _waits);
        Assert.Equal(4, _inner.Starts.Count);
    }

    [Fact]
    public async Task Back_off_jitter_stays_within_twenty_percent()
    {
        var http = Client(new TripItPacingOptions { MinSpacing = TimeSpan.Zero, MaxJitter = TimeSpan.Zero }, jitter: 1.0);
        _inner.Respond = n => n == 0 ? Throttled(HttpStatusCode.TooManyRequests, null) : Ok();

        await http.SendAsync(Get("a"));

        Assert.Equal([TimeSpan.FromSeconds(1.2)], _waits);
    }

    [Fact]
    public async Task Retry_After_beyond_the_cap_is_not_waited_out()
    {
        var http = Client();
        _inner.Respond = _ => Throttled(HttpStatusCode.TooManyRequests, r => r.Delta = TimeSpan.FromSeconds(300));

        var response = await http.SendAsync(Get("a"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_inner.Starts);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Throttled_POST_is_never_retried()
    {
        var http = Client();
        _inner.Respond = _ => Throttled(HttpStatusCode.TooManyRequests, r => r.Delta = TimeSpan.FromSeconds(1));
        var post = new HttpRequestMessage(HttpMethod.Post, "https://www.tripit.com/api/v2/create/trip") { Content = new StringContent("{}") };
        post.Options.Set(RequestPacer.KeyOption, "a");

        var response = await http.SendAsync(post);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_inner.Starts);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private static HttpResponseMessage Throttled(HttpStatusCode status, Action<RetryConditionHeaderValueBuilder>? retryAfter)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfter is not null)
        {
            var b = new RetryConditionHeaderValueBuilder();
            retryAfter(b);
            response.Headers.RetryAfter = b.Date is { } date ? new RetryConditionHeaderValue(date) : new RetryConditionHeaderValue(b.Delta!.Value);
        }
        return response;
    }

    /// <summary>A body that arrives only when <paramref name="until"/> completes.</summary>
    private sealed class SlowContent(Task until) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await until;
            await stream.WriteAsync("{}"u8.ToArray());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RetryConditionHeaderValueBuilder
    {
        public TimeSpan? Delta { get; set; }
        public DateTimeOffset? Date { get; set; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
        private readonly object _lock = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _now;
        }

        public void Advance(TimeSpan by)
        {
            lock (_lock) _now += by;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _inFlight = new();
        private readonly ConcurrentDictionary<string, int> _max = new();
        private int _calls;

        public List<DateTimeOffset> Starts { get; } = [];
        public Func<int, HttpResponseMessage> Respond { get; set; } = _ => Ok();
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource EnteredA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public int InFlight(string account) => _inFlight.GetValueOrDefault(account);
        public int MaxConcurrentFor(string account) => _max.GetValueOrDefault(account);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var account = request.Options.TryGetValue(RequestPacer.KeyOption, out var k) ? k : "";
            var now = _inFlight.AddOrUpdate(account, 1, (_, n) => n + 1);
            _max.AddOrUpdate(account, now, (_, m) => Math.Max(m, now));
            lock (Starts) Starts.Add(Clock.GetUtcNow());
            try
            {
                if (account == "a") EnteredA.TrySetResult();
                if (Gate is { } gate && account == "a") await gate.Task.WaitAsync(ct);
                return Respond(Interlocked.Increment(ref _calls) - 1);
            }
            finally
            {
                _inFlight.AddOrUpdate(account, 0, (_, n) => n - 1);
            }
        }
    }
}
