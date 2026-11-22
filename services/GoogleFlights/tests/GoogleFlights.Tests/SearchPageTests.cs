using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Multiplex.Sites.GoogleFlights.Tests;

public class SearchPageTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);
    private static readonly DateOnly Dec22 = new(2026, 12, 22);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeGoogle _google = new();

    private GoogleFlightsClient Client()
    {
        var options = new GoogleFlightsClientOptions { BurstRequests = 100, MaxRetries = 0 };
        return new GoogleFlightsClient(options, _time, _google);
    }

    private static FlightSearchRequest SeaDen => FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, Dec22);

    private static string WrapHtml(string jsonPayload, string key = "ds:1", string? extraHtml = null) =>
        "<!DOCTYPE html>\n<html>\n<head><title>Google Flights</title></head>\n<body>\n" +
        (extraHtml ?? "") +
        $"\n<script>AF_initDataCallback({{key: '{key}', hash: '1', data:{jsonPayload}, sideChannel: {{}}}});</script>\n</body>\n</html>";

    [Fact]
    public async Task Search_extracts_flights_from_html_page_response()
    {
        // Re-use payload from Flow[0] (which is the JSON array payload for GetShoppingResults)
        var flow = HarFixture.Load("direct-http-flow-2026-11-20");
        var rpcText = flow[0].ResponseText;
        Assert.True(Wire.ResponseFrames.TryParse(rpcText, out var rpcFrames));
        var rawJson = rpcFrames.Payloads[0].GetRawText();

        var html = WrapHtml(rawJson);
        _google.Answer(HttpStatusCode.OK, html);
        using var client = Client();

        var result = await client.SearchAsync(SeaDen);

        Assert.Equal(13, result.AllFlights.Count());
        Assert.Equal(0, result.Leg);
        Assert.Equal("www.google.com", result.GoogleFlightsUrl.Host);
        Assert.Contains("tfs=", result.GoogleFlightsUrl.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_throws_when_ds1_key_is_missing_from_html()
    {
        var html = WrapHtml("[]", key: "ds:0");
        _google.Answer(HttpStatusCode.OK, html);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(SeaDen));
        Assert.Contains("ds:1", ex.Message);
    }

    [Fact]
    public async Task Search_throws_when_html_data_is_not_valid_json()
    {
        var html = WrapHtml("[invalid json", key: "ds:1");
        _google.Answer(HttpStatusCode.OK, html);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(SeaDen));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task Search_throws_when_html_data_is_not_a_json_array()
    {
        var html = WrapHtml("{\"not\": \"array\"}", key: "ds:1");
        _google.Answer(HttpStatusCode.OK, html);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsFormatException>(() => client.SearchAsync(SeaDen));
        Assert.Contains("did not contain a data array", ex.Message);
    }

    [Fact]
    public async Task Search_throws_challenge_when_html_indicates_unusual_traffic()
    {
        var html = "<html><body>We detected unusual traffic from your computer network</body></html>";
        _google.Answer(HttpStatusCode.OK, html);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Challenge, ex.Reason);
    }

    [Fact]
    public async Task Search_throws_challenge_on_redirect_to_sorry()
    {
        _google.Answer(HttpStatusCode.Redirect, "", r => r.Headers.Location = new Uri("https://www.google.com/sorry/index"));
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Challenge, ex.Reason);
    }

    [Fact]
    public async Task Search_handles_http_429_as_throttled()
    {
        _google.Answer(HttpStatusCode.TooManyRequests);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.TooManyRequests, ex.Reason);
    }

    [Fact]
    public async Task Search_handles_http_403_as_refused()
    {
        _google.Answer(HttpStatusCode.Forbidden);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsThrottledException>(() => client.SearchAsync(SeaDen));
        Assert.Equal(ThrottleReason.Refused, ex.Reason);
    }

    [Fact]
    public async Task Search_handles_http_500_as_transient()
    {
        _google.Answer(HttpStatusCode.InternalServerError);
        using var client = Client();

        var ex = await Assert.ThrowsAsync<GoogleFlightsTransientException>(() => client.SearchAsync(SeaDen));
        Assert.Contains("server error", ex.Message);
    }
}
