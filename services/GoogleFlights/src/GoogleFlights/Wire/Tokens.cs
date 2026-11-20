using System.Globalization;
using System.Text.Json;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// What the client reads from Google's opaque base64 protobuf values (protocol.md section 6). The tokens
/// themselves are passed back to Google unchanged; these readers only take the exact price and the UTC
/// offsets out of them.
/// </summary>
internal static class Tokens
{
    /// <summary>
    /// The exact price in a selection, booking or calendar token: field 3 is
    /// <c>{1: amount in minor units, 2: decimals, 3: currency}</c>.
    /// </summary>
    public static Money? Price(string? token)
    {
        if (!ProtoMessage.TryParseBase64(token, out var message))
            return null;
        var price = message.Message(3);
        if (price?.Varint(1) is not { } minor || price.String(3) is not { Length: 3 } currency)
            return null;
        var decimals = (int)(price.Varint(2) ?? 0);
        if (decimals > 6)
            return null;
        return new Money((decimal)minor / Pow10(decimals), currency);
    }

    /// <summary>The flight key in a selection token's field 2, e.g. <c>DL1358</c> (or <c>DL1731#1</c>).</summary>
    public static string? FlightKey(string? token) =>
        ProtoMessage.TryParseBase64(token, out var message) ? message.String(2) : null;

    /// <summary>
    /// The exact times of an itinerary's segments, from <c>itinerary[8]</c>: a JSON string holding a one-item
    /// array with a base64 protobuf whose field 4.1.1 repeats once per segment (1 from, 2 departure with UTC
    /// offset, 3 to, 4 arrival with UTC offset, 7/8 operating carrier and number, 10 aircraft code).
    /// </summary>
    public static IReadOnlyList<SegmentTimes> SegmentTimes(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return [];
        string? base64;
        try
        {
            using var doc = JsonDocument.Parse(detail);
            base64 = doc.RootElement.Str(0);
        }
        catch (JsonException)
        {
            return [];
        }
        if (!ProtoMessage.TryParseBase64(base64, out var message))
            return [];

        var segments = new List<SegmentTimes>();
        foreach (var segment in message.Message(4)?.Message(1)?.Messages(1) ?? [])
        {
            if (!TryOffset(segment.String(2), out var departs) || !TryOffset(segment.String(4), out var arrives))
                return [];
            segments.Add(new SegmentTimes(segment.String(1), departs, segment.String(3), arrives,
                segment.String(7), segment.String(8), segment.String(10)));
        }
        return segments;
    }

    private static bool TryOffset(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

    private static decimal Pow10(int n)
    {
        decimal result = 1;
        for (var i = 0; i < n; i++)
            result *= 10;
        return result;
    }
}

/// <summary>One segment's times with their UTC offsets, and its operating flight.</summary>
internal sealed record SegmentTimes(
    string? From,
    DateTimeOffset Departs,
    string? To,
    DateTimeOffset Arrives,
    string? OperatingCarrier,
    string? OperatingNumber,
    string? AircraftCode);
