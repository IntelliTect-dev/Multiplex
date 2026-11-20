using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Multiplex.Tests.Identity;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

/// <summary>Starting a live sign-in when its browser cannot run. No browser is involved.</summary>
public sealed class LiveSignInStartTests
{
    /// <summary>
    /// An exception's message can carry paths, URLs or page text from the site, so a failed
    /// live sign-in logs only the kind of error, like the rest of the sign-in area.
    /// </summary>
    [Fact]
    public async Task Failed_live_sign_in_logs_the_kind_of_error_only()
    {
        var missingBrowser = Path.Combine(Path.GetTempPath(), "no-such-chrome-" + Guid.NewGuid().ToString("N"), "chrome.exe");
        await using var host = new SignInTestHost(new SignInTestOptions { ExecutablePath = missingBrowser });
        host.Completer.Add("p1", "stub");
        var client = host.NewClient();
        await client.SignInAsync();
        var (started, _) = await client.StartSignInAsync("p1");
        var attempt = LiveMirrorTests.Attempt(host, started);

        await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(AttemptState.Failed, attempt.State);
        var failure = Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Live sign-in for stub failed", StringComparison.Ordinal));
        Assert.Matches(new Regex(@"^Live sign-in for stub failed: [A-Za-z0-9_.`]+$"), failure.Message);
        Assert.DoesNotContain(missingBrowser, host.Logs.AllText);
    }

    /// <summary>A sign-in that could not even be set up must give its live-browser slot back.</summary>
    [Fact]
    public async Task Sign_in_that_fails_to_start_does_not_use_up_a_live_browser_slot()
    {
        var options = new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") };
        options.Settings["SignIn:MaxLiveBrowsers"] = "1";
        await using var host = new SignInTestHost(options);
        var registry = host.Get<LiveSignInRegistry>();

        // An invalid slug cannot name a profile folder, so the attempt cannot be created.
        var owner = TestPeople.OwnerOf();
        Assert.Throws<ArgumentException>(() => registry.StartOrResume(new SlugOnlyDefinition("Not a slug!"), "p-broken", owner));
        Assert.Null(registry.FindForPending("p-broken"));

        Assert.NotNull(registry.StartOrResume(host.Definition, "p1", owner));
    }
}
