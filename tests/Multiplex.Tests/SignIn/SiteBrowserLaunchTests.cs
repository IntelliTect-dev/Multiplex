using System.Text.RegularExpressions;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// How the sign-in browser is started. A site's bot protection reads an automated browser's
/// marks (<c>--enable-automation</c> turns on <c>navigator.webdriver</c>) and refused TripIt's
/// sign-in in one (#61). The browser is started the way a person starts Chrome on a profile of
/// its own, with a debugging port to drive it, and nothing else.
/// </summary>
[Collection(SignInBrowserCollection.Name)]
public sealed class SiteBrowserLaunchTests
{
    /// <summary>
    /// Every switch the sign-in browser may be started with. The first four are the ordinary
    /// Chrome the capture tooling's native mode starts; the rest are this server's own settings
    /// (headless or a window size, no sandbox where configured, and rendering that carries on
    /// while the window is hidden, because the person sees it on their phone).
    /// </summary>
    private static readonly HashSet<string> s_allowedSwitches =
    [
        "user-data-dir", "remote-debugging-port", "no-first-run", "no-default-browser-check",
        "headless", "window-size", "no-sandbox",
        "disable-backgrounding-occluded-windows", "disable-renderer-backgrounding", "disable-background-timer-throttling",
        // Headless Chrome adds this one itself.
        "noerrdialogs",
        // chrome://version brackets the switches set in chrome://flags; there are none.
        "flag-switches-begin", "flag-switches-end",
    ];

    [ChromiumFact]
    public async Task Sign_in_browser_is_started_with_no_automation_switches()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        // A space in the profile's path: the path is quoted on the command line.
        var profile = Path.Combine(host.DataPath, "launch test");
        var browser = await manager.LaunchAsync(StubSignInDefinition.SiteSlug, profile, CancellationToken.None);
        try
        {
            Assert.True(Directory.Exists(Path.Combine(profile, "Default")), "Chrome must run on the profile it was given.");

            var page = (await browser.PagesAsync()).First();

            // What a site reads: the browser does not say it is under automation. Chrome says it
            // is for --enable-automation, and also for an ephemeral debugging port (=0).
            Assert.False(await page.EvaluateExpressionAsync<bool>("navigator.webdriver"));
            Assert.False(await (await browser.NewPageAsync()).EvaluateExpressionAsync<bool>("navigator.webdriver"));

            await page.GoToAsync("chrome://version");
            var commandLine = await page.EvaluateExpressionAsync<string>("document.getElementById('command_line').textContent");

            var switches = Regex.Matches(commandLine, @"(?<=\s|^)--([a-z0-9-]+)").Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("user-data-dir", switches); // this is the launched browser's own command line
            Assert.DoesNotContain("enable-automation", switches);
            Assert.Equal([], switches.Where(s => !s_allowedSwitches.Contains(s)).Distinct().ToArray());
        }
        finally
        {
            await manager.CloseAsync(browser);
        }
    }
}
