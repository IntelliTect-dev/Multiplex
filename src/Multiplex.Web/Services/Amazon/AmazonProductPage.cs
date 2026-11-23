using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Web.Services.Amazon;

/// <summary>What the service knows about Amazon's product page: where each fact sits in the markup.</summary>
internal static partial class AmazonProductPage
{
    internal const string Asin = "input#ASIN";
    internal const string Title = "#productTitle";
    internal const string Byline = "#bylineInfo";
    internal const string Price = "#corePrice_feature_div .a-price .a-offscreen, #corePriceDisplay_desktop_feature_div .a-price:not(.a-text-price) .a-offscreen, #apex_desktop .a-price:not(.a-text-price) .a-offscreen";
    internal const string ListPrice = ".basisPrice .a-offscreen";
    internal const string Savings = ".savingsPercentage";
    internal const string UnitPrice = ".pricePerUnit";
    internal const string Deal = "#dealBadgeSupportingText";
    internal const string Coupon = "#promoPriceBlockMessage_feature_div label, #couponBadgeRegularVpc";
    internal const string Availability = "#availability span";
    internal const string Merchant = "#merchantInfoFeature_feature_div";
    internal const string MerchantName = "#merchantInfoFeature_feature_div a";
    internal const string Fulfiller = "#fulfillerInfoFeature_feature_div [offer-display-feature-name=\"desktop-fulfiller-info\"] span.offer-display-feature-text-message, #fulfillerInfoFeature_feature_div a";
    internal const string Delivery = "#mir-layout-DELIVERY_BLOCK [id^=\"mir-layout-DELIVERY_BLOCK-slot-PRIMARY_DELIVERY_MESSAGE\"]";
    internal const string Stars = "#acrPopover";
    internal const string RatingsCount = "#acrCustomerReviewText";
    internal const string Histogram = "#histogramTable a[aria-label]";
    internal const string Bought = "#social-proofing-faceout-title-tk_bought";
    internal const string BestSellerBadge = "#zeitgeistBadge_feature_div a";
    internal const string ChoiceBadge = "#acBadge_feature_div";
    internal const string Breadcrumbs = "#wayfinding-breadcrumbs_feature_div li a";
    internal const string SaySummary = "[data-testid=\"overall-summary\"]";
    internal const string SayAspect = "[id^=\"rh_controls_aspect_\"]";
    internal const string SayMentions = "[data-testid=\"mentions-inline\"] span";
    internal const string SayAspectSummary = "[data-testid=\"aspect-summary\"]";
    internal const string SayReadMore = "[data-testid=\"read-more\"]";
    internal const string Features = "#feature-bullets li span.a-list-item";
    internal const string Description = "#productDescription";
    internal const string BookDescription = "#bookDescription_feature_div";
    internal const string APlus = "#aplus_feature_div";
    internal const string Overview = "#productOverview_feature_div tr";
    internal const string DetailRows = "table.prodDetTable tr";
    internal const string DetailBullets = "#detailBullets_feature_div li";
    internal const string Variation = "[id^=\"inline-twister-expander-header\"]";
    internal const string Returns = "#returnsInfoFeature_feature_div a span";
    internal const string GalleryMarker = "'colorImages'";

    /// <summary>The parts of the page a saved fixture keeps: everything this class reads.</summary>
    internal static readonly string[] FixtureRoots =
    [
        Asin, Title, Byline, "#corePrice_feature_div", "#corePriceDisplay_desktop_feature_div", "#dealBadge_feature_div",
        "#promoPriceBlockMessage_feature_div", "#availability", Merchant, "#fulfillerInfoFeature_feature_div",
        "#mir-layout-DELIVERY_BLOCK", Stars, RatingsCount, "#histogramTable", Bought, "#zeitgeistBadge_feature_div",
        ChoiceBadge, "#wayfinding-breadcrumbs_feature_div", SaySummary, SayAspect, "#feature-bullets", Description,
        BookDescription, APlus,
        "#productOverview_feature_div", "table.prodDetTable", "#detailBullets_feature_div", Variation,
        "#returnsInfoFeature_feature_div",
    ];

    /// <summary>The parts whose text is Amazon's or its customers' prose; a fixture replaces it with placeholders.</summary>
    internal static readonly string[] FixtureProse = [SaySummary, SayAspectSummary, Features, Description, BookDescription, APlus];

    public const int MaxDescription = 4000;

    [GeneratedRegex(@"(\d+) percent of reviews have (\d) stars?")]
    private static partial Regex HistogramRow();

    [GeneratedRegex(@"^([\d,]+) customers mention (.+?), ([\d,]+) positive, ([\d,]+) negative")]
    private static partial Regex Mentions();

    [GeneratedRegex(@"#([\d,]+) in ([^#(]+)")]
    private static partial Regex Rank();

