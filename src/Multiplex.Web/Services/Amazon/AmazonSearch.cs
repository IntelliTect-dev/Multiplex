using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace Multiplex.Web.Services.Amazon;

/// <summary>What a caller asked <c>amazon_search</c> for, before it is checked.</summary>
public sealed record AmazonSearchRequest(
    string Query,
    AmazonSort Sort = AmazonSort.Relevance,
    int Pages = 1,
    int StartPage = 1,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    double? MinRating = null,
    int? MinRatingsCount = null,
    IReadOnlyList<string>? Filters = null);

/// <summary>A checked search: its page addresses and the minimums in force.</summary>
internal sealed record AmazonSearchPlan(
    AmazonSearchRequest Request,
    AmazonAppliedMinimums Minimums,
    bool CallerGaveMinRating,
    string AmazonOrder,
    string? Rh)
{
    public const int LastPage = 20;

    /// <summary>The pages this search reads, never past Amazon's page 20.</summary>
    public IEnumerable<int> Pages =>
        Enumerable.Range(Request.StartPage, Math.Min(Request.Pages, LastPage - Request.StartPage + 1));

    public Uri PageUrl(int page)
    {
        var url = $"https://www.amazon.com/s?k={Uri.EscapeDataString(Request.Query)}&s={AmazonOrder}";
        if (page > 1) url += $"&page={page}";
        if (Rh is not null) url += $"&rh={Uri.EscapeDataString(Rh)}";
        return new Uri(url);
    }
}

/// <summary>
/// The parts of a search that need no network: checking the inputs, building the request,
/// merging pages, applying our own filters and sorting.
/// </summary>
internal static partial class AmazonSearch
{
    public const double RatingFloor = 3;
    public const int SortMinRatingsCount = 20;
    public const int MaxPages = 10;
    public const int MaxFilters = 10;
    /// <summary>
    /// A weighted rating starts every product at <see cref="WeightedRatingBaseline"/> stars from this many
    /// made-up ratings, then adds its real ones: a product needs about this many ratings to stand on its own.
    /// </summary>
    public const int WeightedRatingPrior = 100;
    /// <summary>
    /// The stars a product starts from before its own ratings count: the lowest stars a rated product
    /// can have and still be returned.
    /// It is fixed, not the average of the results in the answer, because Amazon's best-rated page is
    /// all 4.8 to 5.0 stars and an average that high lets 5.0 from 40 ratings outrank 4.8 from 35,000 (#131).
    /// </summary>
    public const double WeightedRatingBaseline = RatingFloor;
    public const string FourStarsAndUp = "p_72:1248879011";

    [GeneratedRegex("^p_[A-Za-z0-9_-]{1,64}:[A-Za-z0-9]{1,32}$")]
    private static partial Regex FilterCode();

    /// <summary>Whether <paramref name="code"/> is one the <c>filters</c> input accepts.</summary>
    public static bool IsFilterCode(string code) => FilterCode().IsMatch(code);

    /// <exception cref="McpException">An input is out of range; no request is made.</exception>
    public static AmazonSearchPlan Plan(AmazonSearchRequest request)
    {
        var query = request.Query?.Trim() ?? "";
        if (query.Length is 0 or > 200) throw new McpException("query must be 1 to 200 characters.");
        if (!Enum.IsDefined(request.Sort)) throw new McpException("sort must be relevance, price or rating.");
        if (request.Pages is < 1 or > MaxPages) throw new McpException($"pages must be 1 to {MaxPages}.");
        if (request.StartPage is < 1 or > AmazonSearchPlan.LastPage)
            throw new McpException($"startPage must be 1 to {AmazonSearchPlan.LastPage}.");
        if (request.MinPrice is < 0 or > 100_000 || request.MaxPrice is < 0 or > 100_000)
            throw new McpException("minPrice and maxPrice must be between 0 and 100,000 US dollars.");
        if (request.MinPrice > request.MaxPrice) throw new McpException("minPrice is above maxPrice.");
        if (request.MinRating is < RatingFloor or > 5)
            throw new McpException("minRating must be 3 to 5: nothing rated below 3 stars is ever returned.");
        if (request.MinRatingsCount < 0) throw new McpException("minRatingsCount cannot be negative.");

        var filters = request.Filters ?? [];
        if (filters.Count > MaxFilters) throw new McpException($"filters takes at most {MaxFilters} codes.");
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in filters)
        {
            if (code is null || !FilterCode().IsMatch(code))
                throw new McpException($"'{code}' is not a filter code. Use a code from availableFilters, such as p_123:3271.");
            var group = code[..code.IndexOf(':')];
            if (group is "p_36" or "p_72")
                throw new McpException($"'{code}' is set with minPrice, maxPrice or minRating, not in filters.");
            if (!groups.Add(group))
                throw new McpException($"filters has two codes from the group {group}. Amazon takes one per group; run one search per value.");
        }

        var minRating = request.MinRating ?? (request.Sort == AmazonSort.Price ? 4 : RatingFloor);
        var minCount = request.MinRatingsCount ?? (request.Sort == AmazonSort.Relevance ? (int?)null : SortMinRatingsCount);

