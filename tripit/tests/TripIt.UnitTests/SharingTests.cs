using System.Text.Json;
using System.Text.Json.Nodes;
using TripItMcp;
using Xunit;

namespace TripIt.UnitTests;

public sealed class TravelerFilterTests
{
    [Theory]
    [InlineData(TripTravelerFilter.All, "traveler=all")]
    [InlineData(TripTravelerFilter.Mine, "traveler=true")]
    [InlineData(TripTravelerFilter.Others, "traveler=false")]
    public async Task List_sends_traveler_filter(TripTravelerFilter filter, string expected)
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.ListTripSingle);
        await client.Trips.ListAsync(TripTimeframe.Past, 10, 1, default, filter);
        Assert.Contains(expected, handler.Requests.Single().Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_defaults_to_all_trips_not_TripIts_travelling_only_default()
    {
        // TripIt's own default (no parameter) hides trips the user is on but not travelling on.
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.ListTripSingle);
        await client.Trips.ListAsync();
        Assert.Contains("traveler=all", handler.Requests.Single().Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("others", TripTravelerFilter.Others)]
    [InlineData("MINE", TripTravelerFilter.Mine)]
    [InlineData(null, TripTravelerFilter.All)]
    public void ParseTravelerFilter_accepts_friendly_names(string? input, TripTravelerFilter expected)
        => Assert.Equal(expected, TripsClient.ParseTravelerFilter(input));

    [Fact]
    public async Task Creating_a_trip_I_am_not_travelling_on_follows_up_with_a_replace()
    {
        // Measured: create/trip ignores is_owner_traveler=false; replace/trip honours it.
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle)
               .OnJson($"/api/v2/replace/trip/uuid/{Fixtures.TripUuid}", Fixtures.ListTripSingle);

        await client.Trips.CreateAsync(new TripInput
        {
            PrimaryLocation = "Lisbon, Portugal", StartDate = "2027-03-01", EndDate = "2027-03-05", IsOwnerTraveler = false,
        });

        Assert.Equal(2, handler.Requests.Count);
        var replaced = JsonNode.Parse(handler.Requests[1].Body!)!["Trip"]!;
        Assert.Equal("false", (string?)replaced["is_owner_traveler"]);
    }

    [Fact]
    public async Task Creating_a_trip_I_travel_on_is_a_single_request()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/create/trip", Fixtures.ListTripSingle);
        await client.Trips.CreateAsync(new TripInput { PrimaryLocation = "X", StartDate = "2027-03-01", EndDate = "2027-03-05" });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void TripInput_FromTrip_keeps_owner_not_travelling()
    {
        var trip = JsonSerializer.Deserialize<Models.Trip>(
            """{"uuid":"u","start_date":"2027-01-01","end_date":"2027-01-02","TripInvitees":{"Invitee":{"@attributes":{"profile_ref":"p1"},"is_owner":"true","is_traveler":"false"}}}""",
            Json.TripItJson.Options)!;
        Assert.False(TripInput.FromTrip(trip).IsOwnerTraveler);
    }
}

public sealed class ParticipantTests
{
    [Fact]
    public void Participants_resolve_names_roles_and_me()
    {
        using var doc = JsonDocument.Parse(Fixtures.TripWithObjects);
        var detail = TripsClient.ParseTripDetail(doc.RootElement.Clone());

        var people = TripsClient.Participants(detail, "p1");

        Assert.Equal(2, people.Count);
        Assert.Equal(("Test User", TripRole.Traveler, true, true), (people[0].Name, people[0].Role, people[0].IsOwner, people[0].IsMe));
        Assert.Equal(("Friend User", TripRole.Viewer, false), (people[1].Name, people[1].Role, people[1].IsMe));
    }

    [Fact]
    public void Invite_payload_matches_the_web_app_with_real_booleans()
    {
        var json = TripsClient.BuildInvitePayload(Fixtures.TripUuid, ["a@example.com"], TripRole.Editor, "hi").ToJsonString();
        Assert.Equal(
            $$$"""{"Invitation":{"EmailAddresses":{"email_address":"a@example.com"},"TripShare":{"trip_uuid":"{{{Fixtures.TripUuid}}}","is_read_only":false,"is_traveler":false,"is_sent_with_details":false},"message":"hi"}}""",
            json);
    }

