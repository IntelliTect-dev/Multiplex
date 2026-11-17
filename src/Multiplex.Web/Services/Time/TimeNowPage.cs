using PuppeteerSharp;

namespace Multiplex.Web.Services.Time;

/// <summary>DOM knowledge for time.now: the search form, its result list, and a location's clock page.</summary>
internal static class TimeNowPage
{
    public const string HomeUrl = "https://time.now/";
    public const string SearchInput = "#full-width-search-input";
    public const string Clock = "#cityTimeDigital";

    private const string ResultItem = "ul.list-group li.list-group-item";
    private const string ResultBadge = ".badge";
    private const string ResultLink = "a";
    private const string Heading = "h1.city-name-heading";
    private const string ClockZone = ".timezone-abbr";
    private const string DateLine = "#cityDateDisplay";

    internal sealed record Reading(string Location, string Time, string TimeZone, string Date, string Source);

    /// <summary>
    /// Results mix cities, countries, time zones and converter tools. Returns the href of the city or country whose
    /// name matches the query exactly, else the first city or country listed, else null.
    /// </summary>
    public static Task<string?> FindResultHrefAsync(IPage page, string query) =>
        page.EvaluateFunctionAsync<string?>(
            $$"""
            (query) => {
                const strip = s => s.replace(/[^\p{L}\p{N} .'-]/gu, '').trim().toLowerCase();
                const candidates = [...document.querySelectorAll('{{ResultItem}}')]
                    .filter(li => ['City', 'Country'].includes(li.querySelector('{{ResultBadge}}')?.textContent?.trim()))
                    .map(li => li.querySelector('{{ResultLink}}'));
                const best = candidates.find(a => strip(a.textContent) === strip(query)) ?? candidates[0];
                return best?.href ?? null;
            }
            """, query);

    /// <summary>The server-rendered clock can be stale; the first client-side tick replaces it with the real time.</summary>
    public static async Task WaitForLiveClockAsync(IPage page)
    {
        var initial = await page.EvaluateFunctionAsync<string>($"() => document.querySelector('{Clock}').textContent.trim()");
        await page.WaitForFunctionAsync($"(v) => document.querySelector('{Clock}').textContent.trim() !== v", initial);
    }

    public static Task<Reading> ReadAsync(IPage page) =>
        page.EvaluateFunctionAsync<Reading>(
            $$"""
            () => {
                const text = s => document.querySelector(s)?.textContent?.trim() ?? '';
                return {
                    location: text('{{Heading}}').replace(/^Current Time in\s*/, '').replace(/[^\p{L}\p{N} ,.'-]/gu, '').trim(),
                    time: text('{{Clock}}'),
                    timeZone: text('{{ClockZone}}'),
                    date: text('{{DateLine}}'),
                    source: location.href,
                };
            }
            """);
}
