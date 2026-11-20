using System.Text.Json;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>
/// The encoder reproduces, byte for byte, requests Google answered (direct HTTP, F1-F4) and requests the
/// page itself sent (held filter and sort states, multi-city, calendar, autocomplete).
/// </summary>
public class RequestEncoderTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);
    private static readonly DateOnly Dec22 = new(2026, 12, 22);

    private static FlightSearchRequest Normalized(FlightSearchRequest r) => r.Normalize("USD", "US");

    private static FlightSearchRequest SeaDenRoundTrip => Normalized(FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, Dec22));

    [Fact]
    public void A_fresh_round_trip_search_is_the_request_Google_answered_over_direct_HTTP()
    {
        var expected = HarFixture.Load("direct-http-flow-2026-11-20")[0].InnerRequest;

        Assert.Equal(expected, RequestEncoder.ShoppingResults(SeaDenRoundTrip));
    }

    [Fact]
    public void The_return_flight_step_carries_the_outbound_and_its_token()
    {
        var recorded = HarFixture.Load("direct-http-flow-2026-11-20")[1].InnerRequest;
        var token = JsonDocument.Parse(recorded).RootElement.Str(0, 1)!;
        var selection = new FlightSelection(SeaDenRoundTrip, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")]], token);

        Assert.Equal(recorded, RequestEncoder.ShoppingResults(SeaDenRoundTrip, selection));
    }

    [Fact]
    public void Booking_options_carry_every_chosen_leg_and_the_last_token()
    {
        var recorded = HarFixture.Load("direct-http-flow-2026-11-20")[2].InnerRequest;
        var token = JsonDocument.Parse(recorded).RootElement.Str(0, 1)!;
        var selection = new FlightSelection(SeaDenRoundTrip,
            [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")], [new ChosenSegment("DEN", Dec22, "SEA", "DL", "1731")]],
            token);

        Assert.Equal(recorded, RequestEncoder.BookingResults(selection));
    }

    [Fact]
    public void The_price_graph_is_the_request_Google_answered()
    {
        var expected = HarFixture.Load("direct-http-flow-2026-11-20")[3].InnerRequest;

        Assert.Equal(expected, RequestEncoder.CalendarGraph(SeaDenRoundTrip, new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 20), 7));
    }

    [Fact]
    public void The_date_grid_is_the_request_the_page_sent()
    {
        var expected = HarFixture.Load("calendar-grid-and-graph-2026-11-19")[0].InnerRequest;

        Assert.Equal(expected, RequestEncoder.CalendarGrid(SeaDenRoundTrip,
            new DateOnly(2026, 12, 12), new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 19), new DateOnly(2026, 12, 25)));
    }

    [Fact]
    public void A_multi_city_filter_is_the_one_the_page_sent()
    {
        var recorded = JsonDocument.Parse(HarFixture.Load("multi-city-to-booking-2026-11-19")[0].InnerRequest).RootElement[1];
        var request = Normalized(FlightSearchRequest.MultiCity(
            new FlightLeg("SEA", "DEN", Dec15),
            new FlightLeg("DEN", "LAX", new DateOnly(2026, 12, 19))));

        Assert.Equal(recorded.GetRawText(), RequestEncoder.Serialize(RequestEncoder.Filter(request)));
    }

    [Theory]
    // Each row is one state of the page's filter menu (capture H), as the page encoded it.
    [InlineData(1, "Stops: nonstop; airlines: SkyTeam")]
    [InlineData(4, "Nonstop; airlines DL and AS")]
    [InlineData(12, "Nonstop; DL, AS; depart 6-24; arrive 0-20")]
    [InlineData(17, "...; max price 990; max 36 hours; economy excluding basic")]
    [InlineData(20, "...; first class")]
    [InlineData(21, "...; first; 2 adults, 2 children, 1 infant on lap, 1 in seat")]
    public void Each_filter_is_encoded_as_the_page_encoded_it(int entry, string state)
    {
        var recorded = JsonDocument.Parse(HarFixture.Load("filter-requests-held-2026-11-19")[entry].InnerRequest).RootElement;
        var request = Normalized(HeldFilterState(entry));

        Assert.True(recorded.GetRawText() == RequestEncoder.ShoppingResults(request), state);
    }

    [Theory]
    [InlineData(0, FlightSort.Price)]
    [InlineData(2, FlightSort.DepartureTime)]
    [InlineData(3, FlightSort.ArrivalTime)]
    [InlineData(4, FlightSort.Duration)]
    public void Each_sort_order_is_the_value_the_page_sent(int entry, FlightSort sort)
    {
        var recorded = HarFixture.Load("sort-and-multi-city-held-2026-11-19")[entry].InnerRequest;
        var filters = new LegFilters
        {
            Stops = MaxStops.Nonstop,
            Airlines = ["DL", "AS"],
            DepartureHours = new HourRange(6, 24),
            ArrivalHours = new HourRange(0, 23),
            MaxDuration = TimeSpan.FromHours(6),
        };
        var request = Normalized(FlightSearchRequest.OneWay("SEA", "DEN", Dec15, filters) with
        {
            MaxPrice = 400,
            ExcludeBasicEconomy = true,
            Sort = sort,
        });

        Assert.Equal(recorded, RequestEncoder.ShoppingResults(request));
    }

    [Fact]
    public void Autocomplete_is_the_request_the_page_sends_for_an_origin()
    {
        var recorded = HarFixture.Load("airport-autocomplete-2026-11-19")[3];

        Assert.Equal(recorded.InnerRequest, RequestEncoder.Autocomplete("Seat"));
    }

    [Fact]
    public void The_language_region_and_currency_header_is_the_one_every_request_sent()
    {
        var recorded = HarFixture.Load("direct-http-flow-2026-11-20")[0].RequestHeader("x-goog-ext-259736195-jspb");

        Assert.Equal(recorded, RequestEncoder.JspbHeader("USD", "US"));
    }

    [Fact]
    public void The_service_body_is_the_form_Google_answered()
    {
        var recorded = HarFixture.Load("direct-http-flow-2026-11-20")[0];

        Assert.Equal(recorded.RequestBody, RequestEncoder.ServiceBody(recorded.InnerRequest));
    }

    /// <summary>The filter states of capture H, built through the public request model.</summary>
    private static FlightSearchRequest HeldFilterState(int entry)
    {
        var filters = new LegFilters { Stops = MaxStops.Nonstop };
        var request = FlightSearchRequest.OneWay("SEA", "DEN", Dec15);
        if (entry == 1)
            return request with { Legs = [request.Legs[0] with { Filters = filters with { Alliances = [Alliance.SkyTeam] } }] };

        filters = filters with { Airlines = ["DL", "AS"] };
        if (entry >= 12)
            filters = filters with { DepartureHours = new HourRange(6, 24), ArrivalHours = new HourRange(0, 20) };
        if (entry >= 17)
            filters = filters with { MaxDuration = TimeSpan.FromHours(36) };
        request = request with { Legs = [request.Legs[0] with { Filters = filters }] };
        if (entry >= 17)
            request = request with { MaxPrice = 990, ExcludeBasicEconomy = true };
        if (entry >= 20)
            request = request with { Cabin = CabinClass.First };
        if (entry >= 21)
            request = request with { Passengers = new Passengers(2, 2, 1, 1) };
        return request;
    }
}
