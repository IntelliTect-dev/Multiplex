using System.Text.RegularExpressions;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

internal static class AmazonFixture
{
    public static string Folder => Path.Combine("Services", "Amazon", "Fixtures");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));
}

/// <summary>Reading Amazon's pages, against trimmed copies of real ones (see the service README for how they are made).</summary>
public class AmazonSearchPageTests
{
    private static readonly AmazonSearchPageData Page = AmazonSearchPage.Parse(AmazonFixture.Read("search-results.html"));

    private static AmazonSearchItem Anker => Page.Items.Single(i => i.Asin == "B088NRLMPV");

    [Fact]
    public void ReadsAsinTitlePriceStarsAndCount_ForEachResult()
    {
        Assert.Equal(22, Page.Items.Count);
        Assert.All(Page.Items, i => Assert.Matches("^[A-Z0-9]{10}$", i.Asin));
        Assert.All(Page.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Title)));
        Assert.Equal("Anker USB C to USB C Cable, 60W Fast Charging Cable (2-Pack, 6 ft, Black)", Anker.Title);
        Assert.Equal(7.99m, Anker.Price);
        Assert.Equal(4.7, Anker.Rating);
        Assert.StartsWith("https://m.media-amazon.com/images/I/", Anker.ImageUrl);
    }

    [Fact]
    public void FlagsSponsoredResults_AndStripsTheSponsoredPrefixFromTheirTitles()
    {
        var sponsored = Page.Items.Where(i => i.Sponsored).ToList();

        Assert.Equal(6, sponsored.Count);
        Assert.All(Page.Items, i => Assert.DoesNotContain("Sponsored Ad", i.Title));
        Assert.False(Anker.Sponsored);
    }

    [Fact]
    public void TakesTheExactRatingsCount_NotTheShortenedText()
    {
        // The page shows "(91.9K)"; only the link's label has the exact number.
        Assert.Equal(91951, Anker.RatingsCount);
    }

    [Fact]
    public void ReadsListPriceFromTheStruckThroughPrice_NotTheUnitPrice()
    {
        Assert.Equal(9.99m, Anker.ListPrice);
        Assert.Equal("$4.00/count", Anker.UnitPrice);
    }

    [Fact]
    public void ReadsBadges_BoughtInPastMonth_AndDelivery()
    {
        Assert.Contains("Black Friday Deal", Anker.Badges);
        Assert.Contains("Best Seller in USB Cables", Anker.Badges);
        Assert.Equal(10_000, Anker.BoughtInPastMonth);
        Assert.Equal("10K+", Anker.BoughtInPastMonthText);
        Assert.Contains("FREE delivery", Anker.Delivery);
    }

    [Fact]
    public void ReadsTheTotalAndWhetherThereIsANextPage()
    {
        Assert.Equal("over 60,000", Page.TotalResults);
        Assert.True(Page.HasNextPage);
    }

    [Fact]
    public void ReadsTheFilterMenu_AsGroupsOfLabelsAndCodes()
    {
        var brands = Page.Filters.Single(g => g.Name == "Brands");

        Assert.Contains(new AmazonFilterOption("Anker", "p_123:3271"), brands.Options);
        Assert.Contains(new AmazonFilterOption("Amazon Basics", "p_123:234478"), brands.Options);
        Assert.All(Page.Filters.SelectMany(g => g.Options), o => Assert.Matches("^p_[A-Za-z0-9_-]+:[A-Za-z0-9]+$", o.Code));
    }

    [Theory]
    [InlineData("search-results.html")]
    [InlineData("search-filtered.html")]
    [InlineData("search-page-2.html")]
    public void EveryFilterCodeItOffers_IsOneTheFiltersInputAccepts(string fixture)
    {
        var page = AmazonSearchPage.Parse(AmazonFixture.Read(fixture));

        Assert.NotEmpty(page.Filters);
        foreach (var option in page.Filters.SelectMany(g => g.Options))
            AmazonSearch.Plan(new AmazonSearchRequest("cable", Filters: [option.Code]));
    }

    [Fact]
    public void AFilterOptionWhoseCodeTheInputWouldRefuse_IsNotOffered()
    {
        var html = AmazonFixture.Read("search-results.html").Replace("id=\"p_123/3271\"", "id=\"p_123/Western Digital\"");

        var brands = AmazonSearchPage.Parse(html).Filters.Single(g => g.Name == "Brands");

        Assert.DoesNotContain(brands.Options, o => o.Label == "Anker");
        Assert.Contains(brands.Options, o => o.Label == "Amazon Basics");
    }

    [Fact]
    public void TrimsTheFilterMenu_To15GroupsOf10_WithoutPriceOrStars()
    {
        Assert.InRange(Page.Filters.Count, 1, AmazonSearchPage.MaxFilterGroups);
        Assert.All(Page.Filters, g => Assert.InRange(g.Options.Count, 1, AmazonSearchPage.MaxFilterOptions));
        Assert.DoesNotContain(Page.Filters.SelectMany(g => g.Options), o => o.Code.StartsWith("p_36:") || o.Code.StartsWith("p_72:"));
    }

    [Fact]
    public void AFilteredPage_ParsesTheSameWay_AndItsUnpaidResultsAreInRange()
    {
        // Fetched with rh=p_36:500-1000,p_72:1248879011: $5 to $10, 4 stars and up.
        var filtered = AmazonSearchPage.Parse(AmazonFixture.Read("search-filtered.html"));
        var unpaid = filtered.Items.Where(i => !i.Sponsored).ToList();

        Assert.Equal(16, unpaid.Count);
        Assert.All(unpaid, i => Assert.InRange(i.Price!.Value, 5m, 10m));
        Assert.All(unpaid, i => Assert.True(i.Rating >= 4));
        // Ads ignore the filters: one of them is rated below 4.5 while every unpaid result is 4.5 or more.
        Assert.Contains(filtered.Items, i => i.Sponsored && i.Rating < unpaid.Min(u => u.Rating));
    }

    [Theory]
    [InlineData("search-no-results.html")]
    [InlineData("search-filtered-no-results.html")]
    public void ASearchWithNoMatches_HoldsOnlyAds_AndIsNotALayoutChange(string fixture)
    {
        var page = AmazonSearchPage.Parse(AmazonFixture.Read(fixture));

        Assert.All(page.Items, i => Assert.True(i.Sponsored));
        Assert.False(page.HasNextPage);
        Assert.Null(page.TotalResults);
    }

    [Fact]
    public void WithoutTheResultsList_FailsWithLayoutChanged_NamingIt()
    {
        var html = AmazonFixture.Read("search-results.html").Replace("data-component-type=\"s-search-result\"", "data-component-type=\"renamed\"");

        var changed = Assert.Throws<SiteLayoutChangedException>(() => AmazonSearchPage.Parse(html));

        Assert.Equal("the search results list", changed.Missing);
        Assert.Contains("Amazon's page layout has changed", changed.Message);
    }

    [Fact]
    public void WherePricesVanishFromEveryResult_AddsAWarning()
    {
        var html = AmazonFixture.Read("search-results.html").Replace("class=\"a-offscreen\"", "class=\"renamed\"");

        var page = AmazonSearchPage.Parse(html);

        Assert.Equal(22, page.Items.Count);
        Assert.Contains(page.Warnings, w => w.Contains("price"));
    }

    [Fact]
    public void TheWholeFixture_HasNoWarnings()
    {
        Assert.Empty(Page.Warnings);
    }
}

