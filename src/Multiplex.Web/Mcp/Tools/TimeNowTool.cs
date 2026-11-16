using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Multiplex.Web.Browser;
using PuppeteerSharp;

namespace Multiplex.Web.Mcp.Tools;

[McpServerToolType]
[McpEndpoint("time")]
public static class TimeNowTool
{
    public sealed record LocalTime(string Location, string Time, string TimeZone, string Date, string UtcOffset, string Source);

    [McpServerTool(Name = "get_local_time"),
     Description("Looks up the current local time and UTC offset for a city or country by searching time.now.")]
    public static async Task<LocalTime> GetLocalTime(
        BrowserService browser,
        TimeProvider time,
        [Description("City or country to look up, e.g. 'Tokyo' or 'Japan'.")] string location,
        CancellationToken ct)
    {
        var page = await browser.RentPageAsync(ct);
        try
        {
            // Default 800px viewport collapses the navbar and hides the search input; use a desktop width.
            await page.SetViewportAsync(new ViewPortOptions { Width = 1280, Height = 800 });
            await page.GoToAsync(TimeNowPage.HomeUrl, WaitUntilNavigation.DOMContentLoaded);

            await page.WaitForSelectorAsync(TimeNowPage.SearchInput, new WaitForSelectorOptions { Visible = true });
            await browser.PauseAsync(ct);
            await page.TypeAsync(TimeNowPage.SearchInput, location, new PuppeteerSharp.Input.TypeOptions { Delay = browser.StepDelayMs / 10 });
            await browser.PauseAsync(ct);

            // Enter submits the form to /search?q=...
            await Task.WhenAll(
                page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = [WaitUntilNavigation.DOMContentLoaded] }),
                page.Keyboard.PressAsync("Enter"));

            await page.WaitForFunctionAsync("() => document.querySelector('h1')?.textContent.includes('Search results')");
            await browser.PauseAsync(ct);

            var resultHref = await TimeNowPage.FindResultHrefAsync(page, location);
            if (resultHref is null)
                throw new McpException($"time.now has no city or country matching '{location}'.");

            await page.GoToAsync(resultHref, WaitUntilNavigation.DOMContentLoaded);
            await page.WaitForSelectorAsync(TimeNowPage.Clock);
            await browser.PauseAsync(ct);

            await TimeNowPage.WaitForLiveClockAsync(page);
            var reading = await TimeNowPage.ReadAsync(page);

            await browser.PauseAsync(ct);
            return new LocalTime(
                reading.Location,
                reading.Time,
                reading.TimeZone,
                reading.Date,
                UtcOffset.From(reading.Date, reading.Time, time.GetUtcNow()),
                reading.Source);
        }
        finally
        {
            await browser.ReturnPageAsync(page);
        }
    }
}
