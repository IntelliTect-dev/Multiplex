using System.Net;
using ModelContextProtocol;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

public class AmazonPageClassifierTests
{
    private static readonly Uri Product = new("https://www.amazon.com/dp/B088NRLMPV");
    private static readonly Uri Search = new("https://www.amazon.com/s?k=cable");

    private static AmazonAnswer Classify(int status, string body = "", string? location = null, Uri? request = null,
        params string[] headers) =>
        AmazonPageClassifier.Classify((HttpStatusCode)status, headers, request ?? Product,
            location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute), body);

    [Fact]
    public void ARealPage_IsAPage()
    {
        Assert.Equal(AmazonAnswer.Page, Classify(200, AmazonFixture.Read("product-popular.html")));
        Assert.Equal(AmazonAnswer.Page, Classify(200, AmazonFixture.Read("search-results.html"), request: Search));
    }

    [Fact]
    public void AFullPageThatOnlyMentionsACheck_InAReviewOrAScript_IsStillAPage()
    {
        // A real page is a megabyte or more, and a review or a script can say any of these things.
        var page = AmazonFixture.Read("product-popular.html").Replace("</body>",
            "<p>The site made me Enter the characters you see below to make sure you're not a robot. Then I emailed api-services-support@amazon.com.</p>"
            + "<script>var next = '/errors/validateCaptcha';</script>"
            + new string(' ', AmazonPageClassifier.InterstitialMaxLength) + "</body>");

        Assert.Equal(AmazonAnswer.Page, Classify(200, page));
        // The same words on a short page, or with any other status, are what they say.
        Assert.Equal(AmazonAnswer.AutomatedAccessRefused, Classify(503, page));
        Assert.Equal(AmazonAnswer.Challenge, Classify(200, "<html><p>We need to make sure you're not a robot.</p></html>"));
    }

    [Fact]
    public void ACheckForm_IsARobotCheck_HoweverLongThePage()
    {
        var page = AmazonFixture.Read("blocked-continue-shopping.html") + new string(' ', AmazonPageClassifier.InterstitialMaxLength);

        Assert.Equal(AmazonAnswer.RobotCheck, Classify(200, page));
    }

    [Fact]
    public void TheContinueShoppingCheck_ArrivesWithA200_AndIsARobotCheck()
    {
        // The page Amazon served this service on 2026-11-23. It also carries the "automated access"
        // comment, which must not make it a refusal.
        var page = AmazonFixture.Read("blocked-continue-shopping.html");

        Assert.Contains(AmazonPageClassifier.AutomatedAccessMarker, page);
        Assert.Equal(AmazonAnswer.RobotCheck, Classify(200, page));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    public void TheTypeTheCharactersCheck_IsARobotCheck_WhateverItsStatus(int status)
    {
        Assert.Equal(AmazonAnswer.RobotCheck, Classify(status, AmazonFixture.Read("blocked-robot-check.html")));
    }

    [Fact]
    public void TheAutomatedAccess503_IsARefusal_WhileAPlain503IsUnavailable()
    {
        Assert.Equal(AmazonAnswer.AutomatedAccessRefused, Classify(503, AmazonFixture.Read("blocked-automated-access.html")));
        Assert.Equal(AmazonAnswer.Unavailable, Classify(503, "<html>Service Unavailable</html>"));
        Assert.Equal(AmazonAnswer.Unavailable, Classify(500));
    }

    [Fact]
    public void A404_IsNotFound_ThoughAmazons404PageCarriesTheAutomatedAccessComment()
    {
        var page = AmazonFixture.Read("not-found.html");

        Assert.Contains(AmazonPageClassifier.AutomatedAccessMarker, page);
        Assert.Equal(AmazonAnswer.NotFound, Classify(404, page));
    }

    [Fact]
    public void ACheckWithNoFormToAnswer_OrTheFirewallsHeader_IsAChallenge()
    {
        Assert.Equal(AmazonAnswer.Challenge, Classify(200, AmazonFixture.Read("blocked-script-check.html")));
        Assert.Equal(AmazonAnswer.Challenge, Classify(202, "", headers: ["x-amzn-waf-action"]));
    }

    [Fact]
    public void A429_IsThrottled()
    {
        Assert.Equal(AmazonAnswer.Throttled, Classify(429));
    }

    [Theory]
    [InlineData("/ap/signin?openid.return_to=x", "SignInWall")]
    [InlineData("https://www.amazon.com/ap/signin", "SignInWall")]
    [InlineData("/errors/validateCaptcha?amzn=x", "RobotCheck")]
    [InlineData("/errors_page/validateCaptcha", "RobotCheck")]
    [InlineData("/Anker-Charging-Cable/dp/B088NRLMPV?th=1", "Redirect")]
    [InlineData("https://www.amazon.com/dp/B09LCJPZ1P", "Redirect")]
    [InlineData("/gp/product/B088NRLMPV", "Redirect")]
    [InlineData("/gp/cart/view.html", "Blocked")]
    [InlineData("/", "Blocked")]
    [InlineData("http://www.amazon.com/dp/B088NRLMPV", "Blocked")]
    [InlineData("https://smile.amazon.com/dp/B088NRLMPV", "Blocked")]
    [InlineData("https://example.com/dp/B088NRLMPV", "Blocked")]
    [InlineData("https://www.amazon.com:8443/dp/B088NRLMPV", "Blocked")]
    [InlineData("https://user@www.amazon.com/dp/B088NRLMPV", "Blocked")]
    public void ARedirect_IsFollowedOnlyToAnotherProductPageOnAmazonOverHttps(string location, string expected)
    {
        Assert.Equal(expected, Classify(301, "", location).ToString());
        Assert.Equal(expected, Classify(302, "", location).ToString());
    }

    [Theory]
    [InlineData(403)]
    [InlineData(202)]
    [InlineData(301)] // a redirect that names no target
    public void AnythingElseAReaderHasNoBusinessGetting_IsBlocked(int status)
    {
        Assert.Equal(AmazonAnswer.Blocked, Classify(status));
    }
}

