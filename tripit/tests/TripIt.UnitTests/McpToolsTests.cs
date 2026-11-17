using System.Text.Json;
using System.Text.Json.Nodes;
using TripItMcp;
using Xunit;

namespace TripIt.UnitTests;

public sealed class McpToolsTests
{
    [Fact]
    public async Task GetTrip_returns_summary_shared_with_names_and_itinerary()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);
        var tools = new TripItTools(client);

        using var doc = JsonDocument.Parse(await tools.GetTrip(Fixtures.TripUuid));
        var root = doc.RootElement;

        Assert.Equal("Test Trip to Lisbon", root.GetProperty("trip").GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("itinerary").GetArrayLength());
        var participants = root.GetProperty("participants").EnumerateArray().ToList();
        Assert.Equal(["Test User", "Friend User"], participants.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(["traveler", "viewer"], participants.Select(s => s.GetProperty("role").GetString()));
        Assert.True(participants[0].GetProperty("is_me").GetBoolean());
        Assert.True(root.GetProperty("trip").GetProperty("i_am_traveling").GetBoolean());
        Assert.False(root.TryGetProperty("raw", out _));
    }

    [Fact]
    public async Task GetTrip_output_keeps_arrows_unescaped()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var json = await new TripItTools(client).GetTrip(Fixtures.TripUuid);

        Assert.Contains("JFK → LIS", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTrip_with_raw_includes_pruned_objects()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        using var doc = JsonDocument.Parse(await new TripItTools(client).GetTrip(Fixtures.TripUuid, includeRaw: true));
        var lodging = doc.RootElement.GetProperty("raw").GetProperty("lodging")[0];

        Assert.Equal("Hotel Example", lodging.GetProperty("supplier_name").GetString());
        Assert.False(lodging.TryGetProperty("relative_url", out _));
    }

    [Fact]
    public async Task ListObjects_requires_exactly_one_scope()
    {
        var (client, _, _) = FakeTripIt.Create();
        var tools = new TripItTools(client);
        await Assert.ThrowsAsync<ArgumentException>(() => tools.ListObjects());
        await Assert.ThrowsAsync<ArgumentException>(() => tools.ListObjects(Fixtures.TripUuid, unfiledOnly: true));
    }

    [Fact]
    public async Task ListTrips_rejects_unknown_timeframe()
    {
        var (client, _, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new TripItTools(client).ListTrips("someday"));
    }

    [Fact]
    public void MaskSensitiveNumbers_keeps_last_four()
    {
        var node = JsonNode.Parse("""{"Record":[{"template_name":"TravelDocumentPassport","document_number":"123456789","date_of_birth":"2000-01-01"}]}""");
        TripItTools.MaskSensitiveNumbers(node);
        Assert.Equal("*****6789", node!["Record"]![0]!["document_number"]!.GetValue<string>());
        Assert.Equal("2000-01-01", node["Record"]![0]!["date_of_birth"]!.GetValue<string>());
    }

    [Fact]
    public async Task RawGet_only_allows_api_v2_paths()
    {
        var (client, _, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new TripItRawTools(client).RawGet("/account/edit"));
    }
}
