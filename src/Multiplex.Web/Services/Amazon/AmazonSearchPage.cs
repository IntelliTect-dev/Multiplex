using AngleSharp.Dom;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Web.Services.Amazon;

/// <summary>What the service knows about Amazon's search results page: where each fact sits in the markup.</summary>
internal static class AmazonSearchPage
{
    internal const string Result = "div[data-component-type=\"s-search-result\"][data-asin]";
    internal const string NoResultsMarker = "No results for";
    internal const string Title = "h2";
    internal const string SponsoredPrefix = "Sponsored Ad - ";
    internal const string SponsoredLabel = ".puis-sponsored-label-text";
    internal const string Price = ".a-price:not(.a-text-price) .a-offscreen";
    internal const string ListPrice = ".a-price.a-text-price[data-a-strike=\"true\"] .a-offscreen";
    internal const string PriceBlock = "[data-cy=\"price-recipe\"]";
    internal const string Stars = "[aria-label$=\"out of 5 stars, rating details\"]";
    internal const string RatingsCount = "[aria-label$=\" ratings\"], [aria-label$=\" rating\"]";
    internal const string ReviewsBlock = "[data-cy=\"reviews-block\"]";
    internal const string Badge = ".a-badge-text";
    internal const string BadgeCapsule = "div[id=\"amazonBadges-capsule-view\"] [role=\"group\"]";
    internal const string Image = "img.s-image";
    internal const string Delivery = "[data-cy=\"delivery-recipe\"] .udm-primary-delivery-message, [data-cy=\"delivery-recipe\"] .udm-secondary-delivery-message, [data-cy=\"delivery-recipe\"]";
    internal const string InfoBarContainer = "[data-component-type=\"s-result-info-bar\"]";
    internal const string InfoBar = InfoBarContainer + " h2";
    internal const string NextPage = "a.s-pagination-next";
    internal const string FilterMenu = "#s-refinements";
    internal const string FilterOption = "li[id^=\"p_\"]";
    internal const string FilterGroup = "div[role=\"group\"]";
    internal const string FilterGroupTitle = "[role=\"heading\"]";

    /// <summary>The parts of the page a saved fixture keeps: everything this class reads.</summary>
    internal static readonly string[] FixtureRoots = [Result, InfoBarContainer, NextPage, FilterMenu];

    public const int MaxFilterGroups = 15;
    public const int MaxFilterOptions = 10;

    /// <exception cref="SiteLayoutChangedException">The page has neither results nor Amazon's "no results" message.</exception>
    public static AmazonSearchPageData Parse(string html)
    {
        var document = AmazonText.ParseWithoutScripts(html);
        var items = document.QuerySelectorAll(Result)
            .Where(e => !string.IsNullOrEmpty(e.GetAttribute("data-asin")))
            .Select(ParseItem)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

        if (items.Count == 0 && !html.Contains(NoResultsMarker, StringComparison.Ordinal))
            throw new SiteLayoutChangedException("Amazon", "the search results list");

        var warnings = new List<string>();
        if (items.Count >= 5)
        {
            if (items.All(i => i.Price is null)) warnings.Add("No result on a page had a readable price; Amazon's price markup may have changed.");
            if (items.All(i => i.Rating is null)) warnings.Add("No result on a page had a readable rating; Amazon's rating markup may have changed.");
        }

        return new AmazonSearchPageData(items, TotalResults(document), document.QuerySelector(NextPage) is not null,
            Filters(document), warnings);
    }

    private static AmazonSearchItem? ParseItem(IElement result)
    {
        var heading = result.QuerySelector(Title);
        var label = AmazonText.Clean(heading?.GetAttribute("aria-label")) ?? heading.Text();
        if (label is null) return null;
        var sponsored = label.StartsWith(SponsoredPrefix, StringComparison.Ordinal)
                        || result.QuerySelector(SponsoredLabel) is not null;
        var title = label.StartsWith(SponsoredPrefix, StringComparison.Ordinal) ? label[SponsoredPrefix.Length..] : label;

        var (bought, boughtLabel) = AmazonText.BoughtInPastMonth(
            result.QuerySelectorAll($"{ReviewsBlock} span")
                .Select(s => s.Text())
                .FirstOrDefault(t => t is not null && t.Contains("bought in past month", StringComparison.OrdinalIgnoreCase)
                                     && t.Length < 40));

        // "Best Seller in USB Cables" is a capsule of labelled parts; deal and pick badges are plain text.
        var capsules = result.QuerySelectorAll(BadgeCapsule)
            .Select(c => AmazonText.Clean(string.Join(' ', c.Children.Select(part => part.Text()))));
        var badges = capsules.Concat(result.QuerySelectorAll(Badge).Select(b => b.Text()))
            .Where(t => t is not null).Select(t => t!).Distinct().ToList();

        return new AmazonSearchItem(
            result.GetAttribute("data-asin")!,
            title,
            sponsored,
            result.QuerySelector(Image)?.GetAttribute("src"),
            AmazonText.Price(result.QuerySelector(Price)?.TextContent),
            AmazonText.Price(result.QuerySelector(ListPrice)?.TextContent),
            AmazonText.UnitPrice(result.QuerySelector(PriceBlock)?.TextContent),
            AmazonText.Rating(result.QuerySelector(Stars)?.GetAttribute("aria-label")),
            AmazonText.Count(result.QuerySelector(RatingsCount)?.GetAttribute("aria-label")),
            bought,
            boughtLabel,
            badges,
            Short(result.QuerySelector(Delivery).Text()));
    }

    private static string? Short(string? text) => text is { Length: > 200 } ? text[..200] : text;

    /// <summary>"1-16 of over 60,000 results for" as "over 60,000".</summary>
    private static string? TotalResults(IParentNode document)
    {
        var bar = document.QuerySelector(InfoBar).Text();
        if (bar is null) return null;
        var of = bar.IndexOf(" of ", StringComparison.Ordinal);
        var results = bar.IndexOf(" results", StringComparison.Ordinal);
        if (results < 0) return null;
        var start = of >= 0 && of < results ? of + 4 : 0;
        return AmazonText.Clean(bar[start..results]);
    }

    /// <summary>Amazon's filter menu for this search, without price and stars (they have inputs of their own).</summary>
    private static IReadOnlyList<AmazonFilterGroup> Filters(IParentNode document)
    {
        var menu = document.QuerySelector(FilterMenu);
        if (menu is null) return [];
        var groups = new List<(IElement Container, string Name, List<AmazonFilterOption> Options)>();
        foreach (var option in menu.QuerySelectorAll(FilterOption))
        {
            var id = option.Id!;
            var slash = id.IndexOf('/');
            if (slash < 0) continue;
            var code = $"{id[..slash]}:{id[(slash + 1)..]}";
            if (id.StartsWith("p_36/", StringComparison.Ordinal) || id.StartsWith("p_72/", StringComparison.Ordinal)) continue;

            var container = option.Closest(FilterGroup);
            if (container is null || !menu.Contains(container)) continue;
            var label = option.Text();
            if (label is null) continue;

            var group = groups.FirstOrDefault(g => g.Container == container);
            if (group.Container is null)
            {
                var name = container.QuerySelector(FilterGroupTitle).Text();
                if (name is null) continue;
                group = (container, name, []);
                groups.Add(group);
            }
            if (group.Options.Count < MaxFilterOptions) group.Options.Add(new AmazonFilterOption(label, code));
        }
        return groups.Take(MaxFilterGroups).Select(g => new AmazonFilterGroup(g.Name, g.Options)).ToList();
    }
}