public class AmazonProductPageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 23, 52, 51, TimeSpan.Zero);
    private static readonly AmazonProduct Popular = AmazonProductPage.Parse(AmazonFixture.Read("product-popular.html"), At);
    private static readonly AmazonProduct Few = AmazonProductPage.Parse(AmazonFixture.Read("product-few-ratings.html"), At);

    [Fact]
    public void ReadsTheProductAndItsPrices()
    {
        Assert.Equal("B088NRLMPV", Popular.Asin);
        Assert.Equal("Anker USB C to USB C Cable, 60W Fast Charging Cable (2-Pack, 6 ft, Black)", Popular.Title);
        Assert.Equal("Anker", Popular.Brand);
        Assert.Equal("USD", Popular.Currency);
        Assert.Equal(7.99m, Popular.Price);
        Assert.Equal(9.99m, Popular.ListPrice);
        Assert.Equal(20, Popular.SavingsPercent);
        Assert.Equal("$4.00/count", Popular.UnitPrice);
        Assert.Equal(At, Popular.FetchedAt);
    }

    [Fact]
    public void ReadsThePricePaid_EvenWhenTheStruckThroughPriceComesFirst()
    {
        var html = AmazonFixture.Read("product-few-ratings.html").Replace("<div id=\"corePrice_feature_div\">",
            "<div id=\"corePrice_feature_div\"><span class=\"a-price a-text-price\" data-a-strike=\"true\"><span class=\"a-offscreen\">$19.99</span></span>");

        Assert.Equal(14.99m, AmazonProductPage.Parse(html, At).Price);
    }

    [Fact]
    public void OddNumbersInAmazonsText_AreNull_NotACrash()
    {
        Assert.Equal((null, null), AmazonText.BoughtInPastMonth(". bought in past month"));
        Assert.Equal((null, null), AmazonText.BoughtInPastMonth("99999999M+ bought in past month"));
        Assert.Null(AmazonText.Price("$99999999999999999999999999999999"));
        Assert.Null(AmazonText.Count("99,999,999,999 ratings"));
    }

    [Fact]
    public void BuildsItsUrlFromTheAsin_NotTheClpCanonical()
    {
        Assert.Equal("https://www.amazon.com/dp/B088NRLMPV", Popular.Url);
        Assert.Equal(Popular.Url, Popular.Source);
    }

    [Fact]
    public void DropsTheDealBadgesTemplateText()
    {
        // The badge also holds "Black Friday Deal NO_OF_HOURS hours NO_OF_MINUTES minutes" for a script to fill in.
        Assert.Equal("Black Friday Deal", Popular.Deal);
    }

    [Fact]
    public void ReadsAvailabilitySellerDeliveryAndReturns()
    {
        Assert.Equal("In Stock", Popular.Availability);
        Assert.Equal("AnkerDirect", Popular.SoldBy);
        Assert.Equal("Amazon", Popular.ShipsFrom);
        Assert.StartsWith("FREE delivery", Popular.Delivery);
        Assert.StartsWith("FREE refund/replacement", Popular.Returns);
        // "Shipper / Seller Amazon.com" names one party doing both.
        Assert.Equal("Amazon.com", Few.SoldBy);
        Assert.Equal("Amazon.com", Few.ShipsFrom);
    }

    [Fact]
    public void ReadsStarsTheExactCount_AndBoughtInPastMonth()
    {
        Assert.Equal(4.7, Popular.Rating);
        Assert.Equal(91951, Popular.RatingsCount);
        Assert.Equal(10_000, Popular.BoughtInPastMonth);
        Assert.Equal("10K+", Popular.BoughtInPastMonthText);
    }

    [Fact]
    public void ReadsTheHistogram_AndItAddsUpToAbout100()
    {
        Assert.Equal(new Dictionary<string, int> { ["5"] = 87, ["4"] = 8, ["3"] = 2, ["2"] = 1, ["1"] = 2 }, Popular.RatingHistogram);
        Assert.Empty(Popular.Warnings);
    }

    [Fact]
    public void WhoseHistogramDoesNotAddUp_AddsAWarning()
    {
        var html = AmazonFixture.Read("product-popular.html").Replace("87 percent of reviews have 5 stars", "37 percent of reviews have 5 stars");

        var product = AmazonProductPage.Parse(html, At);

        Assert.Contains(product.Warnings, w => w.Contains("star breakdown adds up to 50%"));
    }

    [Fact]
    public void ReadsCustomersSay_WithEachAspectsPositiveAndNegativeCounts()
    {
        var say = Popular.CustomersSay!;

        Assert.True(say.AiGenerated);
        Assert.False(string.IsNullOrWhiteSpace(say.Summary));
        Assert.Equal(8, say.Aspects.Count);
        var quality = say.Aspects[0];
        Assert.Equal("Quality", quality.Name);
        Assert.Equal(6150, quality.Mentions);
        Assert.Equal(5546, quality.Positive);
        Assert.Equal(604, quality.Negative);
        Assert.False(string.IsNullOrWhiteSpace(quality.Summary));
        Assert.Equal(4, quality.Quotes.Count);
        Assert.Contains(say.Aspects, a => a.Name == "Value for money");
        // The one aspect customers are more negative than positive about.
        var reliability = say.Aspects.Single(a => a.Name == "Reliability");
        Assert.True(reliability.Negative > reliability.Positive);
    }

    [Fact]
    public void WithoutCustomersSay_ReturnsNullSummary()
    {
        Assert.Null(Few.CustomersSay);
        Assert.Equal(4.6, Few.Rating);
    }

    [Fact]
    public void ReadsBadgesRankAndCategories()
    {
        Assert.Contains("#1 Best Seller in USB Cables", Popular.Badges);
        Assert.Equal([new AmazonBestSellersRank(1, "USB Cables")], Popular.BestSellersRank);
        Assert.Equal("Electronics", Popular.Categories[0]);
        Assert.Equal("USB Cables", Popular.Categories[^1]);
        Assert.Contains("Amazon's Choice", Few.Badges);
    }

    [Fact]
    public void ReadsTheBrand_FromTheByline_OrFromTheDetails()
    {
        Assert.Equal("Amazon", Few.Brand);     // "Brand: Amazon"
        Assert.Equal("Anker", Popular.Brand);  // no byline on this page; the overview table has it
    }

    [Fact]
    public void ReadsDetailsFromTheTable_OnThePopularPage_AndFromTheBullets_OnTheFewRatingsPage()
    {
        Assert.Equal("USB Type C", Popular.Overview["Connector Type"]);
        Assert.Equal("Anker", Popular.Details["Manufacturer"]);
        Assert.Equal("3.52 ounces", Popular.Details["Item Weight"]);
        Assert.DoesNotContain("Best Sellers Rank", Popular.Details.Keys);
        Assert.DoesNotContain("Customer Reviews", Popular.Details.Keys);

        Assert.Equal("Amazon", Few.Details["Manufacturer"]);
        Assert.Equal("B0FFK2WQJH", Few.Details["ASIN"]);
        Assert.Equal("February 20, 2026", Few.Details["Date First Available"]);
    }

    [Fact]
    public void ReadsFeaturesDescriptionAndVariations()
    {
        Assert.Equal(6, Popular.Features.Count);
        Assert.NotNull(Popular.Description);
        Assert.Equal(
            [new AmazonVariation("Color", "Black"), new AmazonVariation("Size", "6FT*2"), new AmazonVariation("Total product quantity", "2 Count")],
            Popular.Variations);
    }

    [Fact]
    public void ReadsHiResImagesFromTheGalleryData_AndTheLargeOneAsTheMainImage()
    {
        Assert.Equal("https://m.media-amazon.com/images/I/71k0d1eTetL._AC_SL1500_.jpg", Popular.Images[0]);
        Assert.All(Popular.Images, i => Assert.StartsWith("https://m.media-amazon.com/images/I/", i));
        Assert.StartsWith("https://m.media-amazon.com/images/I/", Popular.MainImageUrl);
        Assert.DoesNotContain("SL1500", Popular.MainImageUrl);
    }

    [Fact]
    public void ALongDescription_IsCutAt4000Characters_AndSaysSo()
    {
        var html = AmazonFixture.Read("product-few-ratings.html")
            .Replace("<html><body>", $"<html><body><div id=\"productDescription\">{new string('x', 5000)}</div>");

        var product = AmazonProductPage.Parse(html, At);

        Assert.Equal(AmazonProductPage.MaxDescription, product.Description!.Length);
        Assert.True(product.DescriptionTruncated);
        Assert.False(Few.DescriptionTruncated);
    }

    [Fact]
    public void AnUnavailableProduct_HasNoPrice_AndSaysSo()
    {
        var product = AmazonProductPage.Parse(AmazonFixture.Read("product-unavailable.html"), At);

        Assert.Null(product.Price);
        Assert.Equal("Currently unavailable.", product.Availability);
        Assert.Equal("B0FFK2WQJH", product.Asin);
    }

    [Fact]
    public void AProductWithNoRatings_HasNullRatingCountAndHistogram_NotAnError()
    {
        var product = AmazonProductPage.Parse(AmazonFixture.Read("product-no-ratings.html"), At);

        Assert.Null(product.Rating);
        Assert.Null(product.RatingsCount);
        Assert.Null(product.RatingHistogram);
        Assert.Equal(14.99m, product.Price);
    }

    [Fact]
    public void ABook_HasAtLeastTitlePriceRatingAndDescription()
    {
        var book = AmazonProductPage.Parse(AmazonFixture.Read("product-book.html"), At);

        Assert.StartsWith("Essential C# 8.0", book.Title);
        Assert.NotNull(book.Price);
        Assert.Equal(4.6, book.Rating);
        Assert.NotNull(book.Description);
    }

    [Theory]
    [InlineData("<span id=\"productTitle\"", "the product title")]
    [InlineData("id=\"ASIN\"", "the product's ASIN")]
    public void WithoutAnAnchor_FailsWithLayoutChanged_NamingIt(string anchor, string missing)
    {
        var html = AmazonFixture.Read("product-popular.html").Replace(anchor, "id=\"renamed\"");

        var changed = Assert.Throws<SiteLayoutChangedException>(() => AmazonProductPage.Parse(html, At));

        Assert.Equal(missing, changed.Missing);
    }
}

