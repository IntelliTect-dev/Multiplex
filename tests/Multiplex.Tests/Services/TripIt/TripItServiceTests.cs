using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Tests.SignIn;
using Multiplex.Web.Services.TripIt;

namespace Multiplex.Tests.Services.TripIt;

/// <summary>
/// The TripIt service at <c>/tripit/mcp</c>, driven with real MCP JSON-RPC over HTTP against
/// a fake TripIt: tool list and annotations, per-account sessions, refresh on rejection,
/// sign-out, the confirm step, and short error messages.
/// </summary>
public sealed class TripItServiceTests : IDisposable
{
    private const string TripUuid = "11111111-aaaa-9000-0001-000000000001";
    private const string OtherTripUuid = "11111111-aaaa-9000-0001-000000000009";

    private static readonly string[] s_confirmTools =
    [
        "tripit_delete_trip", "tripit_delete_plan", "tripit_delete_segment",
        "tripit_merge_trips", "tripit_remove_participant", "tripit_add_participants",
    ];

    private readonly TripItTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static string TripJson(string name, int plans = 2)
    {
        var notes = string.Join(",", Enumerable.Range(1, plans).Select(i =>
            "{\"uuid\":\"n" + i + "\",\"trip_uuid\":\"" + TripUuid + "\",\"display_name\":\"Note " + i + "\"}"));
        const string invitees =
            "\"TripInvitees\":{\"Invitee\":{\"@attributes\":{\"profile_ref\":\"ref-cookie-a\"},\"is_owner\":\"true\",\"is_traveler\":\"true\",\"is_read_only\":\"false\"}}";
        return "{\"Trip\":{\"uuid\":\"" + TripUuid + "\",\"display_name\":\"" + name +
               "\",\"start_date\":\"2027-01-18\",\"end_date\":\"2027-01-21\",\"primary_location\":\"Reykjavik, Iceland\"," +
               invitees + "},\"NoteObject\":[" + notes + "]}";
    }

