using Xunit;

namespace TripIt.UnitTests;

public sealed class TripsClientTests
{
    [Fact]
    public async Task List_parses_single_trip_object_and_paging()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.ListTripSingle);

        var list = await client.Trips.ListAsync(TripTimeframe.Past, pageSize: 5);

        var trip = Assert.Single(list.Trips);
        Assert.Equal(Fixtures.TripUuid, trip.Uuid);
        Assert.Equal($"https://www.tripit.com/app/trips/{Fixtures.TripUuid}", trip.WebUrl);
        Assert.Equal(1, list.MaxPage);
        Assert.Contains("past=true", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("page_size=5", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAll_follows_pages_until_max_page()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.ListTripPage(1, 2, "A", "B"))
               .OnJson("/api/v2/list/trip", Fixtures.ListTripPage(2, 2, "C"));

        var names = new List<string?>();
        await foreach (var t in client.Trips.ListAllAsync(TripTimeframe.Past, pageSize: 2)) names.Add(t.DisplayName);

        Assert.Equal(["A", "B", "C"], names);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Get_uses_include_objects_path_and_groups_objects_by_type()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var detail = await client.Trips.GetAsync(Fixtures.TripUuid);

        Assert.Equal("Test Trip to Lisbon", detail.Trip.DisplayName);
        Assert.Single(detail.Objects[TripItObjectType.Air]);
        Assert.Single(detail.Objects[TripItObjectType.Lodging]);
        Assert.Equal(2, detail.ObjectCount); // weather is not counted
        Assert.Equal(2, detail.Profiles.Count);
    }

    [Fact]
    public async Task Get_rejects_numeric_id_with_explanation()
    {
        var (client, _, _) = FakeTripIt.Create();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.Trips.GetAsync("388109389x"));
        Assert.Contains("UUID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_matches_name_location_case_insensitively_across_upcoming_and_past()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.On("/api/v2/list/trip", r => FakeHandler.Json(r.RequestUri!.Query.Contains("past=true", StringComparison.Ordinal)
            ? Fixtures.ListTripPage(1, 1, "VSLive 2018", "Family visit")
            : Fixtures.ListTripPage(1, 1, "Speaking at vslive", "Lisbon")));

        var found = await client.Trips.SearchAsync("VSLIVE");

        Assert.Equal(["Speaking at vslive", "VSLive 2018"], found.Select(t => t.DisplayName));
    }

    [Fact]
    public async Task ListWithObjects_assigns_objects_to_their_trip()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.TripWithObjects);

        var trips = await client.Trips.ListWithObjectsAsync();

        var trip = Assert.Single(trips);
        Assert.Equal(2, trip.ObjectCount);
        Assert.Contains("include_objects=true", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Objects_list_sends_trip_uuid_and_unfiled_filters()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/object", """{"NoteObject":{"uuid":"n1","display_name":"A note"},"page_num":"1","max_page":"3","total_items":"7"}""");

        var list = await client.Objects.ListAsync(unfiledOnly: true, pageSize: 3);

        Assert.Single(list.Objects[TripItObjectType.Note]);
        Assert.Equal(7, list.TotalItems);
        Assert.Contains("is_unfiled_items_only=true", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("trip_uuid", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Objects_get_uses_type_path_and_unwraps_envelope()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/lodging/uuid/{Fixtures.LodgingUuid}",
            $$$"""{"LodgingObject":{"uuid":"{{{Fixtures.LodgingUuid}}}","supplier_name":"Hotel Example"}}""");

        var obj = await client.Objects.GetAsync(TripItObjectType.Lodging, Fixtures.LodgingUuid);

        Assert.Equal("Hotel Example", obj.GetProperty("supplier_name").GetString());
    }

    [Fact]
    public async Task Lookup_airports_maps_id_and_label()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/autocompleteAirport", """[{"id":"SEA","label":"Seattle-Tacoma International Airport (SEA)"}]""");

        var results = await client.Lookup.AirportsAsync("SEA");

        Assert.Equal("SEA", Assert.Single(results).Id);
        Assert.Contains("query=SEA", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }
}
