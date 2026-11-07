using System.Net;
using Xunit;

namespace TripIt.UnitTests;

public sealed class TripItClientTests
{
    [Fact]
    public void Classify_treats_Akamai_403_html_as_authentication_failure()
    {
        // #7: a stale session fails as 403 + HTML block page, not 401.
        var ex = Assert.Throws<TripItAuthenticationException>(
            () => TripItClient.Classify("/api/v2/list/trip", 403, "text/html", Fixtures.AkamaiBlockPage));
        Assert.Contains("extract-session", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    public void Classify_treats_302_and_401_as_authentication_failure(int status)
        => Assert.Throws<TripItAuthenticationException>(
            () => TripItClient.Classify("/p", status, "application/json", "{}"));

    [Fact]
    public void Classify_surfaces_TripIt_error_description()
    {
        var ex = Assert.Throws<TripItApiException>(() => TripItClient.Classify("/p", 400, "application/json",
            """{"Error":{"code":400,"description":"uuid provided is for the wrong type"}}"""));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("wrong type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_rejects_error_envelope_even_with_200()
        => Assert.Throws<TripItApiException>(() => TripItClient.Classify("/p", 200, "application/json",
            """{"Error":{"code":"500","description":"boom"}}"""));

    [Fact]
    public void Classify_rejects_200_html_spa_shell()
        => Assert.Throws<TripItApiException>(() => TripItClient.Classify("/graphql", 200, "text/html",
            "<!doctype html><html></html>"));

    [Fact]
    public void Classify_accepts_json_and_json_arrays()
    {
        TripItClient.Classify("/p", 200, "application/json", """{"Trip":[]}""");
        TripItClient.Classify("/p", 200, "application/json", """[{"id":"SEA","label":"Seattle"}]""");
    }

    [Fact]
    public async Task Send_refreshes_session_and_retries_once_after_Akamai_403()
    {
        FakeRefresher? refresher = null;
        var (client, handler, _) = FakeTripIt.Create("old", store => refresher = new FakeRefresher(store, "new"));
        handler.On("/api/v2/get/profile", _ => FakeHandler.Html(Fixtures.AkamaiBlockPage, HttpStatusCode.Forbidden))
               .On("/api/v2/get/profile", _ => FakeHandler.Json("""{"Profile":{"screen_name":"tester"}}"""));

        var profile = await client.GetProfileAsync();

        Assert.Equal("tester", profile.ScreenName);
        Assert.Equal(1, refresher!.Calls);
        Assert.Equal(["old", "new"], handler.Requests.Select(r => r.Cookie));
    }

    [Fact]
    public async Task Send_picks_up_externally_refreshed_session_file_without_running_refresher()
    {
        FakeRefresher? refresher = null;
        var (client, handler, store) = FakeTripIt.Create("old", s => refresher = new FakeRefresher(s, "never"));
        handler.On("/api/v2/get/profile", _ =>
               {
                   // Simulate the user re-running extract-session.js while the server runs.
                   store.Save(new("fresh", "csrf"));
                   return FakeHandler.Json("x", HttpStatusCode.Unauthorized);
               })
               .On("/api/v2/get/profile", _ => FakeHandler.Json("""{"Profile":{"screen_name":"ok"}}"""));

        await client.GetProfileAsync();

        Assert.Equal(0, refresher!.Calls);
        Assert.Equal("fresh", handler.Requests[^1].Cookie);
    }

    [Fact]
    public async Task Send_without_refresher_throws_authentication_exception()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.On("/api/v2/get/profile", _ => FakeHandler.Html(Fixtures.AkamaiBlockPage, HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<TripItAuthenticationException>(() => client.GetProfileAsync());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_does_not_retry_more_than_once()
    {
        FakeRefresher? refresher = null;
        var (client, handler, _) = FakeTripIt.Create("old", s => refresher = new FakeRefresher(s, "new"));
        handler.On("/api/v2/get/profile", _ => FakeHandler.Json("", HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<TripItAuthenticationException>(() => client.GetProfileAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, refresher!.Calls);
    }

    [Fact]
    public async Task Send_sends_session_cookie_csrf_and_spa_headers()
    {
        var (client, handler, _) = FakeTripIt.Create("session_id=abc");
        HttpRequestMessage? seen = null;
        handler.On("/api/v2/get/profile", r => { seen = r; return FakeHandler.Json("""{"Profile":{}}"""); });

        await client.GetProfileAsync();

        Assert.NotNull(seen);
        Assert.Equal("session_id=abc", seen!.Headers.GetValues("Cookie").Single());
        Assert.Equal("csrf", seen.Headers.GetValues("X-CSRF-Token-WA").Single());
        Assert.Equal("XMLHttpRequest", seen.Headers.GetValues("X-Requested-With").Single());
    }

    [Fact]
    public async Task GetProfile_unwraps_Profile_envelope()
    {
        var (client, handler, _) = FakeTripIt.Create();
        handler.OnJson("/api/v2/get/profile",
            """{"timestamp":"1","Profile":{"screen_name":"tester","public_display_name":"Test User"}}""");

        var profile = await client.GetProfileAsync();

        Assert.Equal("Test User", profile.PublicDisplayName);
    }
}