    [Fact]
    public async Task Tools_list_serves_the_TripIt_tools_with_every_hint_and_no_raw_get()
    {
        _host.SignIn("acct-a", "cookie-a");
        var mcp = _host.McpFor("acct-a");
        await mcp.InitializeAsync();

        var tools = await mcp.ListToolsAsync();
        var names = tools.Select(t => t.GetProperty("name").GetString()!).ToList();

        Assert.DoesNotContain("tripit_raw_get", names);
        Assert.DoesNotContain("get_local_time", names);
        Assert.Equal(29, names.Count); // spec section 6: 14 read, 9 write, 6 write-with-confirm
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            var hints = tool.GetProperty("annotations");
            foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
            {
                Assert.True(hints.TryGetProperty(hint, out _), $"{name} does not declare {hint}");
            }
            var readOnly = hints.GetProperty("readOnlyHint").GetBoolean();
            if (readOnly)
            {
                Assert.False(hints.GetProperty("destructiveHint").GetBoolean(), name);
                Assert.False(hints.GetProperty("openWorldHint").GetBoolean(), name);
            }
            var confirm = s_confirmTools.Contains(name);
            Assert.Equal(confirm, hints.GetProperty("destructiveHint").GetBoolean());
            Assert.Equal(confirm, tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("confirm_token", out _));
        }
        foreach (var read in new[] { "tripit_describe_plan_fields", "tripit_lookup_flight", "tripit_get_trip", "tripit_session_status" })
        {
            Assert.True(tools.Single(t => t.GetProperty("name").GetString() == read).GetProperty("annotations")
                .GetProperty("readOnlyHint").GetBoolean(), read);
        }
    }

    [Fact]
    public async Task Read_tool_returns_the_claimed_accounts_data()
    {
        _host.SignIn("acct-a", "cookie-a");

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_get_profile");

        Assert.False(isError, text);
        Assert.Equal("user-cookie-a", JsonDocument.Parse(text).RootElement.GetProperty("screen_name").GetString());
        Assert.All(_host.TripIt.Requests, r => Assert.Equal("cookie-a", r.Cookie));
    }

    [Fact]
    public async Task Two_accounts_each_get_their_own_session()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.SignIn("acct-b", "cookie-b");
        _host.TripIt.OnJson("/api/v2/list/trip", cookie =>
            $$$"""{"Trip":{"uuid":"{{{TripUuid}}}","display_name":"Trip of {{{cookie}}}","start_date":"2027-01-18","end_date":"2027-01-21"},"page_num":"1","max_page":"1"}""");

        var callA = _host.McpFor("acct-a").CallAsync("tripit_list_trips");
        var callB = _host.McpFor("acct-b").CallAsync("tripit_list_trips");
        var (a, b) = (await callA, await callB);

        Assert.Contains("Trip of cookie-a", a.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-b", a.Text, StringComparison.Ordinal);
        Assert.Contains("Trip of cookie-b", b.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-a", b.Text, StringComparison.Ordinal);
    }

    [ChromiumFact]
    public async Task Two_connections_signed_in_on_the_connect_page_as_different_accounts_each_list_only_their_own_trips()
    {
        // No account list and no saved-account shortcut: each connection signs in live, and is bound to that account.
        await using var site = await StubLoginSite.StartAsync();
        using var host = new TripItTestHost(signInSite: site.BaseUrl);
        // Each trip is named after the session it was asked with (the token part of the cookie).
        host.TripIt.OnJson("/api/v2/list/trip", cookie =>
            $$$"""{"Trip":{"uuid":"{{{TripUuid}}}","display_name":"Trip of {{{cookie[(cookie.IndexOf('=') + 1)..]}}}","start_date":"2027-01-18","end_date":"2027-01-21"},"page_num":"1","max_page":"1"}""");

        var first = await host.ConnectThroughPagesAsync(StubLoginSite.Email, StubLoginSite.Password);
        var second = await host.ConnectThroughPagesAsync(StubLoginSite.OtherEmail, StubLoginSite.OtherPassword);
        var a = await first.CallAsync("tripit_list_trips");
        var b = await second.CallAsync("tripit_list_trips");

        var sessionA = (await host.StoredCookieAsync(StubLoginSite.AccountId)).Split('=')[1];
        var sessionB = (await host.StoredCookieAsync(StubLoginSite.OtherAccountId)).Split('=')[1];
        Assert.NotEqual(sessionA, sessionB);
        Assert.False(a.IsError, a.Text);
        Assert.False(b.IsError, b.Text);
        Assert.Contains("Trip of " + sessionA, a.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionB, a.Text, StringComparison.Ordinal);
        Assert.Contains("Trip of " + sessionB, b.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionA, b.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_session_is_refreshed_once_and_the_request_retried()
    {
        _host.SignIn("acct-a", "old");
        _host.TripIt.Rejected.Add("old");
        _host.Refresher.Next["acct-a"] = () => TripItTestHost.Snapshot("new");

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_get_profile");

        Assert.False(isError, text);
        Assert.Contains("user-new", text, StringComparison.Ordinal);
        Assert.Equal(1, _host.Refresher.Calls["acct-a"]);
        Assert.Equal(["old", "new"], _host.TripIt.Requests.Select(r => r.Cookie));
        Assert.Contains("\"new\"", _host.Store["acct-a"], StringComparison.Ordinal);

        // The next request starts from the stored, refreshed snapshot: no second refresh.
        Assert.False((await _host.McpFor("acct-a").CallAsync("tripit_get_profile")).IsError);
        Assert.Equal(1, _host.Refresher.Calls["acct-a"]);
    }

    [Fact]
    public async Task Refresh_that_yields_the_same_cookie_is_not_retried_and_cools_down()
    {
        _host.SignIn("acct-a", "stale");
        _host.TripIt.Rejected.Add("stale");
        _host.Refresher.Next["acct-a"] = () => TripItTestHost.Snapshot("stale");

        var first = await _host.McpFor("acct-a").CallAsync("tripit_get_profile");
        var second = await _host.McpFor("acct-a").CallAsync("tripit_get_profile");

        Assert.True(first.IsError);
        Assert.Contains("could not be renewed", first.Text, StringComparison.Ordinal);
        Assert.True(second.IsError);
        Assert.Equal(1, _host.Refresher.Calls["acct-a"]);
        Assert.Equal(2, _host.TripIt.Requests.Count);
    }

    [Fact]
    public async Task Signed_out_account_gets_the_reconnect_error()
    {
        _host.SignIn("acct-a", "expired");
        _host.TripIt.Rejected.Add("expired");
        _host.Refresher.Next["acct-a"] = () => null;

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_list_trips");

        Assert.True(isError);
        Assert.Contains("Reconnect the TripIt connector", text, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_without_a_token_gets_HTTP_401_before_calling_TripIt()
    {
        var (status, _) = await _host.McpFor(null).SendAsync("tools/call", new { name = "tripit_get_profile", arguments = new { } });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Empty(_host.TripIt.Requests);
    }

    [Fact]
    public async Task TripIt_error_bodies_are_not_echoed_to_the_client()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.On("/api/v2/get/profile", (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>stack trace with internals " + new string('x', 600) + "</html>"),
        });

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_get_profile");

        Assert.True(isError);
        Assert.Contains("HTTP 500", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stack trace", text, StringComparison.Ordinal);
        Assert.True(text.Length < 200, text);
    }

    [Fact]
    public async Task Delete_trip_previews_without_a_token_and_deletes_with_it()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland", plans: 7));
        _host.TripIt.OnJson($"/api/v2/delete/trip/uuid/{TripUuid}", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["tripUuid"] = TripUuid, ["confirmName"] = "Iceland" };

        var (previewError, previewText) = await mcp.CallAsync("tripit_delete_trip", args);

        Assert.False(previewError, previewText);
        var preview = JsonDocument.Parse(previewText).RootElement;
        Assert.True(preview.GetProperty("confirmation_required").GetBoolean());
        Assert.Equal("Will permanently delete trip 'Iceland' (2027-01-18–2027-01-21) and its 7 plans.",
            preview.GetProperty("preview").GetString());
        Assert.Equal(0, _host.TripIt.Count("POST", $"/api/v2/delete/trip/uuid/{TripUuid}"));

        args["confirm_token"] = preview.GetProperty("confirm_token").GetString();
        var (deleteError, deleteText) = await mcp.CallAsync("tripit_delete_trip", args);

        Assert.False(deleteError, deleteText);
        Assert.Equal(1, _host.TripIt.Count("POST", $"/api/v2/delete/trip/uuid/{TripUuid}"));
    }

    [Fact]
    public async Task Write_answered_302_is_not_repeated_and_the_person_is_asked_to_check()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.Refresher.Next["acct-a"] = () => TripItTestHost.Snapshot("cookie-a2");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));
        _host.TripIt.On($"/api/v2/delete/trip/uuid/{TripUuid}", (_, _) => new HttpResponseMessage(HttpStatusCode.Redirect));
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["tripUuid"] = TripUuid, ["confirmName"] = "Iceland" };
        var (_, previewText) = await mcp.CallAsync("tripit_delete_trip", args);
        args["confirm_token"] = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var (isError, text) = await mcp.CallAsync("tripit_delete_trip", args);

        Assert.True(isError);
        Assert.Contains("may already have made the change", text, StringComparison.Ordinal);
        Assert.Contains("before trying again", text, StringComparison.Ordinal);
        Assert.Equal(1, _host.TripIt.Count("POST", $"/api/v2/delete/trip/uuid/{TripUuid}"));
        Assert.Equal(1, _host.Refresher.Calls["acct-a"]);
    }

    [Fact]
    public async Task Confirm_token_is_single_use_so_invitations_are_not_emailed_twice()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}", _ => TripJson("Iceland"));
        _host.TripIt.OnJson("/api/v2/invite", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["tripUuid"] = TripUuid, ["emails"] = "a@example.com", ["role"] = "viewer" };
        var (_, previewText) = await mcp.CallAsync("tripit_add_participants", args);
        args["confirm_token"] = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var first = await mcp.CallAsync("tripit_add_participants", args);
        var replay = await mcp.CallAsync("tripit_add_participants", args);

        Assert.False(first.IsError, first.Text);
        Assert.True(replay.IsError);
        Assert.Contains("already been used", replay.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", replay.Text, StringComparison.Ordinal);
        Assert.Equal(1, _host.TripIt.Count("POST", "/api/v2/invite"));
    }

    [Fact]
    public async Task Confirm_token_cannot_delete_twice()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));
        _host.TripIt.OnJson($"/api/v2/delete/trip/uuid/{TripUuid}", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["tripUuid"] = TripUuid, ["confirmName"] = "Iceland" };
        var (_, previewText) = await mcp.CallAsync("tripit_delete_trip", args);
        args["confirm_token"] = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        Assert.False((await mcp.CallAsync("tripit_delete_trip", args)).IsError);
        var (isError, text) = await mcp.CallAsync("tripit_delete_trip", args);

        Assert.True(isError);
        Assert.Contains("already been used", text, StringComparison.Ordinal);
        Assert.Equal(1, _host.TripIt.Count("POST", $"/api/v2/delete/trip/uuid/{TripUuid}"));
    }

    [Fact]
    public async Task Confirm_token_presented_with_other_arguments_is_not_used_up()
    {
        // A refused mismatch must not burn the token: the right request can still use it.
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));
        _host.TripIt.OnJson($"/api/v2/delete/trip/uuid/{TripUuid}", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var (_, previewText) = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = TripUuid, confirmName = "Iceland" });
        var token = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var wrong = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = TripUuid, confirmName = "Iceland (changed)", confirm_token = token });
        var right = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = TripUuid, confirmName = "Iceland", confirm_token = token });

        Assert.True(wrong.IsError);
        Assert.False(right.IsError, right.Text);
        Assert.Equal(1, _host.TripIt.Count("POST", $"/api/v2/delete/trip/uuid/{TripUuid}"));
    }

    private const string AirUuid = "22222222-bbbb-9000-0004-000000000002";
    private const string LegOut = "44444444-dddd-9000-0005-000000000001";
    private const string LegBack = "44444444-dddd-9000-0005-000000000002";

    private static string AirJson(params string[] legs)
    {
        var segments = string.Join(",", legs.Select(leg => leg == LegOut
            ? "{\"uuid\":\"" + LegOut + "\",\"StartDateTime\":{\"date\":\"2027-03-01\",\"time\":\"18:00:00\"},\"start_airport_code\":\"JFK\",\"end_airport_code\":\"LIS\",\"marketing_airline_code\":\"EX\",\"marketing_flight_number\":\"1\"}"
            : "{\"uuid\":\"" + LegBack + "\",\"StartDateTime\":{\"date\":\"2027-03-05\",\"time\":\"10:00:00\"},\"start_airport_code\":\"LIS\",\"end_airport_code\":\"JFK\",\"marketing_airline_code\":\"EX\",\"marketing_flight_number\":\"2\"}"));
        return "{\"AirObject\":{\"uuid\":\"" + AirUuid + "\",\"trip_uuid\":\"" + TripUuid +
               "\",\"supplier_conf_num\":\"ABC123\",\"booking_site_name\":\"Example Air\",\"Segment\":[" + segments + "]}}";
    }

    [Fact]
    public async Task Delete_segment_requires_the_plan_type_and_uuid()
    {
        _host.SignIn("acct-a", "cookie-a");
        var mcp = _host.McpFor("acct-a");
        await mcp.InitializeAsync();
        var tool = (await mcp.ListToolsAsync()).Single(t => t.GetProperty("name").GetString() == "tripit_delete_segment");
        var required = tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();

        var (isError, _) = await mcp.CallAsync("tripit_delete_segment", new { segmentUuid = LegOut });

        Assert.Contains("type", required);
        Assert.Contains("objectUuid", required);
        Assert.True(isError);
        Assert.DoesNotContain(_host.TripIt.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Delete_segment_preview_names_the_plan_and_the_leg()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/air/uuid/{AirUuid}", _ => AirJson(LegOut, LegBack));

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_delete_segment",
            new { segmentUuid = LegOut, type = "air", objectUuid = AirUuid });

        Assert.False(isError, text);
        var preview = JsonDocument.Parse(text).RootElement.GetProperty("preview").GetString()!;
        Assert.Contains(AirUuid, preview, StringComparison.Ordinal);
        Assert.Contains("ABC123", preview, StringComparison.Ordinal);
        Assert.Contains("Flight EX 1 JFK → LIS", preview, StringComparison.Ordinal);
        Assert.Contains("1 other leg", preview, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.TripIt.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Delete_segment_deletes_the_leg_after_checking_it_is_still_in_the_plan()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/air/uuid/{AirUuid}", _ => AirJson(LegOut, LegBack));
        _host.TripIt.OnJson($"/api/v2/delete/segment/uuid/{LegOut}", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["segmentUuid"] = LegOut, ["type"] = "air", ["objectUuid"] = AirUuid };
        var (_, previewText) = await mcp.CallAsync("tripit_delete_segment", args);
        args["confirm_token"] = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var (isError, text) = await mcp.CallAsync("tripit_delete_segment", args);

        Assert.False(isError, text);
        Assert.Equal(2, _host.TripIt.Count("GET", $"/api/v2/get/air/uuid/{AirUuid}"));
        Assert.Equal(1, _host.TripIt.Count("POST", $"/api/v2/delete/segment/uuid/{LegOut}"));
    }

    [Fact]
    public async Task Delete_segment_refuses_a_leg_that_is_no_longer_in_the_previewed_plan()
    {
        _host.SignIn("acct-a", "cookie-a");
        var reads = 0;
        // The leg is in the plan at preview time, and gone (moved or deleted elsewhere) at confirm time.
        _host.TripIt.OnJson($"/api/v2/get/air/uuid/{AirUuid}", _ => Interlocked.Increment(ref reads) == 1 ? AirJson(LegOut, LegBack) : AirJson(LegBack));
        _host.TripIt.OnJson($"/api/v2/delete/segment/uuid/{LegOut}", _ => """{"timestamp":"1"}""");
        var mcp = _host.McpFor("acct-a");
        var args = new Dictionary<string, object?> { ["segmentUuid"] = LegOut, ["type"] = "air", ["objectUuid"] = AirUuid };
        var (_, previewText) = await mcp.CallAsync("tripit_delete_segment", args);
        args["confirm_token"] = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var (isError, text) = await mcp.CallAsync("tripit_delete_segment", args);

        Assert.True(isError);
        Assert.Contains("is not a leg of", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", text, StringComparison.Ordinal);
        Assert.Equal(0, _host.TripIt.Count("POST", $"/api/v2/delete/segment/uuid/{LegOut}"));
    }

    [Fact]
    public async Task Confirm_token_is_refused_for_other_arguments_or_another_account()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.SignIn("acct-b", "cookie-b");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{OtherTripUuid}/include_objects/true", _ => TripJson("Iceland"));
        var (_, previewText) = await _host.McpFor("acct-a").CallAsync("tripit_delete_trip",
            new { tripUuid = TripUuid, confirmName = "Iceland" });
        var token = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var tampered = await _host.McpFor("acct-a").CallAsync("tripit_delete_trip",
            new { tripUuid = OtherTripUuid, confirmName = "Iceland", confirm_token = token });
        var otherAccount = await _host.McpFor("acct-b").CallAsync("tripit_delete_trip",
            new { tripUuid = TripUuid, confirmName = "Iceland", confirm_token = token });
        var otherTool = await _host.McpFor("acct-a").CallAsync("tripit_delete_plan",
            new { type = "note", uuid = TripUuid, confirm_token = token });
        var garbage = await _host.McpFor("acct-a").CallAsync("tripit_delete_trip",
            new { tripUuid = TripUuid, confirmName = "Iceland", confirm_token = "not-a-token" });

        foreach (var (isError, text) in new[] { tampered, otherAccount, otherTool })
        {
            Assert.True(isError);
            Assert.Contains("issued for a different request", text, StringComparison.Ordinal);
        }
        Assert.True(garbage.IsError);
        Assert.Contains("invalid or has expired", garbage.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.TripIt.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Expired_confirm_token_is_refused()
    {
        // Tokens issued "10 minutes ago" are past their 5-minute lifetime.
        var clock = new ShiftedClock(TimeSpan.FromMinutes(-10));
        _host.Clock = clock;
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));
        var mcp = _host.McpFor("acct-a");
        var (_, previewText) = await mcp.CallAsync("tripit_delete_trip", new { tripUuid = TripUuid, confirmName = "Iceland" });
        var token = JsonDocument.Parse(previewText).RootElement.GetProperty("confirm_token").GetString();

        var (isError, text) = await mcp.CallAsync("tripit_delete_trip",
            new { tripUuid = TripUuid, confirmName = "Iceland", confirm_token = token });

        Assert.True(isError);
        Assert.Contains("expired", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.TripIt.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Confirm_name_is_still_checked_in_the_preview()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}/include_objects/true", _ => TripJson("Iceland"));

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_delete_trip",
            new { tripUuid = TripUuid, confirmName = "Wrong" });

        Assert.True(isError);
        Assert.Contains("The trip is named 'Iceland'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitations_preview_names_the_addresses_and_role_before_any_email()
    {
        _host.SignIn("acct-a", "cookie-a");
        _host.TripIt.OnJson($"/api/v2/get/trip/uuid/{TripUuid}", _ => TripJson("Iceland"));

        var (isError, text) = await _host.McpFor("acct-a").CallAsync("tripit_add_participants",
            new { tripUuid = TripUuid, emails = "a@example.com, b@example.com", role = "editor" });

        Assert.False(isError, text);
        var preview = JsonDocument.Parse(text).RootElement.GetProperty("preview").GetString();
        Assert.Contains("a@example.com, b@example.com", preview, StringComparison.Ordinal);
        Assert.Contains("as editor", preview, StringComparison.Ordinal);
        Assert.Equal(0, _host.TripIt.Count("POST", "/api/v2/invite"));
    }

    [Fact]
    public void TripIt_http_client_paces_and_has_no_cookie_jar_or_redirects()
    {
        // The real wiring, not the fake: a shared cookie jar would mix accounts, and a followed
        // 302 would hide a rotated session.
        using var host = new TripItTestHost(fakeTripIt: false);
        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(TripItServiceRegistration.HttpClientName);

        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = handler; h is not null; h = (h as DelegatingHandler)?.InnerHandler) chain.Add(h);

        Assert.Single(chain.OfType<global::TripIt.Http.PacingHandler>());
        var primary = Assert.IsType<SocketsHttpHandler>(chain[^1]);
        Assert.False(primary.UseCookies);
        Assert.False(primary.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.All, primary.AutomaticDecompression);
    }

    [Fact]
    public void Confirm_token_hash_ignores_argument_order_and_distinguishes_values()
    {
        var a = ConfirmTokens.HashArguments(new Dictionary<string, string?> { ["x"] = "1", ["y"] = null });
        var b = ConfirmTokens.HashArguments(new Dictionary<string, string?> { ["y"] = null, ["x"] = "1" });
        var c = ConfirmTokens.HashArguments(new Dictionary<string, string?> { ["x"] = "1", ["y"] = "" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }
}
