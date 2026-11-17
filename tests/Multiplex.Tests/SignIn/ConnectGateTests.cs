using System.Net;

namespace Multiplex.Tests.SignIn;

/// <summary>The passcode gate in front of every connect flow. No browser is involved.</summary>
public sealed class ConnectGateTests : IAsyncLifetime
{
    private SignInTestHost _host = null!;

    public Task InitializeAsync()
    {
        // No browser: a sign-in started by a passed gate fails at launch, which these tests do not mind.
        _host = new SignInTestHost(new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Gate_page_names_the_client_and_the_site()
    {
        _host.Completer.Add("p1", "stub", clientName: "Claude");

        var (response, html) = await _host.NewClient().GetAsync("/connect/stub?p=p1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<strong>Claude</strong> wants to connect to your <strong>Stub Site</strong> account", html);
        Assert.Contains("name=\"passcode\"", html);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Client_name_is_html_encoded()
    {
        _host.Completer.Add("p1", "stub", clientName: "<script>alert(1)</script>");

        var (_, html) = await _host.NewClient().GetAsync("/connect/stub?p=p1");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Theory]
    [InlineData("/connect/stub?p=unknown")]
    [InlineData("/connect/stub")]
    public async Task Unknown_or_missing_pending_id_shows_a_friendly_error(string path)
    {
        var (response, html) = await _host.NewClient().GetAsync(path);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        Assert.Contains("Go back to the app", html);
        Assert.DoesNotContain("name=\"passcode\"", html);
    }

    [Fact]
    public async Task Expired_pending_authorization_is_refused()
    {
        _host.Completer.Add("old", "stub", lifetime: TimeSpan.FromSeconds(-1));

        var (response, html) = await _host.NewClient().GetAsync("/connect/stub?p=old");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("expired", html);
    }

    [Fact]
    public async Task Pending_authorization_for_another_site_is_refused()
    {
        _host.Completer.Add("p1", "tripit");

        var (response, _) = await _host.NewClient().GetAsync("/connect/stub?p=p1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_passcode_is_refused_and_the_form_shown_again()
    {
        _host.Completer.Add("p1", "stub");

        var (response, html) = await _host.NewClient().PassGateAsync("p1", "not-the-passcode");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("That passcode is not right", html);
        Assert.Contains("name=\"passcode\"", html);
        Assert.Empty(_host.TempProfiles());
    }

    [Fact]
    public async Task Five_wrong_passcodes_lock_the_gate_even_for_the_right_one()
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PassGateAsync("p1", "wrong-" + i)).Response.StatusCode);
        var (fifth, _) = await client.PassGateAsync("p1", "wrong-5");
        var (afterLock, html) = await client.PassGateAsync("p1");

        Assert.Equal((HttpStatusCode)429, fifth.StatusCode);
        Assert.Equal((HttpStatusCode)429, afterLock.StatusCode);
        Assert.Contains("15 minutes", html);
        Assert.Empty(_host.Completer.Completed);
    }

    [Fact]
    public async Task Lockout_also_applies_per_client_address_across_pending_ids()
    {
        var client = _host.NewClient();
        for (var i = 0; i < 5; i++)
        {
            _host.Completer.Add("p" + i, "stub");
            await client.PassGateAsync("p" + i, "wrong");
        }
        _host.Completer.Add("fresh", "stub");

        var (response, _) = await client.PassGateAsync("fresh");

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_refused()
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var response = await client.Http.PostAsync("/connect/stub?p=p1",
            new FormUrlEncodedContent([new("passcode", SignInTestHost.Passcode)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Right_passcode_sets_an_httponly_strict_gate_cookie()
    {
        _host.Completer.Add("p1", "stub");

        var (response, _) = await _host.NewClient().PassGateAsync("p1");

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mx_gate_stub=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/connect/stub", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_page_and_websocket_need_the_gate_cookie_of_that_pending_authorization()
    {
        _host.Completer.Add("p1", "stub");
        var owner = _host.NewClient();
        var (passed, _) = await owner.PassGateAsync("p1");
        var live = passed.Headers.Location!;

        var stranger = _host.NewClient();
        var (page, _) = await stranger.GetAsync(live.OriginalString);
        var socket = await Record.ExceptionAsync(() => stranger.OpenMirrorAsync(ConnectClient.AttemptIdOf(live)));

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
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
    [InlineData(null, true)]                         // not a browser; the gate cookie still applies
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost.evil.example", false)]
    public async Task Origin_must_be_this_server(string? origin, bool accepted)
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var mirror = await client.OpenMirrorAsync(ConnectClient.AttemptIdOf(gate.Headers.Location!), origin: origin);
        });

        Assert.Equal(accepted, error is null);
    }
}

/// <summary>Without a configured passcode hash the gate fails closed.</summary>
public sealed class ConnectGateNotConfiguredTests : IAsyncLifetime
{
    private SignInTestHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new SignInTestHost(new SignInTestOptions
        {
            WithPasscode = false,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe"),
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Without_a_passcode_hash_nobody_can_connect()
    {
        _host.Completer.Add("p1", "stub");
        var client = _host.NewClient();

        var (page, html) = await client.GetAsync("/connect/stub?p=p1");
        var (post, _) = await client.PassGateAsync("p1", "");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Contains("install script", html);
        Assert.DoesNotContain("name=\"passcode\"", html);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        Assert.Empty(_host.TempProfiles());
    }
}
