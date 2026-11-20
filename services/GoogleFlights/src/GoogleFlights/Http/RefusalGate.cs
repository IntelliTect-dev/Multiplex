namespace Multiplex.Sites.GoogleFlights.Http;

/// <summary>
/// Stops the client from asking Google again while it is refusing. A refusal (gRPC 13, HTTP 429 or the
/// "unusual traffic" check) starts a cool-down; until it ends, calls fail at once without reaching Google.
/// Each refusal in a row doubles the cool-down, up to a ceiling; an answer resets it. Refusals are never
/// retried: Google does not say why it refused, and a block applies to the whole network for hours.
/// </summary>
internal sealed class RefusalGate(TimeSpan initial, TimeSpan max, TimeProvider time)
{
    private readonly Lock _lock = new();
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private int _inARow;

    /// <exception cref="GoogleFlightsThrottledException">A cool-down is in force.</exception>
    public void ThrowIfCoolingDown()
    {
        DateTimeOffset until;
        lock (_lock)
            until = _until;
        if (time.GetUtcNow() < until)
        {
            throw new GoogleFlightsThrottledException(
                $"Not asking Google Flights again until {until:u}: it refused a request, and asking again sooner could get this network blocked for hours.",
                ThrottleReason.CoolingDown, until);
        }
    }

    /// <summary>Records a refusal and returns when the client may ask again.</summary>
    public DateTimeOffset Refused(DateTimeOffset? notBefore = null)
    {
        lock (_lock)
        {
            _inARow++;
            var cooldown = TimeSpan.FromTicks(Math.Min(max.Ticks, initial.Ticks * (1L << Math.Min(_inARow - 1, 20))));
            var until = time.GetUtcNow() + cooldown;
            if (notBefore > until)
                until = notBefore.Value;
            _until = until > _until ? until : _until;
            return _until;
        }
    }

    public void Answered()
    {
        lock (_lock)
            _inARow = 0;
    }
}
