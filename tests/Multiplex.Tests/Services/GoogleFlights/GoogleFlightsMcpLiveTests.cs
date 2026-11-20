using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Multiplex.Tests.Services.TripIt;

namespace Multiplex.Tests.Services.GoogleFlights;

/// <summary>A test that reaches the real Google Flights (no browser); opt in with MULTIPLEX_LIVE_TESTS=1.</summary>
public sealed class GoogleFlightsLiveFactAttribute : FactAttribute
{
    public GoogleFlightsLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1")
            Skip = "Live test against Google Flights. Set MULTIPLEX_LIVE_TESTS=1 to run it (five paced requests).";
    }
}

/// <summary>
/// Every Google Flights tool over the real MCP pipeline against the real Google Flights: five requests, paced by
/// the client. Assertions are on structure, never on prices.
/// </summary>
public class GoogleFlightsMcpLiveTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [GoogleFlightsLiveFact]
    public async Task Every_tool_answers_against_the_real_Google_Flights()
    {
        var mcp = new McpTestClient(factory.CreateClient(), token: null, "/google-flights/mcp");
        var departure = DateTime.Today.AddDays(44).ToString("yyyy-MM-dd");
        var back = DateTime.Today.AddDays(51).ToString("yyyy-MM-dd");

        var airports = await CallAsync(mcp, "lookup_airport", new { query = "Denver" });
        Assert.Contains(airports.GetProperty("places").EnumerateArray().SelectMany(p => p.GetProperty("airports").EnumerateArray()),
            a => a.GetProperty("code").GetString() == "DEN");

        var outbound = await CallAsync(mcp, "search_flights", new { origin = "SEA", destination = "DEN", departure_date = departure, return_date = back });
        var first = outbound.GetProperty("flights")[0];
        Assert.Equal("SEA", first.GetProperty("from").GetString());

        var returns = await CallAsync(mcp, "get_return_flights", new { selection = first.GetProperty("selection").GetString() });
        var home = returns.GetProperty("flights")[0];
        Assert.Equal("DEN", home.GetProperty("from").GetString());

        var booking = await CallAsync(mcp, "get_booking_options", new { selection = home.GetProperty("selection").GetString() });
        Assert.NotEqual(0, booking.GetProperty("options").GetArrayLength());

        var calendar = await CallAsync(mcp, "get_price_calendar", new
        {
            origin = "SEA", destination = "DEN", departure_date_from = departure, departure_date_to = DateTime.Today.AddDays(50).ToString("yyyy-MM-dd"),
            trip_length_days = 7,
        });
        Assert.NotEqual(0, calendar.GetProperty("prices").GetArrayLength());

        var airlines = await CallAsync(mcp, "lookup_airline", new { query = "DL" });
        Assert.Equal("Delta", airlines.GetProperty("airlines")[0].GetProperty("name").GetString());
    }

    private static async Task<JsonElement> CallAsync(McpTestClient mcp, string tool, object arguments)
    {
        var (isError, text) = await mcp.CallAsync(tool, arguments);
        Assert.False(isError, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
