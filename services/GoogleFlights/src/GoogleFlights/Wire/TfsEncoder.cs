using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// Encodes the <c>tfs</c> URL parameter (base64url protobuf) from a request filter, by the field map in
/// protocol.md section 7. Proven both ways: it reproduces the <c>tfs</c> the page produced for every
/// recorded filter state, and Google answered search URLs it built. The client uses it only for links a
/// person can open; it never loads those pages itself.
/// </summary>
internal static class TfsEncoder
{
    /// <summary>The <c>tfs</c> for a filter, as on a search or booking page (field 2 = 2).</summary>
    public static string Encode(JsonArray filter) => Encode(JsonDocument.Parse(filter.ToJsonString()).RootElement);

    public static string Encode(JsonElement filter, int page = 2)
    {
        var tfs = new ProtoWriter().Varint(1, 28).Varint(2, page);
        foreach (var leg in filter.Items(13))
            tfs.Message(3, Leg(leg));

        // tfs lists travelers adult, child, infant in seat, infant on lap: not the payload's order.
        var pax = filter.At(6);
        foreach (var (index, code) in new[] { (0, 1), (1, 2), (3, 4), (2, 3) })
        {
            for (var i = 0; i < (pax.Int(index) ?? (index == 0 ? 1 : 0)); i++)
                tfs.Varint(8, code);
        }
        tfs.Varint(9, filter.Int(5) ?? 1);
        if (filter.Int(7, 1) is { } maxPrice)
            tfs.Varint(12, maxPrice);
        tfs.Varint(14, 1).Message(16, new ProtoWriter().Varint(1, -1)).Varint(19, filter.Int(2) ?? 1);
        if (filter.Int(28) is { } excludeBasic)
            tfs.Varint(25, excludeBasic);
        return tfs.ToBase64Url();
    }

    /// <summary>The <c>tfu</c> value for a sort order other than Google's default.</summary>
    public static string? SortParameter(FlightSort sort) =>
        sort == FlightSort.Best
            ? null
            : new ProtoWriter().Message(2, new ProtoWriter().Varint(1, (int)sort).Varint(2, 0).Varint(3, 0)).ToBase64Url();

    /// <summary>A Google Flights page URL for a request: the search page, or the booking page once every leg is chosen.</summary>
    public static Uri PageUrl(FlightSearchRequest request, JsonArray filter, bool booking)
    {
        var query = $"tfs={Encode(filter)}";
        if (!booking && SortParameter(request.Sort) is { } tfu)
            query += $"&tfu={tfu}";
        query += string.Create(CultureInfo.InvariantCulture, $"&hl={RequestEncoder.Language}&gl={request.Region}&curr={request.Currency}");
        return new Uri($"https://www.google.com/travel/flights/{(booking ? "booking" : "search")}?{query}");
    }

    private static ProtoWriter Leg(JsonElement leg)
    {
        var l = new ProtoWriter();
        if (leg.Str(6) is { } date)
            l.String(2, date);
        foreach (var f in leg.Items(8))
        {
            l.Message(4, new ProtoWriter()
                .String(1, f.Str(0) ?? "").String(2, f.Str(1) ?? "").String(3, f.Str(2) ?? "")
                .String(5, f.Str(4) ?? "").String(6, f.Str(5) ?? ""));
        }
        // Payload stops 1 (nonstop) -> "at most 0 stops"; 2 and 3 are the expected values for 1 and 2 stops (pending).
        if (leg.Int(3) is { } stops and > 0)
            l.Varint(5, stops - 1);
        foreach (var carrier in leg.Items(4))
            l.String(6, carrier.GetString() ?? "");
        var hour = 8;
        foreach (var h in leg.Items(2))
        {
            if (h.ValueKind == JsonValueKind.Number)
                l.Varint(hour, h.GetInt32());
            hour++;
        }
        if (leg.Int(7, 0) is { } maxMinutes)
            l.Varint(12, maxMinutes);
        foreach (var o in leg.Items(0).SelectMany(p => p.EnumerateArray()))
            l.Message(13, new ProtoWriter().Varint(1, 1).String(2, o.Str(0) ?? ""));
        foreach (var d in leg.Items(1).SelectMany(p => p.EnumerateArray()))
            l.Message(14, new ProtoWriter().Varint(1, 1).String(2, d.Str(0) ?? ""));
        return l;
    }
}
