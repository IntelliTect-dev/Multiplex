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
    public async Task A_round_trip_goes_from_search_to_returns_through_selections_and_booking_gives_the_link()
    {
        // The outbound list as F1 recorded it, served the way a search page carries it; the returns are the page
        // fetched live on 2026-11-24 with Delta DL2759 pinned and every flight asked for (H9).
        _app.Google.AnswerPageWith("direct-http-flow-2026-11-20", 0).AnswerPage("return-leg-full-2026-11-24");
        var mcp = _app.Mcp();

        // Basic Economy included, as when H9 was fetched, so the links compare with the recorded one.
        var search = await CallAsync(mcp, "search_flights", new
        {
            origin = "SEA", destination = "DEN", departure_date = "2026-12-15", return_date = "2026-12-22", exclude_basic_economy = false,
        });
        Assert.Equal(1, search.GetProperty("leg").GetInt32());
        Assert.Equal(2, search.GetProperty("of_legs").GetInt32());
        Assert.Contains("get_return_flights", search.GetProperty("next_step").GetString());
        var flights = search.GetProperty("flights").EnumerateArray().ToArray();
        Assert.Equal(13, flights.Length);
        // Every flight fits in the answer, so nothing says there are more.
        Assert.Equal(13, search.GetProperty("total_flights").GetInt32());
        Assert.False(search.TryGetProperty("more_flights", out _));
        Assert.Equal("AS682", flights[0].GetProperty("segments")[0].GetProperty("flight").GetString());
        Assert.Equal("2026-12-15T07:25-08:00", flights[0].GetProperty("segments")[0].GetProperty("departs").GetString());
        var delta = flights.First(f => f.GetProperty("segments")[0].GetProperty("flight").GetString() == "DL2759");

        var returns = await CallAsync(mcp, "get_return_flights", new { selection = delta.GetProperty("selection").GetString() });
        Assert.Equal(21, returns.GetProperty("flights").GetArrayLength());
        // On the last leg the next step is the person's: booking options are not available here (H3).
        var lastStep = returns.GetProperty("next_step").GetString();
        Assert.Contains("google_flights_url", lastStep, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass a flight's selection to get_booking_options", lastStep, StringComparison.Ordinal);
        var back = returns.GetProperty("flights").EnumerateArray()
            .First(f => f.GetProperty("segments")[0].GetProperty("flight").GetString() == "DL1731");

        var (isError, booking) = await mcp.CallAsync("get_booking_options", new { selection = back.GetProperty("selection").GetString() });
        Assert.True(isError);
        Assert.Contains("not available", booking, StringComparison.Ordinal);
        Assert.Contains("https://www.google.com/travel/flights/booking?tfs=", booking, StringComparison.Ordinal);

        // Two search pages went to Google, the second with the chosen outbound pinned (the link H9 fetched); booking sent nothing.
        var sent = _app.Google.SentInnerRequests.ToArray();
        Assert.Equal(2, sent.Length);
        Assert.StartsWith("https://www.google.com/travel/flights/search?tfs=", sent[0], StringComparison.Ordinal);
        Assert.Equal(
            "https://www.google.com/travel/flights/search?tfs=CBwQAhpAEgoyMDI2LTEyLTE1IiAKA1NFQRIKMjAyNi0xMi0xNRoDREVOKgJETDIEMjc1OWoHCAESA1NFQXIHCAESA0RFThoeEgoyMDI2LTEyLTIyagcIARIDREVOcgcIARIDU0VBQAFIAXABggELCP___________wGYAQE&tfu=EgYIABABGAA&hl=en-US&gl=US&curr=USD",
            sent[1]);
    }

    [Fact]
    public async Task A_search_with_more_flights_than_an_answer_holds_lists_the_first_30_and_says_how_to_see_the_rest()
    {
        // Owner's decision, 2026-11-24 (#134): every flight is fetched, but an answer holds at most 30, in the order
        // asked for, so a busy route stays inside an AI's limit for one tool answer (98 flights are about 30k tokens).
        _app.Google.AnswerPage("one-way-full-2026-11-24");

        var search = await CallAsync(_app.Mcp(), "search_flights", new
        {
            origin = "SEA", destination = "DEN", departure_date = "2026-12-15", exclude_basic_economy = false,
        });

        var flights = search.GetProperty("flights").EnumerateArray().ToArray();
        Assert.Equal(30, flights.Length);
        Assert.Equal(98, search.GetProperty("total_flights").GetInt32());
        // Google's order: its 4 best flights first.
        Assert.Equal([true, true, true, true, false], flights.Take(5).Select(f => f.GetProperty("best").GetBoolean()));
        var more = search.GetProperty("more_flights").GetString()!;
        Assert.Contains("30 of 98", more, StringComparison.Ordinal);
        Assert.Contains("max_stops", more, StringComparison.Ordinal);
        Assert.Contains("google_flights_url", more, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_leaves_out_Basic_Economy_unless_asked_for()
    {
        // Owner's decision, 2026-11-22: fares such as Delta Main Basic are excluded by default (tfs field 25 = 1).
        _app.Google.AnswerPage("one-way-2026-11-22").AnswerPage("one-way-2026-11-22");
        var mcp = _app.Mcp();

        await CallAsync(mcp, "search_flights", new { origin = "SEA", destination = "DEN", departure_date = "2026-12-15" });
        await CallAsync(mcp, "search_flights", new { origin = "SEA", destination = "DEN", departure_date = "2026-12-15", exclude_basic_economy = false });

        Assert.Equal(
            ["https://www.google.com/travel/flights/search?tfs=CBwQAhoeEgoyMDI2LTEyLTE1agcIARIDU0VBcgcIARIDREVOQAFIAXABggELCP___________wGYAQLIAQE&tfu=EgYIABABGAA&hl=en-US&gl=US&curr=USD",
             "https://www.google.com/travel/flights/search?tfs=CBwQAhoeEgoyMDI2LTEyLTE1agcIARIDU0VBcgcIARIDREVOQAFIAXABggELCP___________wGYAQI&tfu=EgYIABABGAA&hl=en-US&gl=US&curr=USD"],
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
        _app.Google.AnswerRedirect("https://www.google.com/travel/flights/unsupported?tfs=x&ucpp=y");

        var (isError, text) = await _app.Mcp().CallAsync("search_flights", new { origin = "SEA", destination = "DEN", departure_date = "2026-12-15" });

        Assert.True(isError);
        Assert.Contains("Do not retry before", text);
    }

    [Fact]
    public async Task A_price_calendar_is_not_available_says_so_and_sends_nothing()
    {
        var (isError, text) = await _app.Mcp().CallAsync("get_price_calendar", new
        {
            origin = "SEA", destination = "DEN", departure_date_from = "2026-12-10", departure_date_to = "2026-12-20", trip_length_days = 7,
        });

        Assert.True(isError);
        Assert.Contains("not available", text, StringComparison.Ordinal);
        Assert.Contains("https://www.google.com/travel/flights/search?tfs=", text, StringComparison.Ordinal);
        Assert.Empty(_app.Google.SentInnerRequests);
    }

    [Fact]
    public async Task A_multi_city_search_is_not_available_says_so_and_sends_nothing()
    {
        var (isError, text) = await _app.Mcp().CallAsync("search_flights", new { legs = new[] { "SEA-DEN-2026-12-15", "DEN-LAX-2026-12-20" } });

        Assert.True(isError);
        Assert.Contains("Multi-city trips are not available", text, StringComparison.Ordinal);
        Assert.Empty(_app.Google.SentInnerRequests);
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