    /// <exception cref="SiteLayoutChangedException">The page has no title or no ASIN.</exception>
    public static AmazonProduct Parse(string html, DateTimeOffset fetchedAt)
    {
        var document = AmazonText.ParseWithoutScripts(html);
        var title = document.TextOf(Title) ?? throw new SiteLayoutChangedException("Amazon", "the product title");
        var asin = document.QuerySelector(Asin)?.GetAttribute("value");
        if (string.IsNullOrEmpty(asin)) throw new SiteLayoutChangedException("Amazon", "the product's ASIN");

        var warnings = new List<string>();
        var overview = Pairs(document.QuerySelectorAll(Overview), "td", "td:nth-of-type(2)");
        var details = Details(document);
        var rankText = details.GetValueOrDefault("Best Sellers Rank");
        details.Remove("Best Sellers Rank");
        details.Remove("Customer Reviews");

        var histogram = Histogram_(document, warnings);
        var (bought, boughtLabel) = AmazonText.BoughtInPastMonth(document.TextOf(Bought));
        var (soldBy, shipsFrom) = Seller(document);
        var (description, truncated) = Describe(document);
        var (images, main) = Gallery(html);

        var badges = new List<string>();
        if (document.TextOf(BestSellerBadge) is { } best) badges.Add(best);
        if (document.TextOf(ChoiceBadge) is { } choice && choice.Contains("Amazon's Choice", StringComparison.Ordinal))
            badges.Add("Amazon's Choice");

        var savings = document.TextOf(Savings) is { } s
                      && int.TryParse(s.Trim('-', '%', ' '), NumberStyles.None, CultureInfo.InvariantCulture, out var percent)
            ? percent
            : (int?)null;

        return new AmazonProduct(
            Asin: asin,
            Title: title,
            Url: $"https://www.amazon.com/dp/{asin}",
            Brand: Brand(document, details, overview),
            Currency: "USD",
            Price: AmazonText.Price(document.QuerySelector(Price)?.TextContent),
            ListPrice: AmazonText.Price(document.QuerySelector(ListPrice)?.TextContent),
            SavingsPercent: savings,
            UnitPrice: AmazonText.UnitPrice(document.QuerySelector(UnitPrice)?.TextContent),
            Deal: document.TextOf(Deal),
            Coupon: document.QuerySelectorAll(Coupon).Select(c => c.Text())
                .FirstOrDefault(t => t is not null && t.Contains("coupon", StringComparison.OrdinalIgnoreCase)),
            Availability: document.QuerySelectorAll(Availability).Select(a => a.Text()).FirstOrDefault(t => t is not null),
            SoldBy: soldBy,
            ShipsFrom: shipsFrom,
            Delivery: document.TextOf(Delivery),
            Rating: AmazonText.Rating(document.QuerySelector(Stars)?.GetAttribute("title")),
            RatingsCount: AmazonText.Count(document.QuerySelector(RatingsCount)?.TextContent),
            RatingHistogram: histogram,
            BoughtInPastMonth: bought,
            BoughtInPastMonthText: boughtLabel,
            Badges: badges,
            BestSellersRank: rankText is null
                ? []
                : Rank().Matches(rankText)
                    .Select(m => new AmazonBestSellersRank(
                        int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                        m.Groups[2].Value.Trim()))
                    .ToList(),
            Categories: document.QuerySelectorAll(Breadcrumbs).Select(a => a.Text()).Where(t => t is not null).Select(t => t!).ToList(),
            CustomersSay: CustomersSay(document),
            Features: document.QuerySelectorAll(Features).Select(f => f.Text()).Where(t => t is not null).Select(t => t!).ToList(),
            Description: description,
            DescriptionTruncated: truncated,
            Overview: overview,
            Details: details,
            Variations: document.QuerySelectorAll(Variation).Select(Variation_).Where(v => v is not null).Select(v => v!).ToList(),
            Images: images,
            Returns: document.TextOf(Returns),
            Warnings: warnings,
            Source: $"https://www.amazon.com/dp/{asin}",
            FetchedAt: fetchedAt)
        {
            MainImageUrl = main,
        };
    }

    private static string? Brand(IParentNode document, Dictionary<string, string> details, Dictionary<string, string> overview)
    {
        if (document.TextOf(Byline) is { } byline)
        {
            if (byline.StartsWith("Brand:", StringComparison.Ordinal)) return AmazonText.Clean(byline["Brand:".Length..]);
            if (byline.StartsWith("Visit the ", StringComparison.Ordinal) && byline.EndsWith(" Store", StringComparison.Ordinal))
                return byline["Visit the ".Length..^" Store".Length];
        }
        return overview.GetValueOrDefault("Brand") ?? details.GetValueOrDefault("Brand Name") ?? details.GetValueOrDefault("Brand");
    }

    private static (string? SoldBy, string? ShipsFrom) Seller(IParentNode document)
    {
        var name = document.TextOf(MerchantName);
        var label = document.QuerySelector(Merchant)?.QuerySelector("span").Text();
        // "Shipper / Seller" names one party doing both.
        var shipsFrom = label is not null && label.Contains("Shipper", StringComparison.OrdinalIgnoreCase)
            ? name
            : document.TextOf(Fulfiller);
        return (name, shipsFrom);
    }

