using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripIt.Http;

/// <summary>Pacing rules for calls to TripIt (spec section 9: polite pacing, honour throttling).</summary>
public sealed class TripItPacingOptions
{
    /// <summary>Minimum gap between the end of one request and the start of the next, per account.</summary>
    public TimeSpan MinSpacing { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound of the random amount added to <see cref="MinSpacing"/>.</summary>
    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The longest <c>Retry-After</c> honoured. A longer one is not waited out: the throttled
    /// response is returned to the caller instead of retrying early.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Waits before each retry of a throttled GET when TripIt gives no <c>Retry-After</c>.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Largest fraction of a back-off step added as random jitter.</summary>
    public double BackoffJitter { get; set; } = 0.2;
}

/// <summary>
/// Shared pacing state, one gate per key (a TripIt account). A singleton in a host: TripIt
/// clients are created per request and HTTP handler chains are pooled, so per-account state
/// cannot live on either. Requests name their key with <see cref="KeyOption"/>.
/// </summary>
public sealed class RequestPacer
{
    /// <summary>The request option naming the pacing key (account). Absent means a shared default key.</summary>
    public static readonly HttpRequestOptionsKey<string> KeyOption = new("TripIt.PacingKey");

    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="options">Pacing rules; defaults to <see cref="TripItPacingOptions"/>.</param>
    /// <param name="time">Clock; tests pass a fake.</param>
    /// <param name="random">Source of jitter in [0, 1); tests pass a constant.</param>
    /// <param name="delay">How to wait; tests pass one that advances a fake clock.</param>
    public RequestPacer(TripItPacingOptions? options = null, TimeProvider? time = null, Func<double>? random = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Options = options ?? new TripItPacingOptions();
        Time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
        _delay = delay ?? ((d, ct) => Task.Delay(d, Time, ct));
    }

    public TripItPacingOptions Options { get; }

    public TimeProvider Time { get; }

    internal Gate GateFor(string key) => _gates.GetOrAdd(key, static _ => new Gate());

    /// <summary>Waits until the key's spacing since its previous request has elapsed.</summary>
    internal async Task WaitForTurnAsync(Gate gate, CancellationToken ct)
    {
        if (gate.LastCompleted is not { } last) return;
        var spacing = Options.MinSpacing + Options.MaxJitter * _random();
        var wait = last + spacing - Time.GetUtcNow();
        if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
    }

    internal Task DelayAsync(TimeSpan delay, CancellationToken ct) => _delay(delay, ct);

    /// <summary>
    /// How long to wait before retry number <paramref name="attempt"/> (0-based) of a throttled
    /// response, or null when the response should be returned as is.
    /// </summary>
    internal TimeSpan? RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (attempt >= Options.Backoff.Count) return null;
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requested = retryAfter?.Delta
                              ?? (retryAfter?.Date is { } date ? (TimeSpan?)(date - Time.GetUtcNow()) : null);
        if (requested is { } r)
        {
            if (r < TimeSpan.Zero) r = TimeSpan.Zero;
            return r > Options.MaxRetryAfter ? null : r;
        }
        var step = Options.Backoff[attempt];
        return step + step * (Options.BackoffJitter * _random());
    }

    internal sealed class Gate
    {
        public SemaphoreSlim InFlight { get; } = new(1, 1);
        public DateTimeOffset? LastCompleted { get; set; }
    }
}

/// <summary>
/// Paces requests per account: one request in flight, at least
/// <see cref="TripItPacingOptions.MinSpacing"/> (plus a little jitter) between them, and
/// <c>429</c>/<c>503</c> answers to idempotent GETs retried after <c>Retry-After</c> or an
/// exponential back-off. POSTs are never retried. The pacing is for politeness, not to make
/// the traffic look human.
/// </summary>
public sealed class PacingHandler : DelegatingHandler
{
    private readonly RequestPacer _pacer;
    private readonly ILogger _log;

    public PacingHandler(RequestPacer pacer, ILogger<PacingHandler>? log = null)
    {
        _pacer = pacer;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>For use without dependency injection: wraps <paramref name="inner"/>.</summary>
    public PacingHandler(RequestPacer pacer, HttpMessageHandler inner, ILogger<PacingHandler>? log = null)
        : this(pacer, log)
    {
        InnerHandler = inner;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.Options.TryGetValue(RequestPacer.KeyOption, out var k) && k is not null ? k : "";
        var gate = _pacer.GateFor(key);
        var retriable = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;

        await gate.InFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await _pacer.WaitForTurnAsync(gate, ct).ConfigureAwait(false);
                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.LastCompleted = _pacer.Time.GetUtcNow();
                }

                if (!retriable || !IsThrottled(response.StatusCode)) return response;
                if (_pacer.RetryDelay(response, attempt) is not { } wait)
                {
                    _log.LogWarning("TripIt answered {Status} for {Path}; not retrying (attempt {Attempt}).",
                        (int)response.StatusCode, request.RequestUri?.AbsolutePath, attempt + 1);
                    return response;
                }
                _log.LogInformation("TripIt answered {Status} for {Path}; retrying in {Wait}.",
                    (int)response.StatusCode, request.RequestUri?.AbsolutePath, wait);
                response.Dispose();
                await _pacer.DelayAsync(wait, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.InFlight.Release();
        }
    }

    private static bool IsThrottled(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