    [Fact]
    public void Change_role_payload_uses_invitee_serializer_with_string_booleans_in_order()
    {
        var json = TripsClient.BuildChangeRolePayload(Fixtures.TripUuid, "ref1", TripRole.Traveler).ToJsonString();
        Assert.Equal(
            $$$"""{"TripInvitations":{"trip_uuid":"{{{Fixtures.TripUuid}}}","Invitees":[{"Invitee":{"@attributes":{"profile_ref":"ref1"},"is_read_only":"false","is_traveler":"true"}}]}}""",
            json);
    }

    [Fact]
    public async Task Invite_rejects_bad_addresses_before_sending()
    {
        var (client, handler, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => client.Trips.InviteAsync(Fixtures.TripUuid, ["not-an-email"], TripRole.Viewer));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remove_participant_posts_to_profile_ref_path()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/delete/trip_participant/trip_uuid/{Fixtures.TripUuid}/profile_ref/abc%3D", """{"timestamp":"1"}""");
        await client.Trips.RemoveParticipantAsync(Fixtures.TripUuid, "abc=");
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task Merge_posts_uuid_and_into_uuid_then_rereads_destination()
    {
        var (client, handler, _) = FakeTripIt.Create();
        const string from = "99999999-aaaa-9000-0001-000000000009";
        handler.OnJson("/api/v2/merge/trip", """{"timestamp":"1"}""")
               .OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var merged = await client.Trips.MergeAsync(from, Fixtures.TripUuid);

        Assert.Equal($"?uuid={from}&into_uuid={Fixtures.TripUuid}", handler.Requests[0].Uri.Query);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(2, merged.ObjectCount);
    }

    [Fact]
    public async Task Merge_into_itself_is_rejected()
    {
        var (client, _, _) = FakeTripIt.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => client.Trips.MergeAsync(Fixtures.TripUuid, Fixtures.TripUuid));
    }

    [Theory]
    [InlineData("view", TripRole.Viewer)]
    [InlineData("Editor", TripRole.Editor)]
    [InlineData("traveller", TripRole.Traveler)]
    public void ParseRole_accepts_synonyms(string input, TripRole expected)
        => Assert.Equal(expected, TripsClient.ParseRole(input));
}

public sealed class McpSharingToolsTests
{
    [Fact]
    public async Task MergeTrips_refuses_when_source_name_does_not_match()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}/include_objects/true", Fixtures.TripWithObjects);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new TripItWriteTools(client)
            .MergeTrips(Fixtures.TripUuid, "99999999-aaaa-9000-0001-000000000009", "wrong name"));

        Assert.Contains("Test Trip to Lisbon", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveParticipant_refuses_the_owner()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}", Fixtures.TripWithObjects);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new TripItWriteTools(client).RemoveParticipant(Fixtures.TripUuid, "p1"));

        Assert.Contains("owner", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AddParticipants_splits_emails_and_sends_role()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/invite", """{"timestamp":"1"}""")
               .OnJson($"/api/v2/get/trip/uuid/{Fixtures.TripUuid}", Fixtures.TripWithObjects);

        await new TripItWriteTools(client).AddParticipants(Fixtures.TripUuid, "a@example.com, b@example.com", "traveler");

        var share = JsonNode.Parse(handler.Requests.First(r => r.Uri.AbsolutePath == "/api/v2/invite").Body!)!["Invitation"]!;
        Assert.Equal(2, share["EmailAddresses"]!["email_address"]!.AsArray().Count);
        Assert.True((bool)share["TripShare"]!["is_traveler"]!);
    }

    [Fact]
    public async Task ListTrips_passes_others_filter_and_reports_my_role()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/list/trip", Fixtures.TripWithObjects);

        using var doc = JsonDocument.Parse(await new TripItTools(client).ListTrips(traveler: "others"));

        Assert.Contains("traveler=false", handler.Requests.First(r => r.Uri.AbsolutePath == "/api/v2/list/trip").Uri.Query, StringComparison.Ordinal);
        Assert.Equal("owner, traveler", doc.RootElement.GetProperty("trips")[0].GetProperty("my_role").GetString());
    }
}
