using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Multiplex.Tests;

/// <summary>
/// End-to-end: MCP call over HTTP, real Chromium, real time.now. Opt in with MULTIPLEX_LIVE_TESTS=1.
/// Expected values come from .NET's own time zone database, so they don't depend on the site being right.
/// These are also the tests that notice when time.now changes its markup.
/// </summary>
public class TimeMcpLiveTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public TimeMcpLiveTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(b => b.UseSetting("Browser:ExecutablePath", ChromiumLocator.Path ?? ""))
            .CreateClient();
    }

    [LiveTheory]
    [InlineData("Japan", "Asia/Tokyo")]
    [InlineData("Tokyo", "Asia/Tokyo")]
    [InlineData("New Zealand", "Pacific/Auckland")] // DST: the page's static offset links disagree with its clock
    [InlineData("India", "Asia/Kolkata")]           // half-hour zone
    [InlineData("Peru", "America/Lima")]            // negative offset
    public async Task GetLocalTime_MatchesTimeZoneDatabase(string location, string ianaId)
    {
        var (isError, text) = await CallGetLocalTime(location);
        Assert.False(isError, text);

        var before = DateTimeOffset.UtcNow;
        var result = JsonDocument.Parse(text).RootElement;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        var offset = zone.GetUtcOffset(before);

        Assert.Equal($"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset:hh\\:mm}", result.GetProperty("utcOffset").GetString());

        var expected = (before + offset).DateTime;
        var shown = TimeSpan.ParseExact(result.GetProperty("time").GetString()!, @"hh\:mm\:ss", null);
        var drift = Circular(shown - expected.TimeOfDay);
        Assert.True(drift < TimeSpan.FromMinutes(2), $"Clock {shown} drifted {drift} from expected {expected.TimeOfDay}.");

        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("location").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("timeZone").GetString()));
        Assert.StartsWith("https://time.now/", result.GetProperty("source").GetString());
    }

    [LiveFact]
    public async Task GetLocalTime_ReportsToolErrorWhenNoPlaceMatches()
    {
        var (isError, text) = await CallGetLocalTime("Zzyzxqwv");

        Assert.True(isError);
        Assert.Contains("no city or country matching 'Zzyzxqwv'", text);
    }

    private async Task<(bool IsError, string Text)> CallGetLocalTime(string location)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "get_local_time", arguments = new { location } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/time/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // Streamable HTTP replies as SSE: one "data: {json-rpc response}" line.
        var body = await response.Content.ReadAsStringAsync();
        var json = body.Split('\n').First(l => l.StartsWith("data:"))["data:".Length..].Trim();
        var result = JsonDocument.Parse(json).RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    /// <summary>Absolute difference between two times of day, wrapping at midnight.</summary>
    private static TimeSpan Circular(TimeSpan diff)
    {
        var abs = diff.Duration();
        return abs > TimeSpan.FromHours(12) ? TimeSpan.FromHours(24) - abs : abs;
    }
}
