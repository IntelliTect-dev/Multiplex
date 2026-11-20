namespace Multiplex.Sites.GoogleFlights;

/// <summary>Base of every error the Google Flights client raises about Google's answers.</summary>
public class GoogleFlightsException : Exception
{
    public GoogleFlightsException(string message) : base(message) { }

    public GoogleFlightsException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Why Google, or the client on Google's behalf, is not answering right now.</summary>
public enum ThrottleReason
{
    /// <summary>Google answered with a refusal (gRPC 13). It does not say why; it is treated as "stop and back off".</summary>
    Refused,

    /// <summary>Google answered HTTP 429 Too Many Requests.</summary>
    TooManyRequests,

    /// <summary>Google sent its "unusual traffic" check. A person must clear it in a browser; the client never does.</summary>
    Challenge,

    /// <summary>
    /// The client did not send the request: an earlier refusal started a cool-down that has not ended, so asking
    /// again now could only deepen a block on this network.
    /// </summary>
    CoolingDown,

    /// <summary>The client did not send the request: too many were already waiting for their turn under its own pacing.</summary>
    Paced,
}

/// <summary>
/// Google is refusing or throttling this network, or the client is cooling down after it did. Never retried
/// straight away: a block applies to the whole network and has lasted hours.
/// </summary>
public sealed class GoogleFlightsThrottledException(string message, ThrottleReason reason, DateTimeOffset retryAfter)
    : GoogleFlightsException(message)
{
    public ThrottleReason Reason { get; } = reason;

    /// <summary>The earliest time the client will send another request to Google.</summary>
    public DateTimeOffset RetryAfter { get; } = retryAfter;
}

/// <summary>Google could not answer for a reason that may pass (a server error, a timeout, gRPC 14).</summary>
public sealed class GoogleFlightsTransientException(string message, Exception? inner = null)
    : GoogleFlightsException(message, inner);

/// <summary>Google rejected the request itself (gRPC 3, for example an unknown airport or a date it will not search).</summary>
public sealed class GoogleFlightsRequestException(string message) : GoogleFlightsException(message);

/// <summary>
/// Google's answer was not in the shape the client knows. Google has most likely changed its payloads; the
/// field maps in <c>docs/protocol.md</c> need re-deriving from a fresh capture.
/// </summary>
public sealed class GoogleFlightsFormatException(string message) : GoogleFlightsException(message);
