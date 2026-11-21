using System.Text;
using System.Text.Json.Serialization;
using Multiplex.Tests.Identity;
using Multiplex.Web.SignIn;
using PuppeteerSharp;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// The whole connect flow as a phone sees it: a second Chromium, emulating a touch phone and
/// signed in to Multiplex, opens the real connect pages over real HTTP, starts the sign-in, taps on
/// the mirrored image and types into the page's own text field. Covers the page's script, CSP,
/// cookies and the WebSocket on Kestrel. Set MULTIPLEX_SIGNIN_SCREENSHOTS to a folder to keep screenshots.
/// </summary>
[Collection(SignInBrowserCollection.Name)]
public sealed class PhoneBrowserTests
{
    private static readonly TimeSpan s_long = TimeSpan.FromSeconds(90);

    [ChromiumFact]
    public async Task Phone_signs_in_through_the_connect_pages_and_lands_back_in_the_client()
    {
        await using var site = await StubLoginSite.StartAsync();
        await using var host = new SignInTestHost(new SignInTestOptions { StubBaseUrl = site.BaseUrl });
        var baseUrl = host.StartKestrel();
        host.Completer.Add("p1", "stub", clientName: "Claude");

        var phoneProfile = Path.Combine(Path.GetTempPath(), "multiplex-phone-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var phone = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                HeadlessMode = HeadlessMode.True,
                ExecutablePath = ChromiumLocator.Path,
                UserDataDir = phoneProfile,
                Args = ["--no-first-run", "--no-default-browser-check"],
            });
            var page = (await phone.PagesAsync()).First();
            await page.SetViewportAsync(new ViewPortOptions { Width = 390, Height = 760, DeviceScaleFactor = 3, IsMobile = true, HasTouch = true });
            var console = new StringBuilder();
            page.Console += (_, e) => console.AppendLine(e.Message.Type + ": " + e.Message.Text);
            page.PageError += (_, e) => console.AppendLine("pageerror: " + e.Message);

            // The client's redirect URI is not real; answer it locally.
            await page.SetRequestInterceptionAsync(true);
            page.Request += (_, e) => _ = e.Request.Url.StartsWith("https://client.example.test/", StringComparison.Ordinal)
                ? e.Request.RespondAsync(new ResponseData { Status = System.Net.HttpStatusCode.OK, ContentType = "text/html", Body = "<h1>Back in the app</h1>" })
                : e.Request.ContinueAsync();

            // The phone is signed in to Multiplex (as if it had just come back from Microsoft).
            var (_, cookie) = await TestPeople.SignInAsync(host.Services);
            var separator = cookie.IndexOf('=');
            await page.SetCookieAsync(new CookieParam
            {
                Name = cookie[..separator],
                Value = cookie[(separator + 1)..],
                Url = baseUrl.AbsoluteUri,
                HttpOnly = true,
            });

