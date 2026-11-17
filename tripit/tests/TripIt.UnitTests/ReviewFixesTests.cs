using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TripIt.Tools;
using TripItMcp;
using Xunit;

namespace TripIt.UnitTests;

public sealed class SessionSnapshotTests
{
    [Fact]
    public void FromSnapshotJson_reads_cookie_csrf_headers_and_capture_time()
    {
        var session = TripItSession.FromSnapshotJson(
            """{"cookie":"a=1","csrf_token":"t","spa_headers":{"User-Agent":"UA","n":5},"captured_at":"2026-11-17T08:13:34.000Z","profile_dir":"x"}""",
            "test");

        Assert.Equal("a=1", session.Cookie);
        Assert.Equal("t", session.CsrfToken);
        Assert.Equal("UA", session.SpaHeaders["user-agent"]);
        Assert.False(session.SpaHeaders.ContainsKey("n"));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 8, 13, 34, TimeSpan.Zero), session.CapturedAt);
        Assert.Equal("test", session.Source);
    }

    [Theory]
    [InlineData("""{"cookie":"secret-value","csrf_token":""", "not valid JSON")]
    [InlineData("""{"csrf_token":"secret-value"}""", "no 'cookie'")]
    [InlineData("""{"cookie":42}""", "'cookie' must be a string")]
    [InlineData("""["secret-value"]""", "must be a JSON object")]
    public void FromSnapshotJson_explains_failures_without_values(string json, string reason)
    {
        var ex = Assert.Throws<TripItSessionFormatException>(() => TripItSession.FromSnapshotJson(json));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_never_prints_the_cookie_or_token()
    {
        var text = new TripItSession("session_id=SECRET", "CSRFSECRET", source: "s").ToString();
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_session_is_not_retried_when_the_renewed_cookie_is_the_same()
    {
        var handler = new FakeHandler();
        handler.On("/api/v2/get/profile", _ => FakeHandler.Html(Fixtures.AkamaiBlockPage, HttpStatusCode.Forbidden));
        var session = new TripItSession("same", "");
        var renewals = 0;
        var client = new TripItClient(new SnapshotSessionProvider(session, (_, _) =>
        {
            renewals++;
            return Task.FromResult<TripItSession?>(new TripItSession("same", "other-csrf"));
        }), new HttpClient(handler));

        await Assert.ThrowsAsync<TripItAuthenticationException>(() => client.GetProfileAsync());

        Assert.Equal(1, renewals);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Local_refresher_runs_at_most_once_per_cooldown()
    {
        FakeRefresher? refresher = null;
        var (client, handler, _) = FakeTripIt.Create("old", s => refresher = new FakeRefresher(s, "old"));
        handler.On("/api/v2/get/profile", _ => FakeHandler.Html(Fixtures.AkamaiBlockPage, HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<TripItAuthenticationException>(() => client.GetProfileAsync());
        await Assert.ThrowsAsync<TripItAuthenticationException>(() => client.GetProfileAsync());

        // The refresher produced the same cookie: no retry, and no second refresh inside the cool-down.
        Assert.Equal(1, refresher!.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task My_profile_ref_is_cached_per_session_snapshot()
    {
        var handler = new FakeHandler();
        var client = new TripItClient(new SnapshotSessionProvider(new TripItSession("c", "")), new HttpClient(handler));

        Assert.Equal("p1", await client.GetMyProfileRefAsync());
        Assert.Equal("p1", await client.GetMyProfileRefAsync());

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Profile_email_addresses_are_read_from_single_or_list()
    {
        using var one = JsonDocument.Parse("""{"Profile":{"ProfileEmailAddresses":{"ProfileEmailAddress":{"address":"a@example.com"}}}}""");
        using var two = JsonDocument.Parse("""{"Profile":{"ProfileEmailAddresses":{"ProfileEmailAddress":[{"address":"a@example.com"},{"address":"b@example.com"}]}}}""");
        Assert.Equal(["a@example.com"], TripItClient.ProfileEmailAddresses(one.RootElement));
        Assert.Equal(["a@example.com", "b@example.com"], TripItClient.ProfileEmailAddresses(two.RootElement));
    }
}

public sealed class WriteSafetyTests
{
    [Theory]
    [InlineData("03/04/2026")]
    [InlineData("2027-1-18")]
    [InlineData("18.01.2027")]
    [InlineData("Jan 18 2027")]
    public void NormalizeDate_accepts_ISO_only(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => TripItWrite.NormalizeDate(value));
        Assert.Contains("yyyy-MM-dd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trip_with_slash_dates_is_rejected_before_any_request()
    {
        var (client, handler, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => client.Trips.CreateAsync(new TripInput
        {
            PrimaryLocation = "X", StartDate = "03/04/2027", EndDate = "2027-03-05",
        }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_then_replace_failure_reports_the_created_trip_uuid()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle)
               .OnJson($"/api/v2/replace/trip/uuid/{Fixtures.TripUuid}", """{"Error":{"code":"500","description":"boom"}}""",
                   HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<TripItPartialWriteException>(() => client.Trips.CreateAsync(new TripInput
        {
            PrimaryLocation = "Lisbon", StartDate = "2027-03-01", EndDate = "2027-03-05", IsOwnerTraveler = false,
        }));

        Assert.Equal(Fixtures.TripUuid, ex.CreatedUuid);
        Assert.Contains(Fixtures.TripUuid, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Do not create it again", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, TripItErrorText.ForTool(ex));
    }

    [Fact]
    public async Task Segment_array_that_omits_an_existing_leg_is_refused()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/air/uuid/{Fixtures.AirUuid}", Fixtures.TripWithObjects);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.Objects.UpdateAsync(TripItObjectType.Air,
            Fixtures.AirUuid, new JsonObject { ["Segment"] = new JsonArray(new JsonObject { ["uuid"] = "seg-1", ["seats"] = "1A" }) }));

        Assert.Contains("seg-2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("would be deleted", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Segment_null_is_refused_when_legs_exist()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/air/uuid/{Fixtures.AirUuid}", Fixtures.TripWithObjects);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Objects.UpdateAsync(TripItObjectType.Air,
            Fixtures.AirUuid, new JsonObject { ["Segment"] = null }));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Segment_array_naming_every_leg_merges_each_into_its_leg()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/air/uuid/{Fixtures.AirUuid}", Fixtures.TripWithObjects)
               .OnJson($"/api/v2/replacePlan/air/uuid/{Fixtures.AirUuid}", $$$"""{"AirObject":{"uuid":"{{{Fixtures.AirUuid}}}"}}""");

        await client.Objects.UpdateAsync(TripItObjectType.Air, Fixtures.AirUuid, new JsonObject
        {
            ["Segment"] = new JsonArray(new JsonObject { ["uuid"] = "seg-1", ["seats"] = "1A" }, new JsonObject { ["uuid"] = "seg-2" }),
        });

        var segs = JsonNode.Parse(handler.Requests[^1].Body!)!["AirObject"]!["Segment"]!.AsArray();
        Assert.Equal(2, segs.Count);
        var first = segs.Single(s => (string?)s!["uuid"] == "seg-1")!;
        Assert.Equal("1A", (string?)first["seats"]);
        Assert.NotNull(first["start_airport_code"]); // kept from the existing leg, not dropped
    }

    [Fact]
    public async Task Segment_patch_cannot_change_the_leg_uuid()
    {
        var (client, _, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new TripItWriteTools(client)
            .UpdatePlan("air", Fixtures.AirUuid, """{"uuid":"other"}""", segmentUuid: "seg-1"));
    }

    [Fact]
    public async Task AddFlight_says_when_the_arrival_date_is_assumed()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.On("/api/v2/flightInfo", _ => FakeHandler.Json("""{"Error":{"code":400,"description":""}}""", HttpStatusCode.BadRequest))
               .OnJson("/api/v2/create", $$$"""{"AirObject":{"uuid":"{{{Fixtures.AirUuid}}}"}}""");

        var json = await new TripItWriteTools(client).AddFlight(
            """[{"airline_code":"ZZ","flight_number":"1","date":"2027-01-18","from":"SEA","to":"KEF","depart_time":"14:35","arrive_time":"06:15"}]""");

        using var doc = JsonDocument.Parse(json);
        var note = doc.RootElement.GetProperty("assumptions")[0].GetString()!;
        Assert.Contains("arrival date is unknown", note, StringComparison.Ordinal);
        Assert.Contains("TripIt has no schedule", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_reference_tools_live_with_the_read_tools()
    {
        var readTools = typeof(TripItTools).GetMethods().Select(m => m.Name).ToList();
        Assert.Contains(nameof(TripItTools.DescribePlanFields), readTools);
        Assert.Contains(nameof(TripItTools.LookupFlight), readTools);
        Assert.DoesNotContain(typeof(TripItWriteTools).GetMethods(), m => m.Name is "DescribePlanFields" or "LookupFlight");
    }
}

public sealed class ErrorTextTests
{
    [Fact]
    public void Api_errors_show_status_and_short_TripIt_message_only()
    {
        var ex = Assert.Throws<TripItApiException>(() => TripItClient.Classify("/p", 400, "application/json",
            """{"Error":{"code":"400","description":"JSON did not validate with the xsd"}}"""));
        Assert.Equal("TripIt rejected the request (HTTP 400): 400: JSON did not validate with the xsd", TripItErrorText.ForTool(ex));
    }

    [Fact]
    public void Non_json_error_bodies_are_never_echoed()
    {
        var body = "<html><body>internal stack trace session_id=SECRET " + new string('x', 500) + "</body></html>";
        var ex = Assert.Throws<TripItApiException>(() => TripItClient.Classify("/p", 500, "text/html", body));

        Assert.DoesNotContain("SECRET", ex.Message, StringComparison.Ordinal);
        Assert.Equal("TripIt could not complete the request (HTTP 500).", TripItErrorText.ForTool(ex));
    }

    [Fact]
    public void Long_or_markup_TripIt_messages_are_dropped()
    {
        var ex = new TripItApiException(400, "/p", "x", new string('a', 500));
        Assert.Equal("TripIt could not complete the request (HTTP 400).", TripItErrorText.ForTool(ex));
    }

    [Fact]
    public void Unexpected_exceptions_are_not_reportable()
        => Assert.Null(TripItErrorText.ForTool(new InvalidOperationException("internal detail")));
}

public sealed class RawGetTests
{
    [Theory]
    [InlineData("/api/v2/../account/edit")]
    [InlineData("/api/v2/%2e%2e/account")]
    [InlineData("/api/v2/%2E%2E/account")]
    [InlineData("/api/v2/move/note/uuid/x?trip_uuid=")]
    [InlineData("/api/v2/list/trip#frag")]
    [InlineData("/api/v2/list\\trip")]
    [InlineData("/api/v2//list/trip")]
    [InlineData("/api/v2/./list/trip")]
    [InlineData("/account/edit")]
    public void Raw_paths_that_could_escape_api_v2_are_rejected(string path)
        => Assert.Throws<ArgumentException>(() => TripItRawTools.ValidatePath(path));

    [Fact]
    public void Plain_api_v2_path_is_accepted()
        => Assert.Equal("/api/v2/list/trip", TripItRawTools.ValidatePath("/api/v2/list/trip"));

    [Fact]
    public void Raw_get_is_not_a_read_tool()
        => Assert.DoesNotContain(typeof(TripItTools).GetMethods(), m => m.Name == "RawGet");
}

public sealed class SharedOperationsTests
{
    [Fact]
    public async Task Invitation_sent_but_participants_not_rereadable_is_reported_not_thrown()
    {
        // An error here would make the caller retry, and TripIt would email the invitation again.
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/invite", """{"timestamp":"1"}""")
               .On($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}", _ => FakeHandler.Json("""{"Error":{"code":"503","description":"busy"}}""", HttpStatusCode.ServiceUnavailable));

        var json = await new TripItOperations(client).AddParticipantsAsync(Fixtures.TripUuid, "a@example.com", "viewer", null, default);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
        Assert.Contains("Do not repeat", doc.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath == "/api/v2/invite");
    }

    [Fact]
    public async Task Create_trip_reads_who_am_I_before_creating()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle);

        await new TripItOperations(client).CreateTripAsync("Lisbon", "2027-03-01", "2027-03-05", null, null, false, "leisure", true, default);

        Assert.Equal(["/api/v2/get/profile", "/api/v2/create/trip"], handler.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Delete_trip_preview_names_trip_dates_and_plan_count_and_changes_nothing()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var preview = await new TripItOperations(client).PreviewDeleteTripAsync(Fixtures.TripUuid, "Test Trip to Lisbon", default);

        Assert.Contains("'Test Trip to Lisbon'", preview, StringComparison.Ordinal);
        Assert.Contains("2 plans", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }
}