public class AmazonAsinTests
{
    [Theory]
    [InlineData("B088NRLMPV")]
    [InlineData(" b088nrlmpv ")]
    [InlineData("https://www.amazon.com/dp/B088NRLMPV")]
    [InlineData("https://www.amazon.com/Anker-Charging-MacBook-Galaxy-Charger/dp/B088NRLMPV/ref=sr_1_3?crid=A48&th=1")]
    [InlineData("https://www.amazon.com/gp/product/B088NRLMPV?psc=1")]
    [InlineData("https://www.amazon.com/clp/B088NRLMPV")]
    [InlineData("https://amazon.com/dp/B088NRLMPV/")]
    [InlineData("0135972264")]                                  // a book: its ISBN-10
    public void AnAsin_InAnyLinkShape_IsTheSameProduct(string product)
    {
        Assert.Equal(product == "0135972264" ? "0135972264" : "B088NRLMPV", AmazonClient.ParseAsin(product));
    }

    [Theory]
    [InlineData("")]
    [InlineData("usb c cable")]
    [InlineData("B088NRLMP")]                                   // nine characters
    [InlineData("headphones")]                                  // ten letters, but no ASIN has no digit
    [InlineData("https://example.com/dp/B088NRLMPV")]           // not Amazon
    [InlineData("https://www.amazon.com.evil.test/dp/B088NRLMPV")]
    [InlineData("https://www.amazon.com/gp/cart/view.html")]
    [InlineData("ftp://www.amazon.com/dp/B088NRLMPV")]
    public void AnythingElse_IsRefused(string product)
    {
        var refused = Assert.Throws<McpException>(() => AmazonClient.ParseAsin(product));

        Assert.Contains("is not an Amazon product", refused.Message);
    }
}
