using System.Text.Json;

namespace Multiplex.Sites.GoogleFlights.Wire;

/// <summary>What a <c>GetShoppingResults</c> answer holds, before the client adds its own metadata.</summary>
internal sealed record ParsedFlights(IReadOnlyList<FlightOption> Best, IReadOnlyList<FlightOption> Other, IReadOnlyList<AirlineName> Airlines);

/// <summary>
/// Reads Google Flights' positional answers into the client's models, by the field maps in protocol.md
/// section 6. Every position used here is one the field maps mark as established.
/// </summary>
internal static class ResponseParser
{
    /// <summary>
    /// Flights for one leg. <paramref name="selection"/> is what was chosen before this leg (<c>null</c> on a
    /// fresh search); each option's own selection adds its flights to it.
    /// </summary>
    public static ParsedFlights Flights(RpcFrames frames, FlightSearchRequest request, FlightSelection? selection)
    {
        if (frames.Payloads.Count == 0)
            throw new GoogleFlightsFormatException("Google's flight search answer held no data.");

        // Several payloads stream in; flights come from the last one that has index 2 or 3.
        var payload = frames.LastWith(2) ?? frames.LastWith(3);
        var chosen = selection?.ChosenLegs ?? [];
        var isLastLeg = chosen.Count == request.Legs.Count - 1;

        var listed = 0;
        List<FlightOption> Read(int index)
        {
            var options = new List<FlightOption>();
            foreach (var itinerary in payload.Items(index, 0))
            {
                listed++;
                if (Option(itinerary, request, chosen, isLastLeg) is { } option)
                    options.Add(option);
            }
            return options;
        }

        var best = Read(2);
        var other = Read(3);
        if (listed > 0 && best.Count + other.Count == 0)
            throw new GoogleFlightsFormatException($"Google listed {listed} flights, but none was in the shape the client knows.");

        var airlines = (frames.LastWith(7) ?? payload).Items(7, 1, 1)
            .Select(a => a.Str(0) is { } code && a.Str(1) is { } name ? new AirlineName(code, name) : null)
            .OfType<AirlineName>()
            .ToArray();
        return new ParsedFlights(best, other, airlines);
    }

    public static IReadOnlyList<BookingOption> Booking(RpcFrames frames, FlightSearchRequest request)
    {
        if (frames.Last is not { } payload)
            throw new GoogleFlightsFormatException("Google's booking answer held no data.");

        var options = new List<BookingOption>();
        foreach (var o in payload.Items(1, 0))
        {
            var sellers = o.Items(1)
                .Select(s => s.Str(0) is { } code ? new BookingSeller(code, s.Str(1) ?? code, s.Bool(3) ?? false) : null)
                .OfType<BookingSeller>()
                .ToArray();
            var flights = o.Items(3).Select(f => $"{f.Str(0)}{f.Str(1)}").Where(f => f.Length > 0).ToArray();
            options.Add(new BookingOption(
                sellers,
                flights,
                Price(o.At(7), request.Currency!),
                o.Str(21, 3),
                o.Str(21, 0, 1),
                o.Str(5, 0)));
        }
        if (options.Count == 0 && payload.At(1) is null)
            throw new GoogleFlightsFormatException("Google's booking answer was not in the shape the client knows.");
        return options;
    }

