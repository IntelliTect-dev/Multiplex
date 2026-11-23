using Multiplex.Web.HtmlSites;

namespace Multiplex.Tests.HtmlSites;

public class RobotsRulesTests
{
    private static readonly string[] Us = ["Multiplex-Amazon", "Multiplex"];

    [Fact]
    public void TheStarGroup_DisallowingAPath_RefusesIt()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /s\n");

        Assert.False(rules.IsAllowed("/s?k=cable", Us));
        Assert.True(rules.IsAllowed("/dp/B088NRLMPV", Us));
    }

    [Fact]
    public void AGroupNamingTheService_WinsOverTheStarGroup()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow:\n\nUser-agent: multiplex-amazon\nDisallow: /\n");

        Assert.False(rules.IsAllowed("/dp/B088NRLMPV", Us));
        Assert.True(rules.IsAllowed("/dp/B088NRLMPV", "SomeoneElse"));
    }

    [Fact]
    public void AGroupNamingOnlyAnotherAgent_IsIgnored()
    {
        var rules = RobotsRules.Parse("User-agent: Claude-User\nDisallow: /\n\nUser-agent: *\nDisallow: /gp/cart\n");

        Assert.True(rules.IsAllowed("/s?k=cable", Us));
        Assert.False(rules.IsAllowed("/gp/cart/view.html", Us));
    }

    [Fact]
    public void TheLongestMatchWins_AndAllowWinsATie()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /wishlist\nAllow: /wishlist/universal\nDisallow: /a\nAllow: /a\n");

        Assert.False(rules.IsAllowed("/wishlist/mine", Us));
        Assert.True(rules.IsAllowed("/wishlist/universal-button", Us));
        Assert.True(rules.IsAllowed("/a", Us));
    }

    [Fact]
    public void AStarMatchesAnyRun_AndADollarAnchorsTheEnd()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /gp/registry/wishlist/*/reserve\nDisallow: /slp/s$\n");

        Assert.False(rules.IsAllowed("/gp/registry/wishlist/ABC123/reserve", Us));
        Assert.True(rules.IsAllowed("/gp/registry/wishlist/ABC123", Us));
        Assert.False(rules.IsAllowed("/slp/s", Us));
        Assert.True(rules.IsAllowed("/slp/sale", Us));
    }

    [Fact]
    public void SeveralUserAgentLines_ShareOneGroup_AndCommentsAreIgnored()
    {
        var rules = RobotsRules.Parse("# a comment\nUser-agent: GPTBot\nUser-agent: Multiplex # ours\nDisallow: /private # keep out\n");

        Assert.False(rules.IsAllowed("/private/x", Us));
        Assert.True(rules.IsAllowed("/public", Us));
    }

    [Fact]
    public void AmazonsOwnStarGroup_AllowsSearchAndProductPages()
    {
        var rules = RobotsRules.Parse(File.ReadAllText(Path.Combine("Services", "Amazon", "Fixtures", "robots.txt")));

        Assert.True(rules.IsAllowed("/s?k=usb+c+charging+cable&s=price-asc-rank&rh=p_72%3A1248879011", Us));
        Assert.True(rules.IsAllowed("/dp/B088NRLMPV", Us));
        Assert.True(rules.IsAllowed("/robots.txt", Us));
        Assert.False(rules.IsAllowed("/gp/cart/view.html", Us));
        // Amazon names the AI fetchers and refuses them everything; this service does not go by their names.
        Assert.False(rules.IsAllowed("/dp/B088NRLMPV", "Claude-User"));
    }

    [Fact]
    public void ARuleTooTangledToEvaluate_RefusesThePath()
    {
        var rules = RobotsRules.Parse("User-agent: *\nAllow: /" + string.Concat(Enumerable.Repeat("*a", 40)) + "$\n");

        Assert.False(rules.IsAllowed("/" + new string('a', 60) + "b", Us));
    }

    [Fact]
    public void NoRules_AllowEverything()
    {
        Assert.True(RobotsRules.AllowAll.IsAllowed("/anything", Us));
    }
}
