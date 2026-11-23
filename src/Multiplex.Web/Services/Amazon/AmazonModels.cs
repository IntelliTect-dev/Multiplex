using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplex.Web.Services.Amazon;

/// <summary>The order of search results, named after PowerShell's <c>Sort-Object</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AmazonSort>))]
public enum AmazonSort
{
    [JsonStringEnumMemberName("relevance")] Relevance,
    [JsonStringEnumMemberName("price")] Price,
    [JsonStringEnumMemberName("rating")] Rating,
}

/// <summary>One result as a search page shows it, ads included.</summary>
internal sealed record AmazonSearchItem(
    string Asin,
    string Title,
    bool Sponsored,
    string? ImageUrl,
    decimal? Price,
    decimal? ListPrice,
    string? UnitPrice,
    double? Rating,
    int? RatingsCount,
    int? BoughtInPastMonth,
    string? BoughtInPastMonthText,
    IReadOnlyList<string> Badges,
    string? Delivery);

/// <summary>What one search page holds.</summary>
internal sealed record AmazonSearchPageData(
    IReadOnlyList<AmazonSearchItem> Items,
    string? TotalResults,
    bool HasNextPage,
    IReadOnlyList<AmazonFilterGroup> Filters,
    IReadOnlyList<string> Warnings);

public sealed record AmazonFilterOption(string Label, string Code);

public sealed record AmazonFilterGroup(string Name, IReadOnlyList<AmazonFilterOption> Options);

public sealed record AmazonSearchResult(
    int AmazonRank,
    string Asin,
    string Title,
    string Url,
    string? ImageUrl,
    decimal? Price,
    decimal? ListPrice,
    string? UnitPrice,
    double? Rating,
    int? RatingsCount,
    double? WeightedRating,
    int? BoughtInPastMonth,
    string? BoughtInPastMonthText,
    IReadOnlyList<string> Badges,
    string? Delivery);

public sealed record AmazonAppliedMinimums(double MinRating, int? MinRatingsCount);

/// <summary>Where a person answers Amazon's robot check, when a search stopped at one.</summary>
public sealed record AmazonHandOver(string Url, DateTimeOffset ExpiresAt);

public sealed record AmazonSearchAnswer(
    string Query,
    AmazonSort Sort,
    AmazonAppliedMinimums AppliedMinimums,
    string Currency,
    IReadOnlyList<int> PagesRead,
    int? NextPage,
    string? TotalResults,
    int SponsoredRemoved,
    int FilteredOut,
    IReadOnlyList<AmazonSearchResult> Results,
    string? Note,
    IReadOnlyList<AmazonFilterGroup> AvailableFilters,
    IReadOnlyList<string> Warnings,
    AmazonHandOver? HandOver,
    IReadOnlyList<string> Sources,
    DateTimeOffset FetchedAt);

public sealed record AmazonBestSellersRank(int Rank, string Category);

public sealed record AmazonAspect(
    string Name,
    int? Mentions,
    int? Positive,
    int? Negative,
    string? Summary,
    IReadOnlyList<string> Quotes);

public sealed record AmazonCustomersSay(string? Summary, IReadOnlyList<AmazonAspect> Aspects, bool AiGenerated);

public sealed record AmazonVariation(string Dimension, string Selected);

public sealed record AmazonProduct(
    string Asin,
    string Title,
    string Url,
    string? Brand,
    string Currency,
    decimal? Price,
    decimal? ListPrice,
    int? SavingsPercent,
    string? UnitPrice,
    string? Deal,
    string? Coupon,
    string? Availability,
    string? SoldBy,
    string? ShipsFrom,
    string? Delivery,
    double? Rating,
    int? RatingsCount,
    IReadOnlyDictionary<string, int>? RatingHistogram,
    int? BoughtInPastMonth,
    string? BoughtInPastMonthText,
    IReadOnlyList<string> Badges,
    IReadOnlyList<AmazonBestSellersRank> BestSellersRank,
    IReadOnlyList<string> Categories,
    AmazonCustomersSay? CustomersSay,
    IReadOnlyList<string> Features,
    string? Description,
    bool DescriptionTruncated,
    IReadOnlyDictionary<string, string> Overview,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<AmazonVariation> Variations,
    IReadOnlyList<string> Images,
    string? Returns,
    IReadOnlyList<string> Warnings,
    string Source,
    DateTimeOffset FetchedAt)
{
    /// <summary>The gallery's first image in its "large" size: the one sent as an image.</summary>
    [JsonIgnore]
    public string? MainImageUrl { get; init; }
}

internal static class AmazonJson
{
    /// <summary>How answers are written: camelCase names, nulls kept so a missing value is visible.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