    public static IReadOnlyList<CalendarPrice> Calendar(RpcFrames frames, FlightSearchRequest request)
    {
        if (frames.Last is not { } payload)
            throw new GoogleFlightsFormatException("Google's calendar answer held no data.");

        var prices = new List<CalendarPrice>();
        foreach (var cell in payload.Items(1))
        {
            if (!DateOnly.TryParse(cell.Str(0), System.Globalization.CultureInfo.InvariantCulture, out var departure))
                continue;
            DateOnly? @return = DateOnly.TryParse(cell.Str(1), System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null;
            if (Price(cell.At(2), request.Currency!) is { } price)
                prices.Add(new CalendarPrice(departure, @return, price));
        }
        return prices;
    }

    public static IReadOnlyList<PlaceMatch> Places(RpcFrames frames)
    {
        if (frames.Last is not { } payload)
            throw new GoogleFlightsFormatException("Google's airport lookup answer held no data.");

        var places = new List<PlaceMatch>();
        foreach (var match in payload.Items(0))
        {
            var place = match.At(0);
            var airports = match.Items(1).Select(a => Airport(a.At(0), a.Str(1))).OfType<Airport>().ToList();
            // A match that is itself an airport (kind 1) carries its own code and no nested list.
            if (airports.Count == 0 && Airport(place, null) is { } self)
                airports.Add(self);
            if (place.Str(1) is { } name)
                places.Add(new PlaceMatch(name, place.Str(3), airports));
        }
        return places;
    }

    private static Airport? Airport(JsonElement? a, string? distance)
    {
        var kind = a.Int(0);
        if (kind is not (1 or 5) || a.Str(5) is not { Length: 3 } code || a.Str(1) is not { } name)
            return null;
        return new Airport(code, name, a.Str(2), distance, IsAirport: kind == 1);
    }

    private static FlightOption? Option(JsonElement itinerary, FlightSearchRequest request, IReadOnlyList<IReadOnlyList<ChosenSegment>> chosen, bool isLastLeg)
    {
        var summary = itinerary.At(0);
        var token = itinerary.Str(1, 1);
        if (summary is not { } trip || token is null)
            return null;

        var times = Tokens.SegmentTimes(itinerary.Str(8));
        var segments = new List<FlightSegment>();
        foreach (var (s, i) in trip.Items(2).Select((s, i) => (s, i)))
        {
            if (Segment(s, times.Count == trip.Items(2).Count() ? times[i] : null) is not { } segment)
                return null;
            segments.Add(segment);
        }
        if (segments.Count == 0)
            return null;

        var layovers = trip.Items(13)
            .Select(l => l.Int(0) is { } minutes && l.Str(1) is { } arrive && l.Str(2) is { } depart
                ? new Layover(TimeSpan.FromMinutes(minutes), arrive, depart, l.Str(4), l.Str(5))
                : null)
            .OfType<Layover>()
            .ToArray();
        var airlines = trip.Items(1).Select(n => n.GetString()).OfType<string>().ToArray();
        var duration = trip.Int(9) is { } total
            ? TimeSpan.FromMinutes(total)
            : segments[^1].ArrivesLocal - segments[0].DepartsLocal;

        var thisLeg = segments.Select(s => new ChosenSegment(s.From, DateOnly.FromDateTime(s.DepartsLocal), s.To, s.Carrier, s.FlightNumber)).ToArray();
        var selection = new FlightSelection(request, [.. chosen, thisLeg], token);
        return new FlightOption(
            segments,
            layovers,
            duration,
            airlines,
            Price(itinerary.At(1), request.Currency!),
            isLastLeg && request.Legs.Count > 1 ? itinerary.Bool(6) : null,
            selection);
    }

    private static FlightSegment? Segment(JsonElement s, SegmentTimes? times)
    {
        if (s.Str(3) is not { } from || s.Str(6) is not { } to
            || s.Date(20) is not { } departDate || s.Date(21) is not { } arriveDate
            || s.Time(8) is not { } departTime || s.Time(10) is not { } arriveTime
            || s.Str(22, 0) is not { } carrier || s.Str(22, 1) is not { } number)
        {
            return null;
        }
        var operatedBy = times?.OperatingCarrier is { Length: > 0 } op && op != carrier ? op : null;
        return new FlightSegment(
            from, s.Str(4), to, s.Str(5),
            departDate.ToDateTime(departTime), arriveDate.ToDateTime(arriveTime),
            times?.Departs, times?.Arrives,
            TimeSpan.FromMinutes(s.Int(11) ?? 0),
            carrier, number, s.Str(22, 3), operatedBy, s.Str(17), s.Str(14));
    }

    /// <summary>
    /// A <c>[[null, rounded price], token]</c> pair: the exact price from the token when it carries one,
    /// otherwise the rounded price in the request's currency.
    /// </summary>
    private static Money? Price(JsonElement? pair, string currency) =>
        Tokens.Price(pair.Str(1)) ?? (pair.Decimal(0, 1) is { } rounded ? new Money(rounded, currency) : null);

    private static decimal? Decimal(this JsonElement? e, params ReadOnlySpan<int> path) =>
        e is { } element ? element.Decimal(path) : null;

    private static bool? Bool(this JsonElement? e, params ReadOnlySpan<int> path) =>
        e is { } element ? element.Bool(path) : null;
}