        var codes = new List<string>();
        if (request.MinPrice is not null || request.MaxPrice is not null)
            codes.Add($"p_36:{Cents(request.MinPrice)}-{Cents(request.MaxPrice)}");
        if (minRating >= 4) codes.Add(FourStarsAndUp);
        codes.AddRange(filters);

        var order = request.Sort switch
        {
            AmazonSort.Price => "price-asc-rank",
            AmazonSort.Rating => "review-rank",
            _ => "relevanceblender",
        };
        return new AmazonSearchPlan(request with { Query = query }, new AmazonAppliedMinimums(minRating, minCount),
            request.MinRating is not null, order, codes.Count > 0 ? string.Join(",", codes) : null);
    }

    private static string Cents(decimal? dollars) =>
        dollars is { } d ? ((long)Math.Round(d * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The merged, filtered and sorted results of the pages read, with the counts an answer reports.</summary>
    public sealed record Outcome(
        IReadOnlyList<AmazonSearchResult> Results,
        int SponsoredRemoved,
        int FilteredOut,
        string? Note);

    public static Outcome Combine(AmazonSearchPlan plan, IEnumerable<AmazonSearchPageData> pages)
    {
        var all = pages.SelectMany(p => p.Items).ToList();
        var sponsoredRemoved = all.Count(i => i.Sponsored);

        // Ads are dropped; a product that is advertised and also listed unpaid keeps its unpaid entry.
        var unpaid = new List<AmazonSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in all.Where(i => !i.Sponsored))
        {
            if (seen.Add(item.Asin)) unpaid.Add(item);
        }

        var request = plan.Request;
        var kept = new List<(int Rank, AmazonSearchItem Item)>();
        for (var i = 0; i < unpaid.Count; i++)
        {
            if (Passes(plan, unpaid[i])) kept.Add((i + 1, unpaid[i]));
        }
        var filteredOut = unpaid.Count - kept.Count;

        var results = kept.Select(k => new AmazonSearchResult(
            k.Rank, k.Item.Asin, k.Item.Title, $"https://www.amazon.com/dp/{k.Item.Asin}", k.Item.ImageUrl,
            k.Item.Price, k.Item.ListPrice, k.Item.UnitPrice, k.Item.Rating, k.Item.RatingsCount,
            Weighted(k.Item), k.Item.BoughtInPastMonth, k.Item.BoughtInPastMonthText, k.Item.Badges,
            k.Item.Delivery));

        IEnumerable<AmazonSearchResult> sorted = request.Sort switch
        {
            AmazonSort.Price => results
                .OrderBy(r => r.Price is null)
                .ThenBy(r => r.Price)
                .ThenByDescending(r => r.WeightedRating ?? double.MinValue)
                .ThenBy(r => r.AmazonRank),
            AmazonSort.Rating => results
                .OrderBy(r => r.WeightedRating is null)
                .ThenByDescending(r => r.WeightedRating)
                .ThenByDescending(r => r.RatingsCount)
                .ThenByDescending(r => r.Rating)
                .ThenBy(r => r.AmazonRank),
            _ => results.OrderBy(r => r.AmazonRank),
        };
        var list = sorted.ToList();

        string? note = null;
        if (list.Count == 0 && filteredOut > 0)
        {
            var reasons = new List<string> { $"at least {plan.Minimums.MinRating:0.#} stars" };
            if (plan.Minimums.MinRatingsCount is { } c) reasons.Add($"at least {c} ratings");
            if (request.MinPrice is not null || request.MaxPrice is not null) reasons.Add("the price range");
            note = $"All {filteredOut} results were dropped by the minimums asked for: {string.Join(", ", reasons)}.";
        }
        return new Outcome(list, sponsoredRemoved, filteredOut, note);
    }

    private static bool Passes(AmazonSearchPlan plan, AmazonSearchItem item)
    {
        var request = plan.Request;
        if (request.MinPrice is not null || request.MaxPrice is not null)
        {
            if (item.Price is not { } price) return false;
            if (price < (request.MinPrice ?? 0) || price > (request.MaxPrice ?? decimal.MaxValue)) return false;
        }
        if (item.Rating is { } rating)
        {
            if (rating < plan.Minimums.MinRating) return false;
        }
        else if (plan.CallerGaveMinRating || request.Sort != AmazonSort.Relevance)
        {
            // An unrated product is not "below 3 stars", so only plain relevance keeps it.
            return false;
        }
        if (plan.Minimums.MinRatingsCount is { } min && (item.RatingsCount ?? 0) < min) return false;
        return true;
    }

    private static double? Weighted(AmazonSearchItem item)
    {
        // Without an exact count there is nothing to weigh the stars by: null, never a guess.
        if (item.Rating is not { } rating || item.RatingsCount is not { } count) return null;
        double n = count;
        return Math.Round((n * rating + WeightedRatingPrior * WeightedRatingBaseline) / (n + WeightedRatingPrior), 3);
    }
}
