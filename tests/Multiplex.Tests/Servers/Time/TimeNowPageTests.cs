using Multiplex.Web.Servers.Time;
using PuppeteerSharp;

namespace Multiplex.Tests.Servers.Time;

/// <summary>Runs <see cref="TimeNowPage"/> against saved, trimmed time.now markup. Needs Chromium; no network.</summary>
public class TimeNowPageTests(TimeNowPageTests.BrowserFixture fixture) : IClassFixture<TimeNowPageTests.BrowserFixture>
{
    public sealed class BrowserFixture : IAsyncLifetime
    {
        private IBrowser? _browser;

        public async Task InitializeAsync()
        {
            if (ChromiumLocator.Path is null) return;
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = ChromiumLocator.Path,
                Args = ["--no-sandbox"],
            });
        }

        public async Task<IPage> OpenAsync(string fixtureName)
        {
            var page = await _browser!.NewPageAsync();
            await page.SetContentAsync(await File.ReadAllTextAsync(Path.Combine("Servers", "Time", "Fixtures", fixtureName)));
            return page;
        }

        public async Task DisposeAsync()
        {
            if (_browser is not null) await _browser.CloseAsync();
        }
    }

    [ChromiumFact]
    public async Task FindResultHref_PrefersExactNameMatchOverEarlierResults()
    {
        await using var page = await fixture.OpenAsync("search-tokyo.html");

        // Nishi-Tokyo-shi is listed first, but the query is exactly "Tokyo".
        Assert.Equal("https://time.now/tokyo/", await TimeNowPage.FindResultHrefAsync(page, "Tokyo"));
    }

    [ChromiumFact]
    public async Task FindResultHref_ExactMatchIgnoresCaseAndFlagEmoji()
    {
        await using var page = await fixture.OpenAsync("search-japan.html");

        Assert.Equal("https://time.now/countries/japan/", await TimeNowPage.FindResultHrefAsync(page, "JAPAN"));
    }

    [ChromiumFact]
    public async Task FindResultHref_WithoutExactMatchTakesFirstCityOrCountry()
    {
        await using var page = await fixture.OpenAsync("search-partial.html");

        Assert.Equal("https://time.now/countries/japan/", await TimeNowPage.FindResultHrefAsync(page, "Japa"));
    }

    [ChromiumFact]
    public async Task FindResultHref_SkipsToolsAndTimeZones()
    {
        await using var page = await fixture.OpenAsync("search-no-place.html");

        Assert.Null(await TimeNowPage.FindResultHrefAsync(page, "Zzyzx"));
    }

    [ChromiumFact]
    public async Task ReadAsync_ExtractsClockFieldsAndStripsHeadingNoise()
    {
        await using var page = await fixture.OpenAsync("clock-page.html");

        var reading = await TimeNowPage.ReadAsync(page);

        Assert.Equal("Japan", reading.Location);
        Assert.Equal("JST", reading.TimeZone);
        Assert.Equal("Monday, November 16, 2026", reading.Date);
    }

    [ChromiumFact]
    public async Task WaitForLiveClock_ReplacesStaleServerRenderedTime()
    {
        await using var page = await fixture.OpenAsync("clock-page.html");

        await TimeNowPage.WaitForLiveClockAsync(page);

        Assert.Equal("08:11:27", (await TimeNowPage.ReadAsync(page)).Time);
    }
}
