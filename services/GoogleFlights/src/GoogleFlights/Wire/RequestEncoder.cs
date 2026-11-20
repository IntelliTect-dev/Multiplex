using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>
/// Builds the positional requests Google Flights' service RPCs take (protocol.md sections 4 and 5) from a
/// normalized <see cref="FlightSearchRequest"/>. Everything Google-shaped about a request lives here, so a
/// change to Google's payloads is a change to this file and its tests.
/// </summary>
internal static class RequestEncoder
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        // JSON.stringify's escaping: tokens are base64 with '+', '/' and '='; Google's own page sends them unescaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The language the client asks Google to answer in. Parsing relies on nothing language-specific.</summary>
    public const string Language = "en-US";

    /// <summary>
    /// The value sent as UTC offset in the <c>x-goog-ext-259736195-jspb</c> header: the value every recorded
    /// request carried. It is a constant, not the machine's offset; its effect on answers is not established.
    /// </summary>
    private const int UtcOffsetMinutes = 480;

    /// <summary><c>x-goog-ext-259736195-jspb</c>: language, region, currency (protocol.md section 2).</summary>
    public static string JspbHeader(string currency, string region) =>
        Serialize(new JsonArray(Language, region, currency, 2, null, new JsonArray(UtcOffsetMinutes), null, null, 7, new JsonArray()));

    /// <summary>The search filter: identical in every service RPC (protocol.md section 4).</summary>
    public static JsonArray Filter(FlightSearchRequest request, IReadOnlyList<IReadOnlyList<ChosenSegment>>? chosen = null)
    {
        var p = request.Passengers;
        var filter = new JsonArray(
            null, null, (int)request.TripType, null, new JsonArray(), (int)request.Cabin,
            new JsonArray(p.Adults, p.Children, p.InfantsOnLap, p.InfantsInSeat),
            request.MaxPrice is { } max ? new JsonArray(null, max) : null,
            null, null, null, null, null,
            new JsonArray([.. request.Legs.Select((leg, i) => Leg(leg, chosen is not null && i < chosen.Count ? chosen[i] : null))]),
            null, null, null, 1);
        if (request.ExcludeBasicEconomy)
        {
            while (filter.Count < 28)
                filter.Add(null);
            filter.Add(1);
        }
        return filter;
    }

    /// <summary><c>GetShoppingResults</c>: a fresh search (<c>[]</c>), or the next leg after a choice (<c>[null, token]</c>).</summary>
    public static string ShoppingResults(FlightSearchRequest request, FlightSelection? selection = null) =>
        Serialize(new JsonArray(
            selection is null ? new JsonArray() : new JsonArray(null, selection.Token),
            Filter(request, selection?.ChosenLegs),
            (int)request.Sort, 0, 0, 1));

    /// <summary><c>GetBookingResults</c> for a selection with every leg chosen.</summary>
    public static string BookingResults(FlightSelection selection) =>
        Serialize(new JsonArray(new JsonArray(null, selection.Token), Filter(selection.Request, selection.ChosenLegs), null, 0));

    /// <summary><c>GetCalendarGraph</c>: one price per departure date, for a fixed trip length on a round trip.</summary>
    public static string CalendarGraph(FlightSearchRequest request, DateOnly from, DateOnly to, int? tripDays) =>
        Serialize(new JsonArray(
            null, Filter(request), new JsonArray(Date(from), Date(to)), null,
            tripDays is { } days ? new JsonArray(days, days) : null));

    /// <summary><c>GetCalendarGrid</c>: departure dates by return dates, for a round trip.</summary>
    public static string CalendarGrid(FlightSearchRequest request, DateOnly departFrom, DateOnly departTo, DateOnly returnFrom, DateOnly returnTo) =>
        Serialize(new JsonArray(
            null, Filter(request),
            new JsonArray(Date(departFrom), Date(departTo)),
            new JsonArray(Date(returnFrom), Date(returnTo))));

    /// <summary><c>H028ib</c> airport autocomplete, as the page sends it for an origin.</summary>
    public static string Autocomplete(string text) =>
        Serialize(new JsonArray(text, new JsonArray(1, 2, 3, 5), null, new JsonArray(2), 1));

    /// <summary>The form body of a service RPC: <c>f.req=[null, "&lt;inner&gt;"]</c>.</summary>
    public static string ServiceBody(string inner) =>
        "f.req=" + Uri.EscapeDataString(Serialize(new JsonArray(null, inner)));

    /// <summary>The form body of a batchexecute RPC: <c>f.req=[[[rpcId, "&lt;inner&gt;", null, "generic"]]]</c>.</summary>
    public static string BatchBody(string rpcId, string inner) =>
        "f.req=" + Uri.EscapeDataString(Serialize(new JsonArray(new JsonArray(new JsonArray(rpcId, inner, null, "generic"))))) + "&";

    internal static string Serialize(JsonNode node) => node.ToJsonString(Compact);

    private static JsonArray Leg(FlightLeg leg, IReadOnlyList<ChosenSegment>? chosen)
    {
        var f = leg.Filters;
        JsonArray? times = null;
        if (f.DepartureHours is not null || f.ArrivalHours is not null)
        {
            // Google sends the slider's "to" ends one lower: the whole day is [0, 23].
            var d = f.DepartureHours ?? HourRange.AllDay;
            var a = f.ArrivalHours ?? HourRange.AllDay;
            times = new JsonArray(d.From, d.To - 1, a.From, a.To - 1);
        }

        var carriers = f.Airlines.Concat(f.Alliances.Select(AllianceName)).ToArray();
        return new JsonArray(
            Places(leg.Origin), Places(leg.Destination),
            times,
            (int)f.Stops,
            carriers.Length == 0 ? null : new JsonArray([.. carriers.Select(c => (JsonNode)c)]),
            null,
            Date(leg.Date),
            f.MaxDuration is { } max ? new JsonArray((int)max.TotalMinutes) : null,
            chosen is null ? null : new JsonArray([.. chosen.Select(Chosen)]),
            null, null, null, null, null,
            3);
    }

    private static JsonArray Places(string airport) => new(new JsonArray(new JsonArray(airport, 0)));

    private static JsonNode Chosen(ChosenSegment s) =>
        new JsonArray(s.From, Date(s.Date), s.To, null, s.Carrier, s.FlightNumber);

    internal static string AllianceName(Alliance alliance) => alliance switch
    {
        Alliance.Oneworld => "ONEWORLD",
        Alliance.SkyTeam => "SKYTEAM",
        Alliance.StarAlliance => "STAR_ALLIANCE",
        _ => throw new ArgumentOutOfRangeException(nameof(alliance)),
    };

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
