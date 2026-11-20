using System.Text.Json;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>
/// Parsing recorded answers into the client's models. Expected values are what the same answers showed
/// (the direct flow's own output, and the page as recorded in captures D and J).
/// </summary>
public class ResponseParserTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);
    private static readonly DateOnly Dec22 = new(2026, 12, 22);

    private static FlightSearchRequest SeaDenRoundTrip =>
        FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, Dec22).Normalize("USD", "US");

    private static RpcFrames Frames(string reference, int entry) =>
        ResponseFrames.Parse(HarFixture.Load(reference)[entry].ResponseText);

    [Fact]
    public void A_round_trip_search_lists_the_outbound_flights_with_exact_prices_and_times()
    {
        var parsed = ResponseParser.Flights(Frames("direct-http-flow-2026-11-20", 0), SeaDenRoundTrip, selection: null);

        Assert.Equal(13, parsed.Best.Count + parsed.Other.Count); // F1: "flights":13
        var first = parsed.Best[0];
        var segment = Assert.Single(first.Segments);
        Assert.Equal("AS682", segment.Flight);
        Assert.Equal(("SEA", "DEN"), (first.Origin, first.Destination));
        Assert.Equal(new DateTime(2026, 12, 15, 7, 25, 0), first.DepartsLocal);
        Assert.Equal(new DateTime(2026, 12, 15, 11, 10, 0), first.ArrivesLocal);
        Assert.Equal(new DateTimeOffset(2026, 12, 15, 7, 25, 0, TimeSpan.FromHours(-8)), segment.Departs);
        Assert.Equal(TimeSpan.FromMinutes(165), first.Duration);
        Assert.Equal(0, first.Stops);
        Assert.Equal("Boeing 737", segment.Aircraft);
        Assert.Equal(new Money(275.00m, "USD"), first.Price);
        // An outbound's "Separate tickets" flag is not that label: not exposed before the last leg.
        Assert.Null(first.SeparateTickets);
    }

    [Fact]
    public void Each_outbound_carries_the_selection_that_choosing_it_makes()
    {
        var parsed = ResponseParser.Flights(Frames("direct-http-flow-2026-11-20", 0), SeaDenRoundTrip, selection: null);
        var delta = parsed.Best.Concat(parsed.Other).First(o => o.Flights.SequenceEqual(["DL1358"]));

        Assert.False(delta.Selection.IsComplete);
        Assert.Equal(1, delta.Selection.NextLeg);
        Assert.Equal([new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")], delta.Selection.ChosenLegs[0]);
        Assert.Equal("DL1358", Tokens.FlightKey(delta.Selection.Token));

        // The selection is exactly what F2 sent Google for the return flights.
        Assert.Equal(HarFixture.Load("direct-http-flow-2026-11-20")[1].InnerRequest,
            RequestEncoder.ShoppingResults(SeaDenRoundTrip, delta.Selection));
    }

    [Fact]
    public void The_return_list_marks_separate_tickets_and_completes_the_selection()
    {
        var outbound = new FlightSelection(SeaDenRoundTrip, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")]], "token");

        var parsed = ResponseParser.Flights(Frames("direct-http-flow-2026-11-20", 1), SeaDenRoundTrip, outbound);
        var all = parsed.Best.Concat(parsed.Other).ToArray();

        Assert.Equal(17, all.Length); // F2: "flights":17
        Assert.All(all, o => Assert.True(o.Selection.IsComplete));
        Assert.All(all, o => Assert.Equal(outbound.ChosenLegs[0], o.Selection.ChosenLegs[0]));
        // Capture D: after a Delta outbound, Delta returns are one ticket and every other airline's are separate.
        Assert.All(all.Where(o => o.Segments.All(s => s.Carrier == "DL")), o => Assert.False(o.SeparateTickets));
        Assert.Contains(all, o => o.SeparateTickets == true && o.Segments.Any(s => s.Carrier != "DL"));
    }

    [Fact]
    public void A_connection_lists_its_layover_and_the_next_day_arrival()
    {
        // Capture D's return list: F9777 + F94203, 14 h 27 min at Las Vegas, arriving the next day.
        var request = SeaDenRoundTrip;
        var outbound = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "2759")]], "token");

        var parsed = ResponseParser.Flights(Frames("round-trip-to-booking-2026-11-19", 1), request, outbound);
        var frontier = parsed.Best.Concat(parsed.Other).First(o => o.Flights.SequenceEqual(["F9777", "F94203"]));

        Assert.Equal(1, frontier.Stops);
        var layover = Assert.Single(frontier.Layovers);
        Assert.Equal(TimeSpan.FromMinutes(867), layover.Duration);
        Assert.Equal("LAS", layover.ArrivalAirport);
        Assert.False(layover.ChangesAirport);
        Assert.Equal(frontier.DepartsLocal.Date.AddDays(1), frontier.ArrivesLocal.Date);
    }

    [Fact]
    public void A_one_way_search_lists_complete_selections()
    {
        var request = FlightSearchRequest.OneWay("SEA", "SFO", Dec15).Normalize("USD", "US");

        var parsed = ResponseParser.Flights(Frames("one-way-search-2026-11-18", 0), request, selection: null);
        var all = parsed.Best.Concat(parsed.Other).ToArray();

        Assert.NotEmpty(all);
        Assert.All(all, o => Assert.True(o.Selection.IsComplete));
        Assert.All(all, o => Assert.Null(o.SeparateTickets));
        Assert.Contains(parsed.Airlines, a => a.Code == "AS" && a.Name == "Alaska");
    }

    [Fact]
    public void Booking_options_list_each_fare_with_its_seller_and_exact_price()
    {
        var options = ResponseParser.Booking(Frames("direct-http-flow-2026-11-20", 2), SeaDenRoundTrip);

        Assert.Equal(7, options.Count); // F3: "options":7
        var basic = options[0];
        Assert.Equal("Delta Main Basic", basic.FareName);
        Assert.Equal("DELTA MAIN BASIC", basic.FareCode);
        Assert.Equal(["DL1358", "DL1731"], basic.Flights);
        Assert.Equal(new BookingSeller("DL", "Delta", IsAirline: true), Assert.Single(basic.Sellers));
        Assert.Equal("www.delta.com/...", basic.SellerSite);
        Assert.Equal("USD", basic.Price!.Currency);
        Assert.Equal(297m, Math.Round(basic.Price.Amount));
        Assert.Contains(options, o => o.FareName == "Delta First Classic");
    }

    [Fact]
    public void The_price_graph_has_one_price_per_departure_date()
    {
        var prices = ResponseParser.Calendar(Frames("direct-http-flow-2026-11-20", 3), SeaDenRoundTrip);

        Assert.Equal(11, prices.Count); // F4: "cells":11, 2026-12-10 to 2026-12-20
        Assert.Equal(new CalendarPrice(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 17), new Money(285.00m, "USD")), prices[0]);
        Assert.All(prices, p => Assert.Equal(7, p.Return!.Value.DayNumber - p.Departure.DayNumber));
    }

    [Fact]
    public void The_date_grid_prices_every_departure_and_return_pair()
    {
        var prices = ResponseParser.Calendar(Frames("calendar-grid-and-graph-2026-11-19", 0), SeaDenRoundTrip);

        Assert.Equal(49, prices.Count); // capture K: 7 departures x 7 returns
        Assert.Equal(7, prices.Select(p => p.Departure).Distinct().Count());
        Assert.Equal(7, prices.Select(p => p.Return).Distinct().Count());
    }

    [Fact]
    public void An_airport_lookup_lists_places_with_their_airports()
    {
        var places = ResponseParser.Places(Frames("airport-autocomplete-2026-11-19", 12)); // typed "Denv"

        Assert.Equal("Denver, Colorado", places[0].Name);
        Assert.Equal("City in Colorado", places[0].Description);
        var den = Assert.Single(places[0].Airports);
        Assert.Equal(new Airport("DEN", "Denver International Airport", "Denver", "19 mi", IsAirport: true), den);
    }

    [Fact]
    public void A_station_listed_beside_airports_is_marked_as_not_an_airport()
    {
        var places = ResponseParser.Places(Frames("airport-autocomplete-2026-11-19", 3)); // typed "Seat"

        var seaton = places.First(p => p.Name == "Seaton, United Kingdom");
        Assert.Contains(seaton.Airports, a => a is { Code: "BRS", IsAirport: true });
        Assert.Contains(seaton.Airports, a => a is { Code: "UBW", IsAirport: false });
    }

    [Fact]
    public void An_answer_with_no_data_is_a_format_error()
    {
        var empty = new RpcFrames([], []);

        Assert.Throws<GoogleFlightsFormatException>(() => ResponseParser.Flights(empty, SeaDenRoundTrip, null));
        Assert.Throws<GoogleFlightsFormatException>(() => ResponseParser.Booking(empty, SeaDenRoundTrip));
    }

    [Fact]
    public void Itineraries_in_an_unknown_shape_are_a_format_error_not_an_empty_list()
    {
        // Every itinerary present, but its summary moved: what a Google payload change looks like.
        var shifted = JsonDocument.Parse("""[null,null,[[[["moved"],[[null,100],"tok"]]]],null]""").RootElement;

        Assert.Throws<GoogleFlightsFormatException>(() =>
            ResponseParser.Flights(new RpcFrames([shifted], []), SeaDenRoundTrip, null));
    }
}
