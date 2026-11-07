using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace TripIt.FunctionalTests;

/// <summary>
/// Live write round trip against the signed-in account. It creates ONE dated, private
/// throwaway trip, adds one plan of every writable type to it, exercises update, move and
/// segment delete, and deletes the trip in a finally block — the scope approved for
/// mutation testing (CLAUDE.md, "Decisions already made").
///
/// Opt-in: runs only when TRIPIT_ALLOW_WRITES=1, so `dotnet test` with a session never
/// writes to an account by surprise.
/// </summary>
public sealed class WriteSurfaceTests : IClassFixture<TripItSessionFixture>
{
    private readonly TripItSessionFixture _fx;
    private readonly ITestOutputHelper _out;

    public WriteSurfaceTests(TripItSessionFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private static bool WritesAllowed => Environment.GetEnvironmentVariable("TRIPIT_ALLOW_WRITES") == "1";

    [SkippableFact]
    public async Task Full_write_round_trip_on_a_throwaway_trip()
    {
        Skip.IfNot(_fx.HasSession, "No TripIt session configured.");
        Skip.IfNot(WritesAllowed, "Set TRIPIT_ALLOW_WRITES=1 to run live write tests.");

        var client = _fx.Client;
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HHmm");
        var trip = await client.Trips.CreateAsync(new TripInput
        {
            DisplayName = $"ZZ TripItEx write test {stamp} - safe to delete",
            PrimaryLocation = "Reykjavik, Iceland",
            StartDate = "2027-02-08",
            EndDate = "2027-02-11",
            Description = "Created by TripItEx WriteSurfaceTests; deleted at the end of the test.",
            IsPrivate = true,
        });
        _out.WriteLine($"created trip {trip.Uuid}");
        try
        {
            Assert.True(trip.IsPrivate);

            // ── Trip update ───────────────────────────────────────────────────
            var renamed = await client.Trips.UpdateAsync(trip.Uuid!, t => t with { Description = "updated" });
            Assert.Equal("updated", renamed.Description);
            Assert.Equal(trip.DisplayName, renamed.DisplayName);
            Assert.True(renamed.IsPrivate);

            // ── One plan of every writable type ───────────────────────────────
            var created = new Dictionary<TripItObjectType, PlanWriteResult>();
            foreach (var (type, fields) in SamplePlans())
            {
                var result = await client.Objects.CreateAsync(type, fields, trip.Uuid);
                _out.WriteLine($"created {type} {result.Uuid} warnings: {string.Join(" | ", result.Warnings)}");
                Assert.Equal(trip.Uuid, result.TripUuid);
                created[type] = result;
            }

            var detail = await client.Trips.GetAsync(trip.Uuid!);
            foreach (var type in created.Keys)
            {
                Assert.True(detail.Objects.ContainsKey(type), $"trip is missing its {type}");
            }
            var air = detail.Objects[TripItObjectType.Air].Single();
            Assert.Equal("America/Los_Angeles", air.GetProperty("Segment")[0].GetProperty("StartDateTime").GetProperty("timezone").GetString());

            // ── Update: merge a field, keep the rest ──────────────────────────
            var lodgingUuid = created[TripItObjectType.Lodging].Uuid!;
            var updated = await client.Objects.UpdateAsync(TripItObjectType.Lodging, lodgingUuid,
                new JsonObject { ["room_type"] = "Suite", ["notes"] = "updated by test" });
            Assert.Equal("Suite", updated.Object.GetProperty("room_type").GetString());
            Assert.Equal("ZZ Test Hotel", updated.Object.GetProperty("supplier_name").GetString());
            Assert.Equal("15:00:00", updated.Object.GetProperty("StartDateTime").GetProperty("time").GetString());

            // ── Update one flight leg by segment uuid ─────────────────────────
            var airUuid = created[TripItObjectType.Air].Uuid!;
            var legs = created[TripItObjectType.Air].Object.GetProperty("Segment");
            var secondLeg = legs[1].GetProperty("uuid").GetString()!;
            var seated = await client.Objects.UpdateAsync(TripItObjectType.Air, airUuid,
                new JsonObject { ["seats"] = "14C" }, segmentUuid: secondLeg);
            var seatedLegs = Json.TripItJson.AsList(seated.Object, "Segment");
            Assert.Equal(2, seatedLegs.Count);
            Assert.Contains(seatedLegs, l => Json.TripItJson.Str(l, "seats") == "14C");
            Assert.Contains(seatedLegs, l => Json.TripItJson.Str(l, "seats") == "12A");

            // ── Delete one flight leg, keep the reservation ───────────────────
            var legToDelete = Json.TripItJson.AsList(seated.Object, "Segment")
                .First(l => Json.TripItJson.Str(l, "seats") == "14C").GetProperty("uuid").GetString()!;
            await client.Objects.DeleteSegmentAsync(legToDelete);
            var oneLeg = await client.Objects.GetAsync(TripItObjectType.Air, airUuid);
            Assert.Single(Json.TripItJson.AsList(oneLeg, "Segment"));

            // ── Move a note to Unfiled Items and back ─────────────────────────
            var noteUuid = created[TripItObjectType.Note].Uuid!;
            var unfiled = await client.Objects.MoveAsync(TripItObjectType.Note, noteUuid, tripUuid: null);
            Assert.Null(unfiled.TripUuid);
            var back = await client.Objects.MoveAsync(TripItObjectType.Note, noteUuid, trip.Uuid);
            Assert.Equal(trip.Uuid, back.TripUuid);

            // ── Delete one object ─────────────────────────────────────────────
            await client.Objects.DeleteAsync(TripItObjectType.Map, created[TripItObjectType.Map].Uuid!);
            var afterDelete = await client.Trips.GetAsync(trip.Uuid!);
            Assert.False(afterDelete.Objects.ContainsKey(TripItObjectType.Map));
        }
        finally
        {
            await client.Trips.DeleteAsync(trip.Uuid!);
            _out.WriteLine($"deleted trip {trip.Uuid}");
        }

        await Assert.ThrowsAsync<TripItApiException>(() => client.Trips.GetAsync(trip.Uuid!));
    }

    /// <summary>One minimal-but-realistic plan per writable type, all inside the trip's dates.</summary>
    internal static IEnumerable<(TripItObjectType, JsonObject)> SamplePlans()
    {
        JsonObject Dt(string date, string? time = null) => time is null
            ? new JsonObject { ["date"] = date }
            : new JsonObject { ["date"] = date, ["time"] = time };

        yield return (TripItObjectType.Air, new JsonObject
        {
            ["supplier_conf_num"] = "ZZAIR1",
            ["booking_site_name"] = "Icelandair",
            ["is_purchased"] = false,
            ["Segment"] = new JsonArray(
                new JsonObject
                {
                    ["marketing_airline_code"] = "FI", ["marketing_flight_number"] = "680",
                    ["start_airport_code"] = "SEA", ["end_airport_code"] = "KEF", ["seats"] = "12A",
                    ["StartDateTime"] = Dt("2027-02-08", "14:35"), ["EndDateTime"] = Dt("2027-02-09", "06:15"),
                },
                new JsonObject
                {
                    ["marketing_airline_code"] = "FI", ["marketing_flight_number"] = "681",
                    ["start_airport_code"] = "KEF", ["end_airport_code"] = "SEA", ["seats"] = "12A",
                    ["StartDateTime"] = Dt("2027-02-11", "17:00"), ["EndDateTime"] = Dt("2027-02-11", "17:35"),
                }),
            ["Traveler"] = new JsonObject { ["first_name"] = "Test", ["last_name"] = "Traveler" },
        });
        yield return (TripItObjectType.Lodging, new JsonObject
        {
            ["supplier_name"] = "ZZ Test Hotel", ["supplier_conf_num"] = "ZZHTL1", ["total_cost"] = "$1.00",
            ["StartDateTime"] = Dt("2027-02-09", "15:00"), ["EndDateTime"] = Dt("2027-02-11", "11:00"),
            ["Address"] = new JsonObject { ["address"] = "Laugavegur 1, Reykjavik, Iceland" },
            ["number_guests"] = 1,
        });
        yield return (TripItObjectType.Car, new JsonObject
        {
            ["supplier_name"] = "ZZ Test Cars", ["supplier_conf_num"] = "ZZCAR1",
            ["StartDateTime"] = Dt("2027-02-09", "08:00"), ["EndDateTime"] = Dt("2027-02-11", "12:00"),
            ["start_location_name"] = "KEF Airport", ["end_location_name"] = "KEF Airport", ["car_type"] = "Compact",
        });
        yield return (TripItObjectType.Rail, new JsonObject
        {
            ["supplier_conf_num"] = "ZZRAIL1",
            ["Segment"] = new JsonObject
            {
                ["carrier_name"] = "ZZ Test Rail", ["train_number"] = "1",
                ["start_station_name"] = "Station A", ["end_station_name"] = "Station B",
                ["StartDateTime"] = Dt("2027-02-09", "10:00"), ["EndDateTime"] = Dt("2027-02-09", "11:00"),
            },
        });
        yield return (TripItObjectType.Transport, new JsonObject
        {
            ["supplier_name"] = "ZZ Test Shuttle",
            ["Segment"] = new JsonObject
            {
                ["detail_type_code"] = "G", ["carrier_name"] = "ZZ Test Shuttle",
                ["start_location_name"] = "KEF Airport", ["end_location_name"] = "Hotel",
                ["StartDateTime"] = Dt("2027-02-09", "07:00"), ["EndDateTime"] = Dt("2027-02-09", "07:45"),
            },
        });
        yield return (TripItObjectType.Cruise, new JsonObject
        {
            ["ship_name"] = "ZZ Test Ship", ["supplier_name"] = "ZZ Test Cruises",
            ["Segment"] = new JsonArray(
                new JsonObject { ["location_name"] = "Reykjavik", ["StartDateTime"] = Dt("2027-02-10", "09:00"), ["EndDateTime"] = Dt("2027-02-10", "10:00") },
                new JsonObject { ["location_name"] = "Videy", ["StartDateTime"] = Dt("2027-02-10", "11:00"), ["EndDateTime"] = Dt("2027-02-10", "12:00") }),
        });
        yield return (TripItObjectType.Restaurant, new JsonObject
        {
            ["supplier_name"] = "ZZ Test Restaurant", ["DateTime"] = Dt("2027-02-09", "19:30"), ["number_patrons"] = 2,
        });
        yield return (TripItObjectType.Activity, new JsonObject
        {
            ["display_name"] = "ZZ Test Tour", ["StartDateTime"] = Dt("2027-02-10", "13:00"),
            ["EndDateTime"] = Dt("2027-02-10", "16:00"), ["location_name"] = "Golden Circle",
        });
        yield return (TripItObjectType.Parking, new JsonObject
        {
            ["supplier_name"] = "ZZ Test Parking", ["StartDateTime"] = Dt("2027-02-08", "11:00"),
            ["EndDateTime"] = Dt("2027-02-11", "20:00"), ["location_name"] = "SEA Garage",
        });
        yield return (TripItObjectType.Note, new JsonObject
        {
            ["display_name"] = "ZZ Test Note", ["text"] = "Created by TripItEx tests.", ["DateTime"] = Dt("2027-02-09"),
        });
        yield return (TripItObjectType.Map, new JsonObject
        {
            ["display_name"] = "ZZ Test Map", ["Address"] = new JsonObject { ["address"] = "Hallgrimskirkja, Reykjavik, Iceland" },
            ["DateTime"] = Dt("2027-02-10"),
        });
        yield return (TripItObjectType.Directions, new JsonObject
        {
            ["display_name"] = "ZZ Test Directions", ["DateTime"] = Dt("2027-02-10", "09:00"),
            ["StartAddress"] = new JsonObject { ["address"] = "Keflavik Airport, Iceland" },
            ["EndAddress"] = new JsonObject { ["address"] = "Reykjavik, Iceland" },
        });
    }
}
