using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests.Live;

/// <summary>
/// The client against the real Google Flights. Assertions are on structure and meaning, never on prices,
/// which change by the minute. The suite spends about eight requests, paced by the client itself. Booking options,
/// price calendars and multi-city trips are not available over HTTPS and send nothing (protocol.md section 2).
/// </summary>
[Collection(LiveCollection.Name)]
public class GoogleFlightsLiveTests(LiveClientFixture live)
{
    private static readonly DateOnly Departure = LiveClientFixture.Departure;
    private static readonly DateOnly Return = Departure.AddDays(7);

    [LiveFact]
    public async Task A_one_way_search_lists_priced_flights_between_the_two_airports()
    {
        var result = await live.Client.SearchAsync(FlightSearchRequest.OneWay("SEA", "DEN", Departure));

        var flights = result.AllFlights.ToArray();
        Assert.NotEmpty(flights);
        Assert.All(flights, f =>
        {
            Assert.Equal("SEA", f.Origin);
            Assert.Equal("DEN", f.Destination);
            Assert.Equal(DateOnly.FromDateTime(f.DepartsLocal), Departure);
            Assert.True(f.Selection.IsComplete);
            Assert.True(f.Duration > TimeSpan.FromHours(1));
        });
        Assert.Contains(flights, f => f.Price is { Currency: "USD", Amount: > 0 });
        Assert.False(result.FromCache);
    }

    [LiveFact]
    public async Task A_round_trip_goes_from_outbound_to_returns_and_booking_gives_the_link()
    {
        var outbound = await live.Client.SearchAsync(FlightSearchRequest.RoundTrip("SEA", "DEN", Departure, Return));
        var first = outbound.AllFlights.First(f => f.Segments.Count == 1);
        Assert.False(first.Selection.IsComplete);

        var returns = await live.Client.GetReturnFlightsAsync(first.Selection);
        Assert.Equal(1, returns.Leg);
        Assert.NotEmpty(returns.AllFlights);
        Assert.All(returns.AllFlights, f =>
        {
            Assert.Equal(("DEN", "SEA"), (f.Origin, f.Destination));
            Assert.Equal(Return, DateOnly.FromDateTime(f.DepartsLocal));
            Assert.True(f.Selection.IsComplete);
            Assert.NotNull(f.SeparateTickets);
        });

        // Booking options are not available over HTTPS (protocol.md section 2, H3): no request, the booking page's link.
        var back = returns.AllFlights.FirstOrDefault(f => f.SeparateTickets == false) ?? returns.AllFlights.First();
        var unavailable = await Assert.ThrowsAsync<GoogleFlightsUnavailableException>(() => live.Client.GetBookingOptionsAsync(back.Selection));
        Assert.Equal("/travel/flights/booking", unavailable.GoogleFlightsUrl!.AbsolutePath);
    }

    [LiveFact]
    public async Task Nonstop_airline_and_departure_hour_filters_are_honoured()
    {
        var filters = new LegFilters { Stops = MaxStops.Nonstop, Airlines = ["AS"], DepartureHours = new HourRange(6, 18) };

        var result = await live.Client.SearchAsync(FlightSearchRequest.OneWay("SEA", "SFO", Departure, filters));

        Assert.NotEmpty(result.AllFlights);
        Assert.All(result.AllFlights, f =>
        {
            Assert.Equal(0, f.Stops);
            Assert.Equal("AS", f.Segments[0].Carrier);
            Assert.InRange(f.DepartsLocal.Hour, 6, 17);
        });
    }

    [LiveFact]
    public async Task One_stop_or_fewer_allows_no_more_than_one_stop()
    {
        // Pins the filter value the page was never recorded sending (protocol.md section 4, pending).
        var filters = new LegFilters { Stops = MaxStops.OneOrFewer };

        var result = await live.Client.SearchAsync(FlightSearchRequest.OneWay("SEA", "BOS", Departure, filters));

        Assert.NotEmpty(result.AllFlights);
        Assert.All(result.AllFlights, f => Assert.InRange(f.Stops, 0, 1));
    }

    [LiveFact]
    public async Task Prices_come_in_the_currency_asked_for()
    {
        var result = await live.Client.SearchAsync(FlightSearchRequest.OneWay("SEA", "SFO", Departure) with { Currency = "EUR" });

        Assert.Contains(result.AllFlights, f => f.Price is not null);
        Assert.All(result.AllFlights.Where(f => f.Price is not null), f => Assert.Equal("EUR", f.Price!.Currency));
    }

    [LiveFact]
    public async Task An_airport_lookup_finds_the_city_and_its_airport()
    {
        var lookup = await live.Client.LookupAirportsAsync("Denver");

        Assert.Contains(lookup.Places, p => p.Name.StartsWith("Denver", StringComparison.Ordinal));
        Assert.Contains(lookup.Airports, a => a is { Code: "DEN", IsAirport: true });
    }

    [LiveFact]
    public async Task Contract_answers_keep_the_shape_the_parser_relies_on()
    {
        // The drift check: positions the client reads, asked of a fresh answer. A failure here means Google
        // changed its payloads; re-capture and re-derive protocol.md before changing the parser.
        var result = await live.Client.SearchAsync(FlightSearchRequest.OneWay("SEA", "DEN", Departure), verify: true);
        var flights = result.AllFlights.ToArray();

        Assert.NotEmpty(flights);
        Assert.All(flights, f =>
        {
            Assert.All(f.Segments, s =>
            {
                Assert.Matches("^[A-Z0-9]{2}$", s.Carrier);
                Assert.Matches("^[0-9]+$", s.FlightNumber);
                Assert.NotNull(s.Departs);   // itinerary[8]: times with UTC offsets
                Assert.NotNull(s.Arrives);
                Assert.True(s.Duration > TimeSpan.Zero);
            });
            Assert.Equal(f.Segments.Count - 1, f.Layovers.Count); // itinerary[0][13]
        });
        Assert.Contains(flights, f => Tokens.Price(f.Selection.Token) is not null); // tokens still carry the exact price
        Assert.NotEmpty(result.Airlines);                                           // index 7.1: the airline filter options
    }
}
