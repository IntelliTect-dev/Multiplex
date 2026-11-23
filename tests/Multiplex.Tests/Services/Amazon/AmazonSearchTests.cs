using System.Net;
using ModelContextProtocol;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>The parts of a search that need no network: input rules, the request, merging, our filters and sorting.</summary>
public class AmazonSearchPlanTests
{
    private static string Url(AmazonSearchRequest request, int page = 1) =>
        WebUtility.UrlDecode(AmazonSearch.Plan(request).PageUrl(page).AbsoluteUri);

    [Fact]
    public void Relevance_AsksForAmazonsFeaturedOrder_WithNoFilters()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("usb c cable"));

        Assert.Equal("https://www.amazon.com/s?k=usb%20c%20cable&s=relevanceblender", plan.PageUrl(1).AbsoluteUri);
        Assert.Equal("https://www.amazon.com/s?k=usb%20c%20cable&s=relevanceblender&page=3", plan.PageUrl(3).AbsoluteUri);
        Assert.Equal(new AmazonAppliedMinimums(3, null), plan.Minimums);
    }

    [Fact]
    public void Price_AsksForAmazonsPriceOrder_With4StarsAndUp_And20Ratings()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("cable", AmazonSort.Price));

        Assert.EndsWith("&s=price-asc-rank&rh=p_72:1248879011", WebUtility.UrlDecode(plan.PageUrl(1).AbsoluteUri));
        Assert.Equal(new AmazonAppliedMinimums(4, 20), plan.Minimums);
    }

    [Fact]
    public void Rating_AsksForAmazonsRatingOrder_With20Ratings_AndTheThreeStarFloor()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("cable", AmazonSort.Rating));

        Assert.EndsWith("&s=review-rank", plan.PageUrl(1).AbsoluteUri);
        Assert.Equal(new AmazonAppliedMinimums(3, 20), plan.Minimums);
    }

    [Fact]
    public void TheCallersOwnMinimums_ReplaceTheOnesASortAdds()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("cable", AmazonSort.Price, MinRating: 3, MinRatingsCount: 500));

        // A price search with minRating 3 sends no "4 Stars & Up".
        Assert.DoesNotContain("p_72", plan.PageUrl(1).AbsoluteUri);
        Assert.Equal(new AmazonAppliedMinimums(3, 500), plan.Minimums);
    }

    [Theory]
    [InlineData(4.5, true)]
    [InlineData(4.0, true)]
    [InlineData(3.5, false)]
    public void MinRating_SendsFourStarsAndUp_OnlyAt4OrMore(double minRating, bool sent)
    {
        var url = Url(new AmazonSearchRequest("cable", MinRating: minRating));

        Assert.Equal(sent, url.Contains("p_72:1248879011"));
    }

    [Fact]
    public void PriceStarsAndFilterCodes_GoInOneEncodedRh()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("cable", MinPrice: 5, MaxPrice: 10, MinRating: 4,
            Filters: ["p_123:3271", "p_n_condition-type:2224371011"]));

        var url = plan.PageUrl(1).AbsoluteUri;
        Assert.EndsWith("&rh=p_36%3A500-1000%2Cp_72%3A1248879011%2Cp_123%3A3271%2Cp_n_condition-type%3A2224371011", url);
        Assert.Single(url.Split("rh=").Skip(1));
    }

    [Theory]
    [InlineData(15.0, null, "p_36:1500-")]
    [InlineData(null, 10.0, "p_36:-1000")]
    [InlineData(9.995, 20.0, "p_36:1000-2000")]
    public void OnePriceBoundAlone_AndCents_AreWrittenAmazonsWay(double? min, double? max, string code)
    {
        var url = Url(new AmazonSearchRequest("cable", MinPrice: (decimal?)min, MaxPrice: (decimal?)max));

        Assert.EndsWith("&rh=" + code, url);
    }

    [Fact]
    public void TheQuery_IsEncoded()
    {
        var url = AmazonSearch.Plan(new AmazonSearchRequest("salt & pepper \"mill\" café +1")).PageUrl(1).AbsoluteUri;

        Assert.Contains("k=salt%20%26%20pepper%20%22mill%22%20caf%C3%A9%20%2B1&s=", url);
    }

    [Fact]
    public void StartPage15WithPages10_ReadsPages15To20()
    {
        var plan = AmazonSearch.Plan(new AmazonSearchRequest("cable", Pages: 10, StartPage: 15));

        Assert.Equal([15, 16, 17, 18, 19, 20], plan.Pages);
    }

    public static TheoryData<AmazonSearchRequest, string> Refused() => new()
    {
        { new AmazonSearchRequest(""), "query" },
        { new AmazonSearchRequest("cable", (AmazonSort)7), "sort" },
        { new AmazonSearchRequest("cable", Pages: 11), "pages" },
        { new AmazonSearchRequest("cable", Pages: 0), "pages" },
        { new AmazonSearchRequest("cable", StartPage: 21), "startPage" },
        { new AmazonSearchRequest("cable", MinRating: 2.5), "nothing rated below 3" },
        { new AmazonSearchRequest("cable", MinRating: 5.5), "minRating" },
        { new AmazonSearchRequest("cable", MinPrice: 20, MaxPrice: 10), "minPrice is above maxPrice" },
        { new AmazonSearchRequest("cable", MinPrice: -1), "minPrice" },
        { new AmazonSearchRequest("cable", Filters: ["p_123:3271,p_6:X"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_123:3271&k=x"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_123%3A3271"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_123:3271|234478"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_123: 3271"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_" + new string('a', 65) + ":1"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["brand:anker"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: [.. Enumerable.Range(1, 11).Select(i => $"p_{i}:1")]), "at most 10" },
        { new AmazonSearchRequest("cable", Filters: ["p_36:500-1000"]), "not a filter code" },
        { new AmazonSearchRequest("cable", Filters: ["p_36:500"]), "minPrice, maxPrice or minRating" },
        { new AmazonSearchRequest("cable", Filters: ["p_72:1248879011"]), "minPrice, maxPrice or minRating" },
        { new AmazonSearchRequest("cable", Filters: ["p_123:3271", "p_123:234478"]), "one per group" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AnInputOutOfRange_IsRefused_NamingWhatIsWrong(AmazonSearchRequest request, string expected)
    {
        var refused = Assert.Throws<McpException>(() => AmazonSearch.Plan(request));

        Assert.Contains(expected, refused.Message);
    }
}

public class AmazonSearchCombineTests
{
    private static AmazonSearchItem Item(string asin, decimal? price = 10, double? rating = 4.5, int? count = 1000,
        bool sponsored = false, int? bought = null) =>
        new(asin, "Title " + asin, sponsored, null, price, null, null, rating, count, bought, bought is null ? null : bought + "+", [], null);

    private static AmazonSearchPageData Page(params AmazonSearchItem[] items) => new(items, "over 100", true, [], []);

    private static AmazonSearch.Outcome Combine(AmazonSearchRequest request, params AmazonSearchPageData[] pages) =>
        AmazonSearch.Combine(AmazonSearch.Plan(request), pages);

    [Fact]
    public void DropsSponsoredResults_AndCountsThem()
    {
        var outcome = Combine(new AmazonSearchRequest("cable"),
            Page(Item("AAAAAAAAA1", sponsored: true), Item("AAAAAAAAA2"), Item("AAAAAAAAA3", sponsored: true)));

        Assert.Equal(["AAAAAAAAA2"], outcome.Results.Select(r => r.Asin));
        Assert.Equal(2, outcome.SponsoredRemoved);
    }

    [Fact]
    public void KeepsTheUnpaidEntry_OfAProductThatIsAlsoAdvertised()
    {
        var outcome = Combine(new AmazonSearchRequest("cable"),
            Page(Item("AAAAAAAAA1", sponsored: true), Item("AAAAAAAAA2"), Item("AAAAAAAAA1")));

        Assert.Equal(["AAAAAAAAA2", "AAAAAAAAA1"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void DropsRepeatedAsins_KeepingTheFirst_AndNumbersAmazonRankAcrossPagesWithoutAds()
    {
        var outcome = Combine(new AmazonSearchRequest("cable", Pages: 2),
            Page(Item("AAAAAAAAA1", sponsored: true), Item("AAAAAAAAA2"), Item("AAAAAAAAA3")),
            Page(Item("AAAAAAAAA3"), Item("AAAAAAAAA4")));

        Assert.Equal([("AAAAAAAAA2", 1), ("AAAAAAAAA3", 2), ("AAAAAAAAA4", 3)], outcome.Results.Select(r => (r.Asin, r.AmazonRank)));
        Assert.Equal("https://www.amazon.com/dp/AAAAAAAAA2", outcome.Results[0].Url);
    }

    [Fact]
    public void OurFilters_DropResultsOutsideThePriceRange_BelowMinRating_OrBelowMinRatingsCount_AndCountThem()
    {
        var outcome = Combine(new AmazonSearchRequest("cable", MinPrice: 5, MaxPrice: 10, MinRating: 4, MinRatingsCount: 100),
            Page(
                Item("AAAAAAAAA1", price: 7),
                Item("AAAAAAAAA2", price: 4.99m),      // under the range (Amazon applies its own price filter loosely)
                Item("AAAAAAAAA3", price: 10.01m),     // over the range
                Item("AAAAAAAAA4", price: null),       // no price to check
                Item("AAAAAAAAA5", rating: 3.9),       // under the stars asked for
                Item("AAAAAAAAA6", count: 99),         // too few ratings
                Item("AAAAAAAAA7", price: 10, rating: 4, count: 100)));

        Assert.Equal(["AAAAAAAAA1", "AAAAAAAAA7"], outcome.Results.Select(r => r.Asin));
        Assert.Equal(5, outcome.FilteredOut);
    }

    [Theory]
    [InlineData(AmazonSort.Relevance)]
    [InlineData(AmazonSort.Price)]
    [InlineData(AmazonSort.Rating)]
    public void TheThreeStarFloor_DropsRatedProductsBelow3_InEverySort(AmazonSort sort)
    {
        var outcome = Combine(new AmazonSearchRequest("cable", sort, MinRating: 3),
            Page(Item("AAAAAAAAA1", rating: 2.9), Item("AAAAAAAAA2", rating: 3.0)));

        Assert.Equal(["AAAAAAAAA2"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void AnUnratedProduct_IsKeptOnlyInRelevance_WithNoMinRatingGiven()
    {
        var unrated = Page(Item("AAAAAAAAA1", rating: null, count: null), Item("AAAAAAAAA2"));

        Assert.Equal(2, Combine(new AmazonSearchRequest("cable"), unrated).Results.Count);
        Assert.Null(Combine(new AmazonSearchRequest("cable"), unrated).Results[0].Rating);
        // Any minimum the caller gives drops it, and so do the price and rating sorts.
        Assert.Single(Combine(new AmazonSearchRequest("cable", MinRating: 3), unrated).Results);
        Assert.Single(Combine(new AmazonSearchRequest("cable", AmazonSort.Price), unrated).Results);
        Assert.Single(Combine(new AmazonSearchRequest("cable", AmazonSort.Rating), unrated).Results);
    }

    [Fact]
    public void SortByRating_PutsMany4Point7StarsAboveAFew4Point8Stars()
    {
        // Worked by hand. Every product starts as if 100 people had rated it 3 stars; its real ratings are added to those.
        //   4.7 from 90,000: (90000 x 4.7 + 100 x 3) / 90100 = 4.698
        //   4.0 from 300:    (  300 x 4.0 + 100 x 3) /   400 = 3.750
        //   4.8 from 12:     (   12 x 4.8 + 100 x 3) /   112 = 3.193
        var outcome = Combine(new AmazonSearchRequest("cable", AmazonSort.Rating, MinRatingsCount: 0),
            Page(Item("AAAAAAAAA1", rating: 4.8, count: 12), Item("AAAAAAAAA2", rating: 4.0, count: 300), Item("AAAAAAAAA3", rating: 4.7, count: 90_000)));

        Assert.Equal([("AAAAAAAAA3", 4.698), ("AAAAAAAAA2", 3.75), ("AAAAAAAAA1", 3.193)],
            outcome.Results.Select(r => (r.Asin, r.WeightedRating!.Value)));
    }

    [Fact]
    public void SortByRating_OnAmazonsOwnRatingPage_PutsThousandsOfRatingsAheadOfAFewDozen()
    {
        // The stars and rating counts of eight of the sixteen products on Amazon's "Avg. Customer Review"
        // page for "usb c charging cable" (2026-11-22). Every product there is rated 4.8 to 5.0, so weighing
        // them against the page's own average (4.86) left the three with 43 to 47 ratings on top (#131).
        var outcome = Combine(new AmazonSearchRequest("usb c charging cable", AmazonSort.Rating),
            Page(
                Item("AAAAAAAAA1", rating: 4.9, count: 3_627),
                Item("AAAAAAAAA2", rating: 5.0, count: 47),
                Item("AAAAAAAAA3", rating: 5.0, count: 45),
                Item("AAAAAAAAA4", rating: 5.0, count: 43),
                Item("AAAAAAAAA5", rating: 4.9, count: 75),
                Item("AAAAAAAAA6", rating: 4.8, count: 35_896),
                Item("AAAAAAAAA7", rating: 4.8, count: 6_817),
                Item("AAAAAAAAA8", rating: 5.0, count: 35)));

        Assert.Equal(
            [
                (4.9, 3_627, 4.849), (4.8, 35_896, 4.795), (4.8, 6_817, 4.774),
                (4.9, 75, 3.814), (5.0, 47, 3.639), (5.0, 45, 3.621), (5.0, 43, 3.601), (5.0, 35, 3.519),
            ],
            outcome.Results.Select(r => (r.Rating!.Value, r.RatingsCount!.Value, r.WeightedRating!.Value)));
    }

    [Fact]
    public void AProductWithFewRatings_StartsNearThreeStars_HoweverManyStarsItHas()
    {
        // (7 x 5.0 + 100 x 3) / 107 = 3.131
        var outcome = Combine(new AmazonSearchRequest("cable"), Page(Item("AAAAAAAAA1", rating: 5.0, count: 7)));

        Assert.Equal(3.131, outcome.Results[0].WeightedRating);
    }

    [Fact]
    public void AProductsWeightedRating_IsTheSame_WhateverElseIsInTheAnswer()
    {
        var alone = Combine(new AmazonSearchRequest("cable"), Page(Item("AAAAAAAAA1", rating: 4.6, count: 250)));
        var amongOthers = Combine(new AmazonSearchRequest("cable"),
            Page(Item("AAAAAAAAA1", rating: 4.6, count: 250), Item("AAAAAAAAA2", rating: 3.0, count: 9_000), Item("AAAAAAAAA3", rating: 5.0, count: 4)));

        Assert.Equal(alone.Results[0].WeightedRating, amongOthers.Results.Single(r => r.Asin == "AAAAAAAAA1").WeightedRating);
    }

    [Fact]
    public void WithoutAnExactRatingsCount_ThereIsNoWeightedRating()
    {
        var outcome = Combine(new AmazonSearchRequest("cable"),
            Page(Item("AAAAAAAAA1", rating: 4.9, count: null), Item("AAAAAAAAA2", rating: 4.1, count: 500)));

        Assert.Null(outcome.Results[0].WeightedRating);
        Assert.NotNull(outcome.Results[1].WeightedRating);
    }

    [Fact]
    public void SortByPrice_PutsTheCheapestFirst_TiesByWeightedRatingThenAmazonRank()
    {
        var outcome = Combine(new AmazonSearchRequest("cable", AmazonSort.Price),
            Page(
                Item("AAAAAAAAA1", price: 9, rating: 4.5, count: 1000),
                Item("AAAAAAAAA2", price: 5, rating: 4.2, count: 1000),
                Item("AAAAAAAAA3", price: 5, rating: 4.8, count: 1000),
                Item("AAAAAAAAA4", price: 5, rating: 4.8, count: 1000),
                Item("AAAAAAAAA5", price: 2, rating: 4.1, count: 50)));

        Assert.Equal(["AAAAAAAAA5", "AAAAAAAAA3", "AAAAAAAAA4", "AAAAAAAAA2", "AAAAAAAAA1"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void SortByPrice_AtTheSamePrice_PutsManyRatingsAheadOfAFewPerfectOnes()
    {
        // (5000 x 4.5 + 100 x 3) / 5100 = 4.471 beats (47 x 5.0 + 100 x 3) / 147 = 3.639.
        var outcome = Combine(new AmazonSearchRequest("cable", AmazonSort.Price),
            Page(Item("AAAAAAAAA1", price: 5, rating: 5.0, count: 47), Item("AAAAAAAAA2", price: 5, rating: 4.5, count: 5_000)));

        Assert.Equal([("AAAAAAAAA2", 4.471), ("AAAAAAAAA1", 3.639)],
            outcome.Results.Select(r => (r.Asin, r.WeightedRating!.Value)));
    }

    [Fact]
    public void SortByRating_TiesGoToCountThenStarsThenAmazonRank()
    {
        // Identical ratings and counts weigh the same, so Amazon's order decides.
        var outcome = Combine(new AmazonSearchRequest("cable", AmazonSort.Rating),
            Page(Item("AAAAAAAAA1", rating: 4.5, count: 200), Item("AAAAAAAAA2", rating: 4.5, count: 200)));

        Assert.Equal(["AAAAAAAAA1", "AAAAAAAAA2"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void SortByPrice_PutsResultsWithNoPriceLast_InAmazonsOrder()
    {
        var outcome = Combine(new AmazonSearchRequest("cable", AmazonSort.Price),
            Page(Item("AAAAAAAAA1", price: null), Item("AAAAAAAAA2", price: 8), Item("AAAAAAAAA3", price: null)));

        Assert.Equal(["AAAAAAAAA2", "AAAAAAAAA1", "AAAAAAAAA3"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void Relevance_KeepsAmazonsOrder()
    {
        var outcome = Combine(new AmazonSearchRequest("cable"),
            Page(Item("AAAAAAAAA1", price: 9, rating: 3.1), Item("AAAAAAAAA2", price: 1, rating: 5)));

        Assert.Equal(["AAAAAAAAA1", "AAAAAAAAA2"], outcome.Results.Select(r => r.Asin));
    }

    [Fact]
    public void WhenOurFiltersDropEveryResult_ResultsIsEmpty_AndNoteSaysWhy()
    {
        var outcome = Combine(new AmazonSearchRequest("cable", MinRatingsCount: 500),
            Page(Item("AAAAAAAAA1", count: 20), Item("AAAAAAAAA2", count: 499)));

        Assert.Empty(outcome.Results);
        Assert.Equal(2, outcome.FilteredOut);
        Assert.Equal("All 2 results were dropped by the minimums asked for: at least 3 stars, at least 500 ratings.", outcome.Note);
    }

    [Fact]
    public void ASearchWithNoMatches_IsAnEmptyList_WithoutANote()
    {
        var outcome = Combine(new AmazonSearchRequest("zqxjvkwpfhm"),
            AmazonSearchPage.Parse(AmazonFixture.Read("search-no-results.html")));

        Assert.Empty(outcome.Results);
        Assert.Null(outcome.Note);
        Assert.True(outcome.SponsoredRemoved > 0);
    }

    [Fact]
    public void TheRealFirstPage_Loses6Ads_AndKeeps16Products()
    {
        var outcome = Combine(new AmazonSearchRequest("usb c charging cable"),
            AmazonSearchPage.Parse(AmazonFixture.Read("search-results.html")));

        Assert.Equal(6, outcome.SponsoredRemoved);
        Assert.Equal(16, outcome.Results.Count);
        Assert.Equal(Enumerable.Range(1, 16), outcome.Results.Select(r => r.AmazonRank));
    }

    [Fact]
    public void ASecondRealPage_CarriesAmazonRankOn_AndRepeatsNothing()
    {
        var outcome = Combine(new AmazonSearchRequest("usb c charging cable", Pages: 2),
            AmazonSearchPage.Parse(AmazonFixture.Read("search-results.html")),
            AmazonSearchPage.Parse(AmazonFixture.Read("search-page-2.html")));

        Assert.Equal(outcome.Results.Count, outcome.Results.Select(r => r.Asin).Distinct().Count());
        Assert.True(outcome.Results.Count > 16);
        Assert.Equal(Enumerable.Range(1, outcome.Results.Count), outcome.Results.Select(r => r.AmazonRank));
    }
}
