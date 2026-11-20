using System.Text.Json;
using Multiplex.Sites.GoogleFlights;

namespace Multiplex.Tests.Services.GoogleFlights;

/// <summary>The MCP tools at <c>/google-flights/mcp</c>, over the real MCP pipeline, against recorded answers.</summary>
public class GoogleFlightsToolTests : IDisposable
{
    private readonly GoogleFlightsTestApp _app = new();

    private static readonly Dictionary<string, string> Described = new()
    {
        ["search_flights"] = GoogleFlightsDocs.SearchFlights,
        ["get_return_flights"] = GoogleFlightsDocs.GetReturnFlights,
        ["get_booking_options"] = GoogleFlightsDocs.GetBookingOptions,
        ["get_price_calendar"] = GoogleFlightsDocs.GetPriceCalendar,
        ["lookup_airport"] = GoogleFlightsDocs.LookupAirport,
        ["lookup_airline"] = GoogleFlightsDocs.LookupAirline,
    };

    [Fact]
    public async Task The_endpoint_serves_the_six_tools_described_once_in_the_client()
    {
        var tools = await _app.Mcp().ListToolsAsync();

        Assert.Equal(Described.Keys.Order(), tools.Select(t => t.GetProperty("name").GetString()!).Order());
        Assert.All(tools, t =>
        {
            var name = t.GetProperty("name").GetString()!;
            Assert.Equal(Described[name], t.GetProperty("description").GetString());
            Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), name);
            var verify = t.GetProperty("inputSchema").GetProperty("properties").GetProperty("verify");
            Assert.Equal(GoogleFlightsDocs.Verify, verify.GetProperty("description").GetString());
        });
    }

    [Fact]
    public async Task A_round_trip_goes_from_search_to_returns_to_booking_through_selections()
    {
        _app.Google.Answer("direct-http-flow-2026-11-20", 0).Answer("direct-http-flow-2026-11-20", 1).Answer("direct-http-flow-2026-11-20", 2);
        var mcp = _app.Mcp();

        var search = await CallAsync(mcp, "search_flights", new { origin = "SEA", destination = "DEN", departure_date = "2026-12-15", return_date = "2026-12-22" });
        Assert.Equal(1, search.GetProperty("leg").GetInt32());
        Assert.Equal(2, search.GetProperty("of_legs").GetInt32());
        Assert.Contains("get_return_flights", search.GetProperty("next_step").GetString());
        var flights = search.GetProperty("flights").EnumerateArray().ToArray();
        Assert.Equal(13, flights.Length);
        Assert.Equal("AS682", flights[0].GetProperty("segments")[0].GetProperty("flight").GetString());
        Assert.Equal("2026-12-15T07:25-08:00", flights[0].GetProperty("segments")[0].GetProperty("departs").GetString());
        var delta = flights.First(f => f.GetProperty("segments")[0].GetProperty("flight").GetString() == "DL1358");

        var returns = await CallAsync(mcp, "get_return_flights", new { selection = delta.GetProperty("selection").GetString() });
        Assert.Equal(17, returns.GetProperty("flights").GetArrayLength());
        Assert.Contains("get_booking_options", returns.GetProperty("next_step").GetString());
        var back = returns.GetProperty("flights").EnumerateArray()
            .First(f => f.GetProperty("segments")[0].GetProperty("flight").GetString() == "DL1731");

        var booking = await CallAsync(mcp, "get_booking_options", new { selection = back.GetProperty("selection").GetString() });
        Assert.Equal(7, booking.GetProperty("options").GetArrayLength());
        Assert.Equal("Delta Main Basic", booking.GetProperty("options")[0].GetProperty("fare").GetString());
        Assert.Contains("Nothing is booked", booking.GetProperty("note").GetString());

        // What went to Google is exactly what Google answered in the recorded flow.
        Assert.Equal(
            [RecordedGoogle.RecordedInnerRequest("direct-http-flow-2026-11-20", 0),
             RecordedGoogle.RecordedInnerRequest("direct-http-flow-2026-11-20", 1),
             RecordedGoogle.RecordedInnerRequest("direct-http-flow-2026-11-20", 2)],
            _app.Google.SentInnerRequests.ToArray());
    }

    [Fact]
    public async Task A_bad_airport_is_a_tool_error_that_says_what_to_fix_and_nothing_is_sent()
    {
        var (isError, text) = await _app.Mcp().CallAsync("search_flights", new { origin = "Seattle", destination = "DEN", departure_date = "2026-12-15" });

        Assert.True(isError);
        Assert.Contains("three-letter IATA airport code", text);
        Assert.Empty(_app.Google.SentInnerRequests);
    }

    [Fact]
    public async Task A_refusal_tells_the_AI_not_to_retry()
    {
        _app.Google.Answer("direct-search-refused-2026-11-19", 0);

        var (isError, text) = await _app.Mcp().CallAsync("search_flights", new { origin = "SEA", destination = "DEN", departure_date = "2026-12-15" });

        Assert.True(isError);
        Assert.Contains("Do not retry before", text);
    }

    [Fact]
    public async Task A_price_calendar_lists_prices_and_the_cheapest_date()
    {
        _app.Google.Answer("direct-http-flow-2026-11-20", 3);

        var calendar = await CallAsync(_app.Mcp(), "get_price_calendar", new
        {
            origin = "SEA", destination = "DEN", departure_date_from = "2026-12-10", departure_date_to = "2026-12-20", trip_length_days = 7,
        });

        Assert.Equal(11, calendar.GetProperty("prices").GetArrayLength());
        Assert.Equal(162.96m, calendar.GetProperty("cheapest").GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task An_airport_lookup_lists_places_and_codes()
    {
        _app.Google.Answer("airport-autocomplete-2026-11-19", 12);

        var lookup = await CallAsync(_app.Mcp(), "lookup_airport", new { query = "Denv" });

        var denver = lookup.GetProperty("places")[0];
        Assert.Equal("Denver, Colorado", denver.GetProperty("name").GetString());
        Assert.Equal("DEN", denver.GetProperty("airports")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_airline_lookup_answers_from_the_data_file_without_reaching_Google()
    {
        var result = await CallAsync(_app.Mcp(), "lookup_airline", new { query = "skyteam" });

        Assert.Equal(GoogleFlightsClient.Airlines.Version, result.GetProperty("data_version").GetString());
        Assert.Contains(result.GetProperty("airlines").EnumerateArray(), a => a.GetProperty("code").GetString() == "DL");
        Assert.All(result.GetProperty("airlines").EnumerateArray(), a => Assert.Equal("skyteam", a.GetProperty("alliance").GetString()));
        Assert.Empty(_app.Google.SentInnerRequests);
    }

    private static async Task<JsonElement> CallAsync(Multiplex.Tests.Services.TripIt.McpTestClient mcp, string tool, object arguments)
    {
        var (isError, text) = await mcp.CallAsync(tool, arguments);
        Assert.False(isError, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public void Dispose() => _app.Dispose();
}