/// <summary>The repository is public, so no committed Amazon page may carry a visitor's ids or Amazon's prose.</summary>
public partial class AmazonFixturePolicyTests
{
    [GeneratedRegex(">([^<]{301,})<")]
    private static partial Regex LongText();

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(AmazonFixture.Folder, "*.html").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixtures_CarryNoVisitorIdsOrLongProse(string fixture)
    {
        var html = AmazonFixture.Read(fixture);

        Assert.DoesNotContain("ue_sid", html);
        Assert.DoesNotContain("session-id", html);
        Assert.DoesNotContain("ubid-", html);
        Assert.DoesNotContain("<script src", html);
        Assert.DoesNotContain("csrf", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offerListingId", html);
        Assert.DoesNotContain("qid", html);
        // A hidden field's value may only be a short fact or a placeholder, never a token.
        Assert.DoesNotMatch("<input[^>]*value=\"[^\"]{25,}\"", html);
        // Styles are not prose; everything else over 300 characters would be.
        var withoutStyles = Regex.Replace(html, "<style.*?</style>|<!--.*?-->", "", RegexOptions.Singleline);
        Assert.False(LongText().IsMatch(withoutStyles), $"{fixture} holds a run of text over 300 characters.");
    }

    [Fact]
    public void ProductFixtures_HoldPlaceholders_WhereAmazonsProseWas()
    {
        var html = AmazonFixture.Read("product-popular.html");

        Assert.Contains("Placeholder text", html);
        Assert.Contains("Placeholder quote", html);
        Assert.DoesNotContain("Customers find", html);
    }
}
