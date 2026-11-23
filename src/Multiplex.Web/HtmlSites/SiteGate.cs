namespace Multiplex.Web.HtmlSites;

/// <summary>What a request through the gate fetches; it decides the wait before it.</summary>
public enum SiteRequestKind
{
    Page,
    Image,
}

public sealed class SiteGateOptions
{
    /// <summary>The site's name in messages, e.g. "Amazon".</summary>
    public string Site { get; init; } = "The site";

    /// <summary>The least wait after a page before the next page from another call or search.</summary>
    public TimeSpan PageSpacing { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan PageJitter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The least wait between consecutive pages of one sequence (one search).</summary>
    public TimeSpan SequenceSpacing { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SequenceJitter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The wait between an image and the request before or after it.</summary>
    public TimeSpan ImageSpacing { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest a request waits for its turn.</summary>
    public TimeSpan QueueLimit { get; init; } = TimeSpan.FromSeconds(30);

    public int HourlyCap { get; init; } = 100;
    public int DailyCap { get; init; } = 500;
}

/// <summary>
/// Paces every request one service sends to its website: one in flight for the whole host, a gap
/// between requests, a limit on how long a request queues, and hourly and daily caps. It never
/// retries; what to do with an answer is the caller's decision.
/// </summary>
public sealed class SiteGate
{
    private readonly SiteGateOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly Queue<DateTimeOffset> _sent = new();
    private DateTimeOffset? _lastPageDone;
    private DateTimeOffset? _lastAnyDone;
    private string? _lastSequence;
    private bool _lastWasPage;

    /// <param name="random">Source of jitter in [0, 1); tests pass a constant.</param>
    /// <param name="delay">How to wait; tests pass one that advances their clock.</param>
    public SiteGate(SiteGateOptions options, TimeProvider time, Func<double>? random = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _time = time;
        _random = random ?? Random.Shared.NextDouble;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
    }

    /// <summary>
    /// Runs <paramref name="send"/> when its turn comes. <paramref name="beforeSend"/> runs at the
    /// front of the queue, before the spacing wait: a caller re-checks there whatever may have
    /// changed while it queued, and throws to give the turn up.
    /// </summary>
    /// <param name="sequence">Names a run of pages a person would page through (one search); consecutive
    /// pages of one sequence wait less.</param>
    /// <param name="deadline">When the whole call must stop; the wait for a turn never runs past it.</param>
    /// <exception cref="SiteBusyException">The queue limit, the deadline or a volume cap stopped it.</exception>
    public async Task<T> RunAsync<T>(SiteRequestKind kind, string? sequence, DateTimeOffset? deadline,
        Action? beforeSend, Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        var limit = _options.QueueLimit;
        var deadlineBound = false;
        if (deadline is { } d && d - _time.GetUtcNow() < limit)
        {
            limit = d - _time.GetUtcNow();
            deadlineBound = true;
        }
        if (limit <= TimeSpan.Zero) throw DeadlineReached();

        await EnterAsync(limit, deadlineBound, ct).ConfigureAwait(false);
        try
        {
            beforeSend?.Invoke();
            ThrowIfOverCap();

            var wait = WaitBefore(kind, sequence);
            if (deadline is { } dl && _time.GetUtcNow() + wait >= dl) throw DeadlineReached();
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);

            _sent.Enqueue(_time.GetUtcNow());
            try
            {
                return await send(ct).ConfigureAwait(false);
            }
            finally
            {
                var now = _time.GetUtcNow();
                _lastAnyDone = now;
                _lastWasPage = kind == SiteRequestKind.Page;
                if (kind == SiteRequestKind.Page)
                {
                    _lastPageDone = now;
                    _lastSequence = sequence;
                }
            }
        }
        finally
        {
            _inFlight.Release();
        }
    }

    /// <summary>Waits the way the gate does, so a caller's own waits follow the same clock in tests.</summary>
    public Task DelayAsync(TimeSpan wait, CancellationToken ct) => _delay(wait, ct);

    private async Task EnterAsync(TimeSpan limit, bool deadlineBound, CancellationToken ct)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entering = _inFlight.WaitAsync(abandon.Token);
        try
        {
            await entering.WaitAsync(limit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            abandon.Cancel();
            // The turn may have arrived in the same instant; give it back rather than leak it.
            try { await entering.ConfigureAwait(false); _inFlight.Release(); }
            catch (OperationCanceledException) { }
            throw deadlineBound
                ? DeadlineReached()
                : new SiteBusyException($"{_options.Site} requests are queued; try again in a moment.");
        }
    }

    private TimeSpan WaitBefore(SiteRequestKind kind, string? sequence)
    {
        var now = _time.GetUtcNow();
        var due = now;
        if (kind == SiteRequestKind.Image)
        {
            if (_lastAnyDone is { } any) due = any + _options.ImageSpacing;
        }
        else
        {
            if (_lastPageDone is { } page)
            {
                // Only the page straight after another page of the same search counts as paging on.
                var sameSequence = _lastWasPage && sequence is not null && sequence == _lastSequence;
                var gap = sameSequence
                    ? _options.SequenceSpacing + _options.SequenceJitter * _random()
                    : _options.PageSpacing + _options.PageJitter * _random();
                due = page + gap;
            }
            if (_lastAnyDone is { } any && any + _options.ImageSpacing > due) due = any + _options.ImageSpacing;
        }
        return due > now ? due - now : TimeSpan.Zero;
    }

    private void ThrowIfOverCap()
    {
        var now = _time.GetUtcNow();
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromDays(1)) _sent.Dequeue();
        if (_sent.Count >= _options.DailyCap)
        {
            throw new SiteBusyException(
                $"{_options.Site} requests have reached today's limit of {_options.DailyCap}; try again after {_sent.Peek() + TimeSpan.FromDays(1):HH:mm} UTC.");
        }
        var lastHour = _sent.Where(t => now - t < TimeSpan.FromHours(1)).ToList();
        if (lastHour.Count >= _options.HourlyCap)
        {
            throw new SiteBusyException(
                $"{_options.Site} requests have reached this hour's limit of {_options.HourlyCap}; try again after {lastHour[0] + TimeSpan.FromHours(1):HH:mm} UTC.");
        }
    }

    private SiteBusyException DeadlineReached() =>
        new($"The call ran out of time before {_options.Site} could be asked again.");
}
