using Xunit;

namespace TripIt.FunctionalTests;

/// <summary>
/// Live read-surface checks against the signed-in account. Assertions are about shape,
/// not content, so they hold for any account; tests skip when the account has no data
/// of the kind being checked.
/// </summary>
public sealed class ReadSurfaceTests : IClassFixture<TripItSessionFixture>
{
    private readonly TripItSessionFixture _fx;

    public ReadSurfaceTests(TripItSessionFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Trips_list_upcoming_and_past_parse_with_uuids()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        foreach (var tf in new[] { TripTimeframe.Upcoming, TripTimeframe.Past })
        {
            var list = await _fx.Client.Trips.ListAsync(tf, pageSize: 5);
            Assert.All(list.Trips, t => Assert.False(string.IsNullOrWhiteSpace(t.Uuid)));
        }
    }

    [SkippableFact]
    public async Task Trips_get_with_objects_builds_itinerary()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var upcoming = await _fx.Client.Trips.ListAsync(TripTimeframe.Upcoming, pageSize: 5);
        var past = await _fx.Client.Trips.ListAsync(TripTimeframe.Past, pageSize: 5);
        var trip = upcoming.Trips.Concat(past.Trips).FirstOrDefault();
        Skip.If(trip is null, "Account has no trips.");

        var detail = await _fx.Client.Trips.GetAsync(trip!.Uuid!);

        Assert.Equal(trip.Uuid, detail.Trip.Uuid);
        Assert.All(detail.Itinerary, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
    }

    [SkippableFact]
    public async Task Objects_get_round_trips_an_object_from_a_trip()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var trips = await _fx.Client.Trips.ListWithObjectsAsync();
        var (type, obj) = trips
            .SelectMany(t => t.Objects)
            .Where(kv => kv.Key != TripItObjectType.Weather)
            .SelectMany(kv => kv.Value.Select(o => (kv.Key, o)))
            .FirstOrDefault();
        Skip.If(obj.ValueKind == System.Text.Json.JsonValueKind.Undefined, "No upcoming trip objects.");

        var uuid = obj.GetProperty("uuid").GetString()!;
        var fetched = await _fx.Client.Objects.GetAsync(type, uuid);

        Assert.Equal(uuid, fetched.GetProperty("uuid").GetString());
    }

    [SkippableFact]
    public async Task Objects_list_unfiled_returns_paging()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var list = await _fx.Client.Objects.ListAsync(unfiledOnly: true, pageSize: 2);
        Assert.NotNull(list.Objects);
    }

    [SkippableFact]
    public async Task Lookup_airport_finds_SEA()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var results = await _fx.Client.Lookup.AirportsAsync("SEA");
        Assert.Contains(results, r => r.Id == "SEA");
    }

    [SkippableFact]
    public async Task Wrong_type_for_uuid_surfaces_TripIt_error()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        var trip = (await _fx.Client.Trips.ListAsync(TripTimeframe.Past, pageSize: 1)).Trips.FirstOrDefault();
        Skip.If(trip is null, "Account has no trips.");

        var ex = await Assert.ThrowsAsync<TripItApiException>(
            () => _fx.Client.Objects.GetAsync(TripItObjectType.Air, trip!.Uuid!));
        Assert.Equal(400, ex.StatusCode);
    }
}
