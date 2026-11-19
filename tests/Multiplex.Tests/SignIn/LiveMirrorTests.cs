using System.Net;
using System.Text.Json.Serialization;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// The live mirror end to end, against a real Chromium and the stub login site: the test plays
/// the phone, receiving frames and sending taps and text over the connect page's WebSocket.
/// </summary>
[Collection(SignInBrowserCollection.Name)]
public sealed class LiveMirrorTests
{
    private static readonly TimeSpan s_long = TimeSpan.FromSeconds(90);

    [ChromiumFact]
    public async Task Person_signs_in_through_the_mirror_and_the_client_gets_a_code()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub", clientName: "Claude");
        var client = host.NewClient();

        var (gate, _) = await client.PassGateAsync("p1");
        Assert.Equal(HttpStatusCode.SeeOther, gate.StatusCode);
        var (livePage, html) = await client.GetAsync(gate.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, livePage.StatusCode);
        Assert.Contains("id=\"txt\"", html);
        Assert.Contains("Stub hint.", html);

        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);
        await mirror.WaitForFramesAsync(1, s_long);
        Assert.Equal([0xFF, 0xD8], mirror.LastFrame![..2]); // JPEG

        await SignInAsync(attempt, mirror, StubLoginSite.Email, StubLoginSite.Password);

        var ready = await mirror.WaitForStatusAsync("ready", s_long);
        var next = ready.GetProperty("next").GetString()!;
        Assert.StartsWith("/connect/stub/done?a=", next);

        var (done, _) = await client.GetAsync(next);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("https://client.example.test/callback?code=code-for-p1&state=s1", done.Headers.Location!.AbsoluteUri);
        var completed = Assert.Single(host.Completer.Completed);
        Assert.Equal("p1", completed.PendingId);
        Assert.Equal("stub", completed.Account.Site);
        Assert.Equal(StubLoginSite.AccountId, completed.Account.AccountId);
        Assert.Equal(StubLoginSite.DisplayName, completed.Account.DisplayName);
        Assert.Equal([StubLoginSite.Email], completed.Account.Emails);

        // A second visit to the done URL reuses the first code instead of minting another.
        var (again, _) = await client.GetAsync(next);
        Assert.Equal(done.Headers.Location, again.Headers.Location);
        Assert.Single(host.Completer.Completed);

        // Session stored encrypted; temp profile became the account's profile.
        var snapshot = await host.Get<ISiteSessionStore>().GetAsync("stub", StubLoginSite.AccountId, CancellationToken.None);
        Assert.Contains($"{StubLoginSite.CookieName}=", snapshot);
        await attempt.Completion;
        Assert.Empty(host.TempProfiles());
        Assert.True(Directory.Exists(Path.Combine(host.ProfilesDirectory, StubLoginSite.AccountId)));
        Assert.Contains(await host.Get<SiteAccountIndex>().ListAsync("stub", CancellationToken.None), a => a.AccountId == StubLoginSite.AccountId);

        // The kept profile is still signed in: the real refresher reads a fresh snapshot from it.
        var refreshed = await host.Get<ISiteSessionRefresher>().RefreshAsync("stub", StubLoginSite.AccountId, CancellationToken.None);
        Assert.Contains($"{StubLoginSite.CookieName}=", refreshed);
        Assert.Empty(host.Revoker.Revoked);
    }

    [ChromiumFact]
    public async Task Two_connections_sign_in_as_different_accounts_and_each_is_bound_to_its_own()
    {
        // No account list is configured: the passcode is the gate, and each connection gets the account signed in while adding it.
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        host.Completer.Add("p2", "stub");

        // The first connection signs in as the first account.
        var first = host.NewClient();
        var (gate1, _) = await first.PassGateAsync("p1");
        await SignInLiveAsync(host, first, gate1, StubLoginSite.Email, StubLoginSite.Password);

        // The same connector added again, from another client: the first account is not on offer, so it signs in as another.
        var second = host.NewClient();
        var (gate2, _) = await second.PassGateAsync("p2");
        await SignInLiveAsync(host, second, gate2, StubLoginSite.OtherEmail, StubLoginSite.OtherPassword);

        Assert.Equal(
            [("p1", StubLoginSite.AccountId), ("p2", StubLoginSite.OtherAccountId)],
            host.Completer.Completed.Select(c => (c.PendingId, c.Account.AccountId)));
        Assert.Empty(host.Completer.Failed);
        // Each account keeps its own session and its own browser profile.
        var store = host.Get<ISiteSessionStore>();
        var firstSession = await store.GetAsync("stub", StubLoginSite.AccountId, CancellationToken.None);
        var secondSession = await store.GetAsync("stub", StubLoginSite.OtherAccountId, CancellationToken.None);
        Assert.Contains($"{StubLoginSite.CookieName}=", firstSession);
        Assert.Contains($"{StubLoginSite.CookieName}=", secondSession);
        Assert.NotEqual(firstSession, secondSession);
        Assert.True(Directory.Exists(Path.Combine(host.ProfilesDirectory, StubLoginSite.AccountId)));
        Assert.True(Directory.Exists(Path.Combine(host.ProfilesDirectory, StubLoginSite.OtherAccountId)));
        Assert.Empty(host.TempProfiles());
    }

    [ChromiumFact]
    public async Task Navigation_to_a_host_outside_the_allowed_list_is_blocked_and_cancel_cleans_up()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var (_, liveHtml) = await client.GetAsync(gate.Headers.Location!.OriginalString);
        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);

        // The link points at localhost: same server, but not an allowed host.
        var link = await CenterOfAsync(attempt, "#foreign");
        await mirror.SendAsync(new { t = "tap", x = link.X, y = link.Y });

        var blocked = await mirror.WaitForAsync(m => m.GetProperty("t").GetString() == "blocked", s_long, "a blocked navigation");
        Assert.Equal("localhost", blocked.GetProperty("host").GetString());
        // The person is put back on the sign-in page, and the other host never saw a request.
        await WaitForAsync(() => Task.FromResult(attempt.ActivePage!.Url == new Uri(site.BaseUrl, "/login").AbsoluteUri), TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(site.Requests, r => r.StartsWith("/foreign", StringComparison.Ordinal));
        Assert.Equal(AttemptState.Live, attempt.State);

        var (cancel, _) = await client.PostAsync($"/connect/stub/cancel?a={attempt.Id}", liveHtml);
        Assert.Equal(HttpStatusCode.SeeOther, cancel.StatusCode);
        Assert.Contains("error=access_denied", cancel.Headers.Location!.AbsoluteUri);
        Assert.Equal(("p1", "access_denied", "Sign-in cancelled"), Assert.Single(host.Completer.Failed));
        await attempt.Completion.WaitAsync(s_long);
        Assert.Empty(host.TempProfiles());
    }

    [ChromiumFact]
    public async Task New_tab_to_another_host_is_closed_and_new_tab_on_the_site_is_mirrored_until_Back()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);
        var login = attempt.ActivePage!;

        // target=_blank to a host outside the list: the new tab is closed and never mirrored.
        var foreign = await CenterOfAsync(attempt, "#foreign-tab");
        await mirror.SendAsync(new { t = "tap", x = foreign.X, y = foreign.Y });
        var blocked = await mirror.WaitForAsync(m => m.GetProperty("t").GetString() == "blocked", s_long, "a blocked new tab");
        Assert.Equal("localhost", blocked.GetProperty("host").GetString());
        await WaitForAsync(async () => (await login.Browser.PagesAsync()).Length == 1, TimeSpan.FromSeconds(20));
        Assert.Same(login, attempt.ActivePage);
        Assert.DoesNotContain(site.Requests, r => r.StartsWith("/foreign-tab", StringComparison.Ordinal));

        // target=_blank on the site itself: mirrored, and Back closes it and returns to the sign-in page.
        var help = await CenterOfAsync(attempt, "#help-tab");
        await mirror.SendAsync(new { t = "tap", x = help.X, y = help.Y });
        await WaitForAsync(() => Task.FromResult(attempt.ActivePage!.Url.EndsWith("/help", StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        await mirror.SendAsync(new { t = "back" });
        await WaitForAsync(() => Task.FromResult(attempt.ActivePage == login), TimeSpan.FromSeconds(30));
        Assert.Equal(AttemptState.Live, attempt.State);
    }

    [ChromiumFact]
    public async Task Main_page_that_shows_a_non_web_address_is_sent_back_to_the_sign_in_page()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);
        var login = new Uri(site.BaseUrl, "/login").AbsoluteUri;
        await WaitForAsync(() => Task.FromResult(attempt.ActivePage!.Url == login), TimeSpan.FromSeconds(30));

        // A web page cannot open such an address itself (Chrome refuses), so drive the browser directly:
        // this is the lock's second line, for whatever gets past the first.
        try
        {
            await attempt.ActivePage!.GoToAsync("data:text/html,<h1 id='elsewhere'>Somewhere else</h1>",
                new PuppeteerSharp.NavigationOptions { Timeout = 30_000 });
        }
        catch (PuppeteerSharp.PuppeteerException)
        {
            // The lock may already be navigating the page away.
        }

        var blocked = await mirror.WaitForAsync(m => m.GetProperty("t").GetString() == "blocked", s_long, "a blocked page");
        Assert.Equal("another site", blocked.GetProperty("host").GetString());
        await WaitForAsync(() => Task.FromResult(attempt.ActivePage!.Url == login), TimeSpan.FromSeconds(30));
        Assert.Equal(AttemptState.Live, attempt.State);
    }

    [ChromiumFact]
    public async Task Attempt_that_times_out_closes_its_browser_and_deletes_its_profile()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions
        {
            StubBaseUrl = site.BaseUrl,
            AttemptTimeout = TimeSpan.FromSeconds(12),
        });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var attempt = Attempt(host, gate);
        await using var firstTab = await client.OpenMirrorAsync(attempt.Id);
        await firstTab.WaitForStatusAsync("live", s_long);
        Assert.NotEmpty(host.TempProfiles());

        // A second tab takes over; the first is told not to reconnect.
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await WaitForAsync(() => Task.FromResult(firstTab.CloseStatus == MirrorProtocol.ReplacedCloseStatus), TimeSpan.FromSeconds(10));

        var timedOut = await mirror.WaitForStatusAsync("timeout", s_long);

        Assert.Contains("too long", timedOut.GetProperty("msg").GetString());
        await attempt.Completion.WaitAsync(s_long);
        Assert.Equal(AttemptState.TimedOut, attempt.State);
        Assert.Empty(host.TempProfiles());
        Assert.Empty(host.Completer.Completed);
    }

    [ChromiumFact]
    public async Task Wrong_password_keeps_the_attempt_live()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);

        await SignInAsync(attempt, mirror, StubLoginSite.Email, "not-the-password");
        await WaitForAsync(async () => await attempt.ActivePage!.EvaluateExpressionAsync<string>("document.getElementById('error')?.textContent ?? ''") is { Length: > 0 });

        Assert.Equal(AttemptState.Live, attempt.State);
        Assert.Empty(host.Completer.Completed);
        Assert.Empty(host.Completer.Failed);
    }

    [ChromiumFact]
    public async Task Text_typed_on_the_phone_reaches_the_page_as_key_presses()
    {
        // A site's bot protection scores the keystrokes on its sign-in form; text that appears
        // with no key presses is not how a person types (#61).
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        var (gate, _) = await client.PassGateAsync("p1");
        var attempt = Attempt(host, gate);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);
        var email = await CenterOfAsync(attempt, "#email");
        await attempt.ActivePage!.EvaluateExpressionAsync(
            "window.__keys = []; window.__times = []; document.getElementById('email').addEventListener('keydown', e => { window.__keys.push(e.key); window.__times.push(e.timeStamp); }); 0");

        const string typed = "Tester+1@example.test";
        await mirror.SendAsync(new { t = "tap", x = email.X, y = email.Y });
        await mirror.SendAsync(new { t = "text", v = typed });

        await WaitForAsync(async () => await attempt.ActivePage!.EvaluateExpressionAsync<string>("document.getElementById('email').value") == typed);
        Assert.Equal(typed.Select(c => c.ToString()), await attempt.ActivePage!.EvaluateExpressionAsync<string[]>("window.__keys"));
        // At a typing pace, not all at once.
        var times = await attempt.ActivePage!.EvaluateExpressionAsync<double[]>("window.__times");
        var shortestGap = times.Zip(times.Skip(1), (a, b) => b - a).Min();
        Assert.True(shortestGap >= LiveSignInAttempt.KeystrokePause.TotalMilliseconds * 0.8,
            $"Key presses {shortestGap:F0} ms apart; expected about {LiveSignInAttempt.KeystrokePause.TotalMilliseconds} ms.");

        // A character no keyboard key types still arrives.
        var password = await CenterOfAsync(attempt, "#password");
        await mirror.SendAsync(new { t = "tap", x = password.X, y = password.Y });
        await mirror.SendAsync(new { t = "text", v = "pässwörd-€" });
        await WaitForAsync(async () => await attempt.ActivePage!.EvaluateExpressionAsync<string>("document.getElementById('password').value") == "pässwörd-€");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// From the redirect to the live page to the client's code: open the mirror, sign in as
    /// <paramref name="email"/>, collect the code, and wait for the attempt to release its browser.
    /// </summary>
    private static async Task SignInLiveAsync(SignInTestHost host, ConnectClient client, HttpResponseMessage toLive, string email, string password)
    {
        var attempt = Attempt(host, toLive);
        await using var mirror = await client.OpenMirrorAsync(attempt.Id);
        await mirror.WaitForStatusAsync("live", s_long);
        await SignInAsync(attempt, mirror, email, password);
        var ready = await mirror.WaitForStatusAsync("ready", s_long);
        var (done, _) = await client.GetAsync(ready.GetProperty("next").GetString()!);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        await attempt.Completion.WaitAsync(s_long);
    }

    internal static LiveSignInAttempt Attempt(SignInTestHost host, HttpResponseMessage gate)
    {
        Assert.Equal(HttpStatusCode.SeeOther, gate.StatusCode);
        var id = ConnectClient.AttemptIdOf(gate.Headers.Location!);
        return host.Get<LiveSignInRegistry>().Find(id) ?? throw new InvalidOperationException("No attempt " + id);
    }

    /// <summary>What a person does on the phone: tap a field, type, tap the next, type, press Enter.</summary>
    internal static async Task SignInAsync(LiveSignInAttempt attempt, MirrorClient mirror, string email, string password)
    {
        var emailField = await CenterOfAsync(attempt, "#email");
        await mirror.SendAsync(new { t = "tap", x = emailField.X, y = emailField.Y });
        await mirror.SendAsync(new { t = "text", v = email });
        var passwordField = await CenterOfAsync(attempt, "#password");
        await mirror.SendAsync(new { t = "tap", x = passwordField.X, y = passwordField.Y });
        await mirror.SendAsync(new { t = "text", v = password });
        await mirror.SendAsync(new { t = "key", k = "Enter" });
    }

    /// <summary>Where an element's centre is, as a fraction of the mirrored viewport (what the phone page sends).</summary>
    internal static async Task<(double X, double Y)> CenterOfAsync(LiveSignInAttempt attempt, string selector)
    {
        Point? point = null;
        await WaitForAsync(async () =>
        {
            point = await attempt.ActivePage!.EvaluateFunctionAsync<Point?>(
                """
                sel => {
                  const e = document.querySelector(sel);
                  if (!e || document.readyState === 'loading') return null;
                  const r = e.getBoundingClientRect();
                  return { x: (r.left + r.width / 2) / innerWidth, y: (r.top + r.height / 2) / innerHeight };
                }
                """, selector);
            return point is not null;
        });
        return (point!.X, point.Y);
    }

    internal static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? s_long);
        while (true)
        {
            try
            {
                if (await condition()) return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // The page was navigating; try again.
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(200);
        }
    }

    internal sealed class Point
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
    }
}
