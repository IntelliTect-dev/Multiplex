using System.Net;
using Multiplex.Tests.Identity;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// Who may go on with a connection: only the person, signed in to Multiplex, who started it at
/// <c>/oauth/authorize</c>. No browser is involved.
/// </summary>
public sealed class ConnectGateTests : IAsyncLifetime
{
    private SignInTestHost _host = null!;

    public Task InitializeAsync()
    {
        // No browser: a sign-in that is started fails at launch, which these tests do not mind.
        _host = new SignInTestHost(new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<ConnectClient> SignedInAsync(string email = TestPeople.Owner)
    {
        var client = _host.NewClient();
        await client.SignInAsync(email);
        return client;
    }

    /// <summary>
    /// The friend's own connect page: a page they may open, so their posts carry a valid antiforgery
    /// token and are refused only for whose connection it is.
    /// </summary>
    private async Task<(ConnectClient Friend, string Page)> FriendWithAPageAsync()
    {
        _host.Completer.Add("p-friend", "stub", owner: TestPeople.OwnerOf(TestPeople.Friend));
        var friend = await SignedInAsync(TestPeople.Friend);
        var (response, page) = await friend.GetAsync("/connect/stub?p=p-friend");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(ConnectClient.TokenOf(page));
        return (friend, page);
    }

    private static void AssertNotAllowed(HttpResponseMessage response, string html)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Not allowed", html);
    }

    [Fact]
    public async Task Connect_page_names_the_client_the_site_and_the_person()
    {
        _host.Completer.Add("p1", "stub", clientName: "Claude");

        var (response, html) = await (await SignedInAsync()).GetAsync("/connect/stub?p=p1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<strong>Claude</strong> wants to use your <strong>Stub Site</strong> account, for <strong>owner@example.test</strong>", html);
        Assert.Contains("action=\"/connect/stub/start?p=p1\"", html);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Sign_in_page_warns_that_the_password_goes_through_this_server()
    {
        _host.Completer.Add("p1", "stub");

        var (_, html) = await (await SignedInAsync()).GetAsync("/connect/stub?p=p1");

        Assert.Contains("""<p class="warning">What you type there, including your password, goes through this Multiplex server on its way to Stub Site.""", html);
        Assert.DoesNotContain("password is not stored by this server", html);
    }

    [Fact]
    public async Task Live_page_warns_beside_the_text_box_every_time_something_is_sent()
    {
        _host.Completer.Add("p1", "stub");
        var client = await SignedInAsync();
        var (started, _) = await client.StartSignInAsync("p1");

        var (_, html) = await client.GetAsync(started.Headers.Location!.OriginalString);

        // Always shown, never dismissed, in the bar with the text box, and read out with the box each time it is focused.
        const string warning = """<p id="type-warning" class="warning">Anything you send here, passwords included, goes through this Multiplex server.""";
        Assert.Contains(warning, html);
        Assert.InRange(html.IndexOf(warning, StringComparison.Ordinal), html.IndexOf("id=\"txt\"", StringComparison.Ordinal), html.IndexOf("id=\"stage\"", StringComparison.Ordinal));
        Assert.Matches("""<input id="txt"[^>]* aria-describedby="type-warning"[^>]*>""", html);
    }

    [Fact]
    public async Task Client_name_is_html_encoded()
    {
        _host.Completer.Add("p1", "stub", clientName: "<script>alert(1)</script>");

        var (_, html) = await (await SignedInAsync()).GetAsync("/connect/stub?p=p1");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Theory]
    [InlineData("/connect/stub?p=unknown")]
    [InlineData("/connect/stub")]
    public async Task Unknown_or_missing_pending_id_shows_a_friendly_error(string path)
    {
        var (response, html) = await (await SignedInAsync()).GetAsync(path);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        Assert.Contains("Go back to the app", html);
        Assert.DoesNotContain("/connect/stub/start", html);
    }

    [Fact]
    public async Task Expired_pending_authorization_is_refused()
    {
        _host.Completer.Add("old", "stub", lifetime: TimeSpan.FromSeconds(-1));

        var (response, html) = await (await SignedInAsync()).GetAsync("/connect/stub?p=old");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("expired", html);
    }

    [Fact]
    public async Task Pending_authorization_for_another_site_is_refused()
    {
        _host.Completer.Add("p1", "tripit");

        var (response, _) = await (await SignedInAsync()).GetAsync("/connect/stub?p=p1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_refused()
    {
        _host.Completer.Add("p1", "stub");
        var client = await SignedInAsync();

        var response = await client.Http.PostAsync("/connect/stub/start?p=p1", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_host.Get<LiveSignInRegistry>().FindForPending("p1"));
    }

    [Fact]
    public async Task Signed_in_owner_sees_the_sign_in_page_and_starting_it_opens_the_live_page()
    {
        _host.Completer.Add("p1", "stub");
        var client = await SignedInAsync();

        var (page, html) = await client.GetAsync("/connect/stub?p=p1");
        var (started, _) = await client.StartSignInAsync("p1");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Sign in to Stub Site", html);
        Assert.Equal(HttpStatusCode.SeeOther, started.StatusCode);
        Assert.StartsWith("/connect/stub/live?a=", started.Headers.Location!.OriginalString);
        Assert.Equal("no-store", started.Headers.CacheControl?.ToString());
        var attempt = LiveMirrorTests.Attempt(_host, started);
        Assert.Equal("p1", attempt.PendingId);
        Assert.Equal(TestPeople.OwnerOf(), attempt.Owner);
        var (live, _) = await client.GetAsync(started.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Another_person_cannot_view_start_or_cancel_a_pending_authorization()
    {
        _host.Completer.Add("p1", "stub", clientName: "Claude");
        var (friend, friendPage) = await FriendWithAPageAsync();

        var (view, viewHtml) = await friend.GetAsync("/connect/stub?p=p1");
        var (start, startHtml) = await friend.PostAsync("/connect/stub/start?p=p1", friendPage);
        var (cancel, cancelHtml) = await friend.PostAsync("/connect/stub/cancel?p=p1", friendPage);

        AssertNotAllowed(view, viewHtml);
        Assert.DoesNotContain("Claude", viewHtml);
        AssertNotAllowed(start, startHtml);
        AssertNotAllowed(cancel, cancelHtml);
        Assert.Null(_host.Get<LiveSignInRegistry>().FindForPending("p1"));
        Assert.Empty(_host.TempProfiles());
        Assert.Empty(_host.Completer.Failed);
        Assert.NotNull(await _host.Completer.FindAsync("p1", CancellationToken.None));

        // The person who started it still can.
        var (owners, _) = await (await SignedInAsync()).GetAsync("/connect/stub?p=p1");
        Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
    }

    [Fact]
    public async Task Another_person_cannot_open_the_live_page_websocket_or_done_of_a_sign_in()
    {
        _host.Completer.Add("p1", "stub");
        var owner = await SignedInAsync();
        var (started, _) = await owner.StartSignInAsync("p1");
        var live = started.Headers.Location!.OriginalString;
        var attemptId = ConnectClient.AttemptIdOf(started.Headers.Location!);
        var (friend, friendPage) = await FriendWithAPageAsync();

        var (page, pageHtml) = await friend.GetAsync(live);
        var socket = await Record.ExceptionAsync(() => friend.OpenMirrorAsync(attemptId));
        var (done, doneHtml) = await friend.GetAsync($"/connect/stub/done?a={attemptId}");
        var (cancel, cancelHtml) = await friend.PostAsync($"/connect/stub/cancel?a={attemptId}", friendPage);

        AssertNotAllowed(page, pageHtml);
        Assert.NotNull(socket);
        AssertNotAllowed(done, doneHtml);
        AssertNotAllowed(cancel, cancelHtml);
        Assert.Empty(_host.Completer.Completed);
        Assert.Empty(_host.Completer.Failed);
        Assert.NotEqual(AttemptState.Cancelled, LiveMirrorTests.Attempt(_host, started).State);

        // The person who started it still can.
        var (owners, _) = await owner.GetAsync(live);
        Assert.Equal(HttpStatusCode.OK, owners.StatusCode);
        await using var mirror = await owner.OpenMirrorAsync(attemptId);
    }

    /// <summary>
    /// Every connection belongs to a signed-in person, so the connect pages never let a request without
    /// one through, not even from this PC (the test server counts as this PC). Microsoft is not
    /// configured in the test host, so the answer is the "not set up" page rather than a redirect to it.
    /// </summary>
    [Theory]
    [InlineData("GET", "/connect/stub?p=p1")]
    [InlineData("GET", "/connect/stub/live?a=some-attempt")]
    [InlineData("GET", "/connect/stub/done?a=some-attempt")]
    [InlineData("POST", "/connect/stub/start?p=p1")]
    [InlineData("POST", "/connect/stub/cancel?p=p1")]
    public async Task Without_a_sign_in_to_Multiplex_no_connect_page_is_let_through(string method, string path)
    {
        _host.Completer.Add("p1", "stub", clientName: "Claude");
        var client = _host.NewClient();

        var (response, html) = method == "GET"
            ? await client.GetAsync(path)
            : await client.PostAsync(path, "");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Sign-in is not set up", html);
        Assert.DoesNotContain("Claude", html);
        Assert.Null(_host.Get<LiveSignInRegistry>().FindForPending("p1"));
        Assert.Empty(_host.Completer.Failed);
        Assert.NotNull(await _host.Completer.FindAsync("p1", CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_sign_in_to_Multiplex_the_live_websocket_is_refused()
    {
        _host.Completer.Add("p1", "stub");
        var (started, _) = await (await SignedInAsync()).StartSignInAsync("p1");

        var socket = await Record.ExceptionAsync(() => _host.NewClient().OpenMirrorAsync(ConnectClient.AttemptIdOf(started.Headers.Location!)));

        Assert.NotNull(socket);
    }
}

/// <summary>The live WebSocket accepts pages served by this server only (no cross-site WebSocket hijacking).</summary>
public sealed class LiveSocketOriginTests : IAsyncLifetime
{
    private SignInTestHost _host = null!;

    public Task InitializeAsync()
    {
        var options = new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") };
        options.Settings["Multiplex:PublicBaseUrl"] = "https://mx.example.ts.net";
        _host = new SignInTestHost(options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("http://localhost", true)]           // the page's own origin (Host)
    [InlineData("https://mx.example.ts.net", true)]  // the public URL behind a tunnel that rewrites Host
    [InlineData(null, true)]                         // not a browser; the sign-in cookie and owner check still apply
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost.evil.example", false)]
    public async Task Origin_must_be_this_server(string? origin, bool accepted)
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        await client.SignInAsync();
        var (started, _) = await client.StartSignInAsync("p1");

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var mirror = await client.OpenMirrorAsync(ConnectClient.AttemptIdOf(started.Headers.Location!), origin: origin);
        });

        Assert.Equal(accepted, error is null);
    }
}
