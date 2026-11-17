using System.Text.Json;

namespace TripIt;

/// <summary>
/// Short, safe descriptions of failures for tool results. They name what went wrong without
/// echoing TripIt's response bodies (only its validation message, when it is short plain
/// text) and never contain session values. Full detail belongs in Debug logs.
/// </summary>
public static class TripItErrorText
{
    /// <summary>TripIt's own message is shown only up to this length.</summary>
    public const int MaxTripItMessageLength = 200;

    /// <summary>
    /// A one-line description of an expected failure, or null when <paramref name="ex"/> is
    /// not one (callers then report a generic failure and log the exception).
    /// </summary>
    public static string? ForTool(Exception ex) => ex switch
    {
        TripItApiException api => Api(api),
        // Written by this library; names the remedy and carries no values.
        TripItAuthenticationException auth => auth.Message,
        TripItPartialWriteException partial => partial.Message,
        TripItSessionFormatException format => format.Message,
        // Argument errors are this library's and the tools' own validation messages.
        ArgumentException arg => arg.Message,
        JsonException => "TripIt's response was not the JSON that was expected.",
        HttpRequestException => "TripIt could not be reached (network error).",
        TimeoutException or TaskCanceledException => "TripIt did not answer in time.",
        _ => null,
    };

    /// <summary>Like <see cref="ForTool"/>, falling back to the exception type name.</summary>
    public static string Describe(Exception ex) => ForTool(ex) ?? ex.GetType().Name;

    private static string Api(TripItApiException ex)
    {
        if (Clean(ex.TripItMessage) is { } message)
        {
            return $"TripIt rejected the request (HTTP {ex.StatusCode}): {message}";
        }
        // A 2xx failure is this library's own finding (e.g. "Response contained no Trip.").
        return ex.StatusCode is >= 200 and <= 299 && Clean(ex.Detail) is { } own
            ? $"TripIt's response was not usable: {own}"
            : $"TripIt could not complete the request (HTTP {ex.StatusCode}).";
    }

    /// <summary>Keeps a message only when it is short, single-line plain text.</summary>
    private static string? Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var s = message.Trim();
        return s.Length > MaxTripItMessageLength || s.Contains('<') || s.Contains('>') || s.Any(char.IsControl)
            ? null
            : s;
    }
}