    private static Dictionary<string, int>? Histogram_(IParentNode document, List<string> warnings)
    {
        var rows = new Dictionary<string, int>();
        foreach (var row in document.QuerySelectorAll(Histogram))
        {
            if (HistogramRow().Match(row.GetAttribute("aria-label") ?? "") is { Success: true } m)
                rows[m.Groups[2].Value] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (rows.Count == 0) return null;
        var sum = rows.Values.Sum();
        if (sum is < 97 or > 103)
            warnings.Add($"The star breakdown adds up to {sum}%, not about 100%; Amazon's markup may have changed.");
        return rows;
    }

    private static AmazonCustomersSay? CustomersSay(IParentNode document)
    {
        var aspects = new List<AmazonAspect>();
        foreach (var element in document.QuerySelectorAll(SayAspect))
        {
            var mentions = element.QuerySelectorAll(SayMentions).Select(s => s.Text())
                .Select(t => t is null ? null : Mentions().Match(t))
                .FirstOrDefault(m => m is { Success: true });
            var summary = element.TextOf(SayAspectSummary);
            string? name = mentions is not null ? mentions.Groups[2].Value : null;
            if (name is null && summary is null) continue;
            name ??= (element.GetAttribute("data-testid") ?? "").Replace("bottomsheet-content-", "");
            // A quote is the text beside each "Read more" link.
            var quotes = element.QuerySelectorAll(SayReadMore)
                .Select(link => AmazonText.Clean(link.ParentElement?.TextContent))
                .Where(t => t is not null)
                .Select(t => t!.EndsWith("Read more", StringComparison.Ordinal) ? t[..^"Read more".Length].Trim() : t)
                .Select(t => t.Trim('"', '“', '”', ' '))
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
            aspects.Add(new AmazonAspect(
                char.ToUpperInvariant(name[0]) + name[1..],
                Number(mentions?.Groups[1].Value), Number(mentions?.Groups[3].Value), Number(mentions?.Groups[4].Value),
                summary, quotes));
        }
        var overall = document.TextOf(SaySummary);
        return overall is null && aspects.Count == 0 ? null : new AmazonCustomersSay(overall, aspects, AiGenerated: true);
    }

    private static int? Number(string? text) =>
        text is not null && int.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static (string? Text, bool Truncated) Describe(IParentNode document)
    {
        var text = document.TextOf(Description) ?? document.TextOf(BookDescription) ?? document.TextOf(APlus);
        return text is { Length: > MaxDescription } ? (text[..MaxDescription], true) : (text, false);
    }

    private static Dictionary<string, string> Pairs(IEnumerable<IElement> rows, string keySelector, string valueSelector)
    {
        var pairs = new Dictionary<string, string>();
        foreach (var row in rows)
        {
            if (row.TextOf(keySelector) is { } key && row.TextOf(valueSelector) is { } value) pairs.TryAdd(key, value);
        }
        return pairs;
    }

    /// <summary>Details come as a table on some pages and as a bulleted "Key : Value" list on others.</summary>
    private static Dictionary<string, string> Details(IParentNode document)
    {
        var details = Pairs(document.QuerySelectorAll(DetailRows), "th", "td");
        foreach (var bullet in document.QuerySelectorAll(DetailBullets))
        {
            var spans = bullet.QuerySelectorAll("span > span").ToList();
            if (spans.Count < 2) continue;
            var key = spans[0].Text()?.TrimEnd(':', ' ');
            var value = spans[1].Text();
            if (!string.IsNullOrEmpty(key) && value is not null) details.TryAdd(key, value);
        }
        return details;
    }

    private static AmazonVariation? Variation_(IElement header)
    {
        var text = header.Text();
        var colon = text?.IndexOf(':') ?? -1;
        if (text is null || colon <= 0 || colon == text.Length - 1) return null;
        return new AmazonVariation(text[..colon].Trim(), text[(colon + 1)..].Trim());
    }

    /// <summary>The gallery is a JSON list inside a script: each item's "hiRes" is a full-size picture, "large" a mid-size one.</summary>
    internal static (IReadOnlyList<string> Images, string? Main) Gallery(string html)
    {
        var marker = html.IndexOf(GalleryMarker, StringComparison.Ordinal);
        if (marker < 0) return ([], null);
        var start = html.IndexOf('[', marker);
        if (start < 0 || start - marker > 200) return ([], null);
        var depth = 0;
        var inString = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
            {
                return ReadGallery(html[start..(i + 1)]);
            }
        }
        return ([], null);
    }

    private static (IReadOnlyList<string>, string?) ReadGallery(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var images = new List<string>();
            string? main = null;
            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                var hiRes = String(item, "hiRes");
                var large = String(item, "large");
                if ((hiRes ?? large) is { } url) images.Add(url);
                main ??= large ?? hiRes;
            }
            return (images, main);
        }
        catch (JsonException)
        {
            return ([], null);
        }
        catch (InvalidOperationException)
        {
            return ([], null);
        }
    }

    private static string? String(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