            var step = "connect page";
            try
            {
                await RunFlowAsync(host, page, baseUrl, console, s => step = s);
            }
            catch (Exception ex)
            {
                var attempt = host.Get<LiveSignInRegistry>().FindForPending("p1");
                throw new Xunit.Sdk.XunitException(
                    $"Phone flow failed at '{step}': {ex.GetType().Name}: {ex.Message}\nphone url: {page.Url}\n" +
                    $"attempt: {attempt?.State} ({attempt?.StatusMessage}) remote url: {attempt?.ActivePage?.Url}\nconsole:\n{console}");
            }
        }
        finally
        {
            for (var i = 0; i < 20 && Directory.Exists(phoneProfile); i++)
            {
                try { Directory.Delete(phoneProfile, recursive: true); }
                catch (IOException) { await Task.Delay(250); }
            }
        }
    }

    private static async Task RunFlowAsync(SignInTestHost host, IPage page, Uri baseUrl, StringBuilder console, Action<string> step)
    {
        {
            // Connect page: who is asking, for whom, and the button that starts the site's sign-in.
            await page.GoToAsync(new Uri(baseUrl, "/connect/stub?p=p1").AbsoluteUri);
            var connectPage = await page.GetContentAsync();
            Assert.Contains("wants to use your", connectPage);
            Assert.Contains(TestPeople.Owner, connectPage);
            await ScreenshotAsync(page, "1-connect");
            await Task.WhenAll(page.WaitForNavigationAsync(), TapAsync(page, "main form[action^='/connect/stub/start'] button[type=submit]"));

            // Live page: wait for a mirrored frame that matches the size the phone asked for.
            step("live page: first frame");
            Assert.Contains("/connect/stub/live?a=", page.Url);
            var attempt = host.Get<LiveSignInRegistry>().FindForPending("p1")!;
            await LiveMirrorTests.WaitForAsync(async () =>
                await page.EvaluateExpressionAsync<bool>("document.getElementById('stage').classList.contains('has-frame')"));
            step("live page: frame fitted to the phone");
            await LiveMirrorTests.WaitForAsync(async () =>
            {
                var remote = await attempt.ActivePage!.EvaluateExpressionAsync<double>("innerWidth / innerHeight");
                var shown = await page.EvaluateExpressionAsync<double>("(() => { const i = document.getElementById('screen'); return i.naturalWidth / i.naturalHeight; })()");
                return Math.Abs(remote - shown) < 0.02;
            });
            await ScreenshotAsync(page, "2-live");
            Assert.Equal(0, await page.EvaluateExpressionAsync<int>("document.documentElement.scrollWidth - innerWidth")); // no sideways scroll
            // The remote viewport was fitted to the phone, so the whole mirrored page is on screen
            // (swipes on the mirror scroll the remote page, not ours).
            Assert.True(await page.EvaluateExpressionAsync<bool>(
                "(() => { const r = document.getElementById('screen').getBoundingClientRect(); return r.bottom <= innerHeight + 2 && Math.abs(r.width - innerWidth) < 2; })()"),
                "The mirror must fill the phone's width and fit its height.");
            Assert.Equal(390, await attempt.ActivePage!.EvaluateExpressionAsync<int>("innerWidth"));

            // Tap the email field on the mirror, type in our own text field, Send; then the password, then Enter.
            step("typing");
            await TapMirrorAsync(page, attempt, "#email");
            await page.TypeAsync("#txt", StubLoginSite.Email);
            await TapAsync(page, "#send");
            await TapMirrorAsync(page, attempt, "#password");
            await TapAsync(page, "#hide");
            Assert.Equal("password", await page.EvaluateExpressionAsync<string>("document.getElementById('txt').type"));
            await page.TypeAsync("#txt", StubLoginSite.Password);
            await ScreenshotAsync(page, "3-typing");
            await TapAsync(page, "button[data-key=Enter]"); // sends the typed text, then Enter

            step("back to the client");
            await LiveMirrorTests.WaitForAsync(() => Task.FromResult(page.Url.StartsWith("https://client.example.test/callback?code=code-for-p1", StringComparison.Ordinal)), s_long);
            await ScreenshotAsync(page, "4-back-in-client");

            step("checks");
            var completed = Assert.Single(host.Completer.Completed);
            Assert.Equal(TestPeople.OwnerOf(), completed.Owner);
            Assert.Equal(StubLoginSite.AccountId, completed.Account?.AccountId);
            Assert.DoesNotContain("pageerror", console.ToString());
            Assert.DoesNotContain("Content Security Policy", console.ToString());
        }
    }

    private static async Task TapAsync(IPage page, string selector)
    {
        var box = await (await page.WaitForSelectorAsync(selector)).BoundingBoxAsync();
        await page.Touchscreen.TapAsync(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    /// <summary>Taps the mirrored image where <paramref name="selector"/> sits on the remote page.</summary>
    private static async Task TapMirrorAsync(IPage page, LiveSignInAttempt attempt, string selector)
    {
        var target = await LiveMirrorTests.CenterOfAsync(attempt, selector);
        var image = await page.EvaluateExpressionAsync<Rect>(
            "(() => { const r = document.getElementById('screen').getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()");
        await page.Touchscreen.TapAsync((decimal)(image.X + target.X * image.W), (decimal)(image.Y + target.Y * image.H));
        await Task.Delay(300);
    }

    private static async Task ScreenshotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_SIGNIN_SCREENSHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        await page.ScreenshotAsync(Path.Combine(folder, name + ".png"), new ScreenshotOptions { FullPage = true });
    }

    private sealed class Rect
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("w")] public double W { get; set; }
        [JsonPropertyName("h")] public double H { get; set; }
    }
}
