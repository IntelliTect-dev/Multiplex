namespace Multiplex.Web.HtmlSites;

/// <summary>
/// A pause for a whole service after its website refuses it: the first block pauses every
/// request for <c>first</c>, each later one doubles that up to <c>longest</c>, and a success
/// starts over. It lives in the process, not in any MCP session.
/// </summary>
public sealed class CircuitBreaker(TimeProvider time, TimeSpan first, TimeSpan longest)
{
    private readonly object _lock = new();
    private TimeSpan _next = first;
    private DateTimeOffset? _until;
    private string? _reason;

    /// <summary>The pause in force, or null.</summary>
    public (DateTimeOffset Until, string Reason)? Pause
    {
        get
        {
            lock (_lock)
            {
                return _until is { } until && until > time.GetUtcNow() ? (until, _reason!) : null;
            }
        }
    }

    /// <summary>Starts a pause and returns when it ends. <paramref name="reason"/> finishes "it ...".</summary>
    public DateTimeOffset Trip(string reason)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            _until = now + _next;
            _reason = $"{reason} at {now:HH:mm} UTC";
            _next = _next * 2 > longest ? longest : _next * 2;
            return _until.Value;
        }
    }

    public void Succeeded()
    {
        lock (_lock)
        {
            _next = first;
            _until = null;
        }
    }
}
