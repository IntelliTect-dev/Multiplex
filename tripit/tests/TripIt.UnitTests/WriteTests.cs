using System.Text.Json;
using System.Text.Json.Nodes;
using TripItMcp;
using Xunit;

namespace TripIt.UnitTests;

public sealed class PlanPayloadTests
{
    [Fact]
    public void PreparePlan_orders_fields_as_the_web_serializer_does()
    {
        // TripIt validates the XML derived from the JSON against an ordered XSD: the
        // same fields in another order answered 400 "JSON did not validate with the xsd".
        var fields = new JsonObject
        {
            ["Traveler"] = new JsonObject { ["last_name"] = "User", ["first_name"] = "Test" },
            ["Segment"] = new JsonObject
            {
                ["EndDateTime"] = new JsonObject { ["time"] = "06:15", ["date"] = "2027-01-19" },
                ["start_airport_code"] = "SEA",
                ["marketing_flight_number"] = "680",
            },
            ["is_purchased"] = false,
            ["supplier_conf_num"] = "X1",
            ["trip_uuid"] = Fixtures.TripUuid,
        };

        var payload = TripItWrite.PreparePlan(TripItObjectType.Air, fields);

        Assert.Equal(["trip_uuid", "supplier_conf_num", "is_purchased", "Segment", "Traveler"], payload.Select(kv => kv.Key));
        var seg = payload["Segment"]![0]!.AsObject();
        Assert.Equal(["marketing_flight_number", "start_airport_code", "EndDateTime"], seg.Select(kv => kv.Key));
        Assert.Equal(["date", "time", "is_timezone_manual"], seg["EndDateTime"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["first_name", "last_name"], payload["Traveler"]![0]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void PreparePlan_stringifies_scalars_and_normalizes_dates_and_times()
    {
        var payload = TripItWrite.PreparePlan(TripItObjectType.Lodging, new JsonObject
        {
            ["is_purchased"] = true,
            ["number_guests"] = 2,
            ["StartDateTime"] = new JsonObject { ["date"] = "2027-01-18", ["time"] = "3:00 pm" },
            ["EndDateTime"] = new JsonObject { ["date"] = "2027-01-21", ["time"] = "11:00", ["timezone"] = "Atlantic/Reykjavik" },
        });

        Assert.Equal("true", (string?)payload["is_purchased"]);
        Assert.Equal("2", (string?)payload["number_guests"]);
        Assert.Equal("2027-01-18", (string?)payload["StartDateTime"]!["date"]);
        Assert.Equal("15:00:00", (string?)payload["StartDateTime"]!["time"]);
        Assert.Equal("false", (string?)payload["StartDateTime"]!["is_timezone_manual"]);
        Assert.Equal("true", (string?)payload["EndDateTime"]!["is_timezone_manual"]);
    }

    [Fact]
    public void PreparePlan_rejects_unknown_fields_with_the_valid_list()
    {
        var ex = Assert.Throws<ArgumentException>(() => TripItWrite.PreparePlan(TripItObjectType.Lodging,
            new JsonObject { ["hotel_name"] = "x", ["Address"] = new JsonObject { ["street"] = "y" } }));
        Assert.Contains("hotel_name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Address.street", ex.Message, StringComparison.Ordinal);
        Assert.Contains("supplier_name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePlan_rejects_unknown_segment_fields()
    {
        var ex = Assert.Throws<ArgumentException>(() => TripItWrite.PreparePlan(TripItObjectType.Air,
            new JsonObject { ["Segment"] = new JsonArray(new JsonObject { ["gate"] = "B4" }) }));
        Assert.Contains("Segment.gate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePlan_refuses_weather()
        => Assert.Throws<ArgumentException>(() => TripItWrite.PreparePlan(TripItObjectType.Weather, new JsonObject()));

    [Theory]
    [InlineData("14:35", "14:35:00")]
    [InlineData("9:05", "09:05:00")]
    [InlineData("3:00 pm", "15:00:00")]
    [InlineData("06:15:00", "06:15:00")]
    public void NormalizeTime_accepts_common_forms(string input, string expected)
        => Assert.Equal(expected, TripItWrite.NormalizeTime(input));

    [Fact]
    public void NormalizeDate_rejects_garbage()
        => Assert.Throws<ArgumentException>(() => TripItWrite.NormalizeDate("next tuesday"));

    [Fact]
    public void DeepMerge_merges_objects_replaces_scalars_and_removes_nulls()
    {
        var target = JsonNode.Parse("""{"a":"1","d":{"date":"2027-01-01","time":"10:00:00"},"n":"x"}""")!.AsObject();
        TripItWrite.DeepMerge(target, JsonNode.Parse("""{"a":"2","d":{"time":"16:00:00"},"n":null}""")!.AsObject());
        Assert.Equal("""{"a":"2","d":{"date":"2027-01-01","time":"16:00:00"}}""", target.ToJsonString());
    }

    [Fact]
    public void ToWritable_drops_read_only_fields_and_keeps_segments_in_order()
    {
        using var doc = JsonDocument.Parse(Fixtures.TripWithObjects);
        var air = doc.RootElement.GetProperty("AirObject");

        var writable = PlanSchema.ToWritable(TripItObjectType.Air, air);

        Assert.False(writable.ContainsKey("id"));
        Assert.False(writable.ContainsKey("uuid"));
        Assert.Equal(Fixtures.TripUuid, (string?)writable["trip_uuid"]);
        var segs = writable["Segment"]!.AsArray();
        Assert.Equal(2, segs.Count);
        Assert.Equal("seg-2", (string?)segs[0]!["uuid"]);
        Assert.False(segs[0]!.AsObject().ContainsKey("Status"));
        Assert.IsType<JsonArray>(writable["Traveler"]); // single traveler object -> array
    }

    [Fact]
    public void Every_writable_type_has_a_schema_and_description()
    {
        foreach (var t in TripItObjectTypes.All.Where(t => t != TripItObjectType.Weather))
        {
            Assert.False(string.IsNullOrWhiteSpace(PlanSchema.For(t).Summary));
            Assert.Contains("trip_uuid", PlanSchema.Describe(t), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TripInput_serializes_like_the_web_app()
    {
        var json = new TripInput
        {
            DisplayName = "N", PrimaryLocation = "Reykjavik, Iceland", StartDate = "2027-01-18",
            EndDate = "2027-01-21", Description = "D", IsPrivate = true,
        }.ToPayload().ToJsonString();

        // Captured from the SPA's own replace/trip request.
        Assert.Equal(
            """{"Trip":{"is_owner_traveler":"true","description":"D","display_name":"N","primary_location":"Reykjavik, Iceland","start_date":"2027-01-18","end_date":"2027-01-21","is_private":"true","TripPurposes":[{"purpose_type_code":"L"}]}}""",
            json);
    }
}

public sealed class WriteClientTests
{
    [Fact]
    public async Task Trip_create_posts_payload_and_parses_trip()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle);

        var trip = await client.Trips.CreateAsync(new TripInput
        {
            PrimaryLocation = "Lisbon, Portugal", StartDate = "2027-03-01", EndDate = "2027-03-05",
        });

        Assert.Equal(Fixtures.TripUuid, trip.Uuid);
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("\"primary_location\":\"Lisbon, Portugal\"", req.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trip_create_validates_dates_locally()
    {
        var (client, handler, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => client.Trips.CreateAsync(new TripInput
        {
            PrimaryLocation = "X", StartDate = "2027-03-05", EndDate = "2027-03-01",
        }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Trip_update_reads_current_values_and_changes_only_what_was_asked()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}", Fixtures.ListTripSingle)
               .OnJson($"/api/v2/replace/trip/uuid/{Fixtures.TripUuid}", Fixtures.ListTripSingle);

        await client.Trips.UpdateAsync(Fixtures.TripUuid, t => t with { Description = "new" });

        var body = JsonNode.Parse(handler.Requests[^1].Body!)!["Trip"]!;
        Assert.Equal("new", (string?)body["description"]);
        Assert.Equal("Test Trip to Lisbon", (string?)body["display_name"]);
        Assert.Equal("2027-03-01", (string?)body["start_date"]);
        Assert.Equal("false", (string?)body["is_private"]);
    }

    [Fact]
    public async Task Trip_delete_posts_to_delete_path()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/delete/trip/uuid/{Fixtures.TripUuid}", """{"timestamp":"1"}""");

        await client.Trips.DeleteAsync(Fixtures.TripUuid);

        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task Object_create_wraps_in_envelope_key_and_returns_warnings()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create", $$$"""{"Warning":{"description":"heads up"},"LodgingObject":{"uuid":"{{{Fixtures.LodgingUuid}}}","trip_uuid":"{{{Fixtures.TripUuid}}}"}}""");

        var result = await client.Objects.CreateAsync(TripItObjectType.Lodging,
            new JsonObject { ["supplier_name"] = "Hotel Example" }, Fixtures.TripUuid);

        Assert.Equal(Fixtures.LodgingUuid, result.Uuid);
        Assert.Equal(["heads up"], result.Warnings);
        var sent = JsonNode.Parse(handler.Requests.Single().Body!)!["LodgingObject"]!.AsObject();
        Assert.Equal(["trip_uuid", "supplier_name"], sent.Select(kv => kv.Key));
    }

    [Fact]
    public async Task Object_update_fetches_merges_and_replaces_full_writable_state()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/lodging/uuid/{Fixtures.LodgingUuid}", Fixtures.TripWithObjects)
               .OnJson($"/api/v2/replacePlan/lodging/uuid/{Fixtures.LodgingUuid}",
                   $$$"""{"LodgingObject":{"uuid":"{{{Fixtures.LodgingUuid}}}"}}""");

        await client.Objects.UpdateAsync(TripItObjectType.Lodging, Fixtures.LodgingUuid,
            new JsonObject { ["room_type"] = "Suite", ["StartDateTime"] = new JsonObject { ["time"] = "16:00" } });

        var sent = JsonNode.Parse(handler.Requests[^1].Body!)!["LodgingObject"]!;
        Assert.Equal("Suite", (string?)sent["room_type"]);
        Assert.Equal("Hotel Example", (string?)sent["supplier_name"]);
        Assert.Equal("2027-03-02", (string?)sent["StartDateTime"]!["date"]);
        Assert.Equal("16:00:00", (string?)sent["StartDateTime"]!["time"]);
        Assert.Null(sent["relative_url"]);
    }

    [Fact]
    public async Task Object_update_of_one_segment_leaves_other_legs_alone()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/air/uuid/{Fixtures.AirUuid}", Fixtures.TripWithObjects)
               .OnJson($"/api/v2/replacePlan/air/uuid/{Fixtures.AirUuid}", $$$"""{"AirObject":{"uuid":"{{{Fixtures.AirUuid}}}"}}""");

        await client.Objects.UpdateAsync(TripItObjectType.Air, Fixtures.AirUuid, new JsonObject { ["seats"] = "1A" }, segmentUuid: "seg-1");

        var segs = JsonNode.Parse(handler.Requests[^1].Body!)!["AirObject"]!["Segment"]!.AsArray();
        Assert.Equal("12A", (string?)segs.Single(s => (string?)s!["uuid"] == "seg-2")!["seats"]);
        Assert.Equal("1A", (string?)segs.Single(s => (string?)s!["uuid"] == "seg-1")!["seats"]);
    }

    [Fact]
    public async Task Object_update_rejects_unknown_segment()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/air/uuid/{Fixtures.AirUuid}", Fixtures.TripWithObjects);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Objects.UpdateAsync(TripItObjectType.Air,
            Fixtures.AirUuid, new JsonObject { ["seats"] = "1A" }, segmentUuid: "nope"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Move_to_unfiled_sends_empty_trip_uuid()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/move/note/uuid/{Fixtures.LodgingUuid}", """{"NoteObject":{"uuid":"n"}}""");

        var result = await client.Objects.MoveAsync(TripItObjectType.Note, Fixtures.LodgingUuid, tripUuid: null);

        Assert.Null(result.TripUuid);
        Assert.Equal("?trip_uuid=", handler.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Object_list_clamps_page_size_to_TripIt_maximum()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/object", """{"timestamp":"1"}""");
        await client.Objects.ListAsync(unfiledOnly: true, pageSize: 500);
        Assert.Contains("page_size=100", handler.Requests.Single().Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flight_lookup_computes_arrival_date_and_treats_400_as_not_found()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/flightInfo",
                """{"FlightInfo":{"orig_airport_code":"SEA","dest_airport_code":"KEF","departure_time":"14:35:00","arrival_time":"06:15:00","arrival_date_diff":"1"}}""")
               .On("/api/v2/flightInfo", _ => FakeHandler.Json("""{"Error":{"code":400,"description":""}}""", System.Net.HttpStatusCode.BadRequest));

        var found = await client.Lookup.FlightAsync("fi", "680", "2027-01-18");
        var missing = await client.Lookup.FlightAsync("FI", "9999", "2027-01-18");

        Assert.Equal("2027-01-19", found!.ArrivalDate);
        Assert.Equal("FI", found.AirlineCode);
        Assert.Null(missing);
    }
}

public sealed class McpWriteToolsTests
{
    [Fact]
    public async Task AddFlight_fills_route_and_times_from_schedule_lookup()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/flightInfo",
                """{"FlightInfo":{"orig_airport_code":"SEA","dest_airport_code":"KEF","departure_time":"14:35:00","arrival_time":"06:15:00","arrival_date_diff":"1"}}""")
               .OnJson("/api/v2/create", $$$"""{"AirObject":{"uuid":"{{{Fixtures.AirUuid}}}"}}""");

        await new TripItWriteTools(client).AddFlight(
            """[{"airline_code":"fi","flight_number":680,"date":"2027-01-18","seats":"12A"}]""",
            tripUuid: Fixtures.TripUuid, confirmation: "ABC");

        var seg = JsonNode.Parse(handler.Requests[^1].Body!)!["AirObject"]!["Segment"]![0]!;
        Assert.Equal("FI", (string?)seg["marketing_airline_code"]);
        Assert.Equal("680", (string?)seg["marketing_flight_number"]);
        Assert.Equal("SEA", (string?)seg["start_airport_code"]);
        Assert.Equal("KEF", (string?)seg["end_airport_code"]);
        Assert.Equal("2027-01-19", (string?)seg["EndDateTime"]!["date"]);
        Assert.Equal("06:15:00", (string?)seg["EndDateTime"]!["time"]);
    }

    [Fact]
    public async Task AddFlight_without_schedule_requires_airports()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.On("/api/v2/flightInfo", _ => FakeHandler.Json("""{"Error":{"code":400,"description":""}}""", System.Net.HttpStatusCode.BadRequest));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new TripItWriteTools(client).AddFlight(
            """[{"airline_code":"ZZ","flight_number":"1","date":"2027-01-18"}]"""));
        Assert.Contains("'from' and 'to'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteTrip_refuses_when_confirm_name_does_not_match()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new TripItWriteTools(client).DeleteTrip(Fixtures.TripUuid, "Some other trip"));

        Assert.Contains("Test Trip to Lisbon", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DeleteTrip_deletes_when_name_matches()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects)
               .OnJson($"/api/v2/delete/trip/uuid/{Fixtures.TripUuid}", """{"timestamp":"1"}""");

        var json = await new TripItWriteTools(client).DeleteTrip(Fixtures.TripUuid, "Test Trip to Lisbon");

        Assert.Contains("\"plans_deleted\": 2", json, StringComparison.Ordinal);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreateTrip_is_private_unless_sharing_requested()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle);

        await new TripItWriteTools(client).CreateTrip("Lisbon, Portugal", "2027-03-01", "2027-03-05", purpose: "business");

        var trip = JsonNode.Parse(handler.Requests.Single(r => r.Uri.AbsolutePath == "/api/v2/create/trip").Body!)!["Trip"]!;
        Assert.Equal("true", (string?)trip["is_private"]);
        Assert.Equal("B", (string?)trip["TripPurposes"]![0]!["purpose_type_code"]);
    }

    [Fact]
    public async Task UpdatePlan_with_segment_rejects_non_segment_fields()
    {
        var (client, _, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new TripItWriteTools(client)
            .UpdatePlan("air", Fixtures.AirUuid, """{"supplier_conf_num":"X"}""", segmentUuid: "seg-1"));
    }

    [Fact]
    public void DescribePlanFields_lists_segment_fields_for_rail()
    {
        var json = new TripItTools(FakeTripIt.Create().Client).DescribePlanFields("rail");
        Assert.Contains("train_number", json, StringComparison.Ordinal);
        Assert.Contains("Traveler", json, StringComparison.Ordinal);
    }
}
