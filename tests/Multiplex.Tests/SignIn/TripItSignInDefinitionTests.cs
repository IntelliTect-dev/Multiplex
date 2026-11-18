using Microsoft.Extensions.Configuration;
using Multiplex.Web.Services.TripIt.SignIn;

namespace Multiplex.Tests.SignIn;

public sealed class TripItSignInDefinitionTests
{
    [Fact]
    public void Single_email_object_is_read()
    {
        var account = TripItSignInDefinition.ParseAccount("""
            {"timestamp":"1","num_bytes":"10","Profile":{"@attributes":{"ref":"ABC123"},"screen_name":"tester",
             "public_display_name":"Test User",
             "ProfileEmailAddresses":{"ProfileEmailAddress":{"address":"test@example.test","is_confirmed":"true","is_primary":"true"}}}}
            """);

        Assert.NotNull(account);
        Assert.Equal("tripit", account.Site);
        Assert.Equal("ABC123", account.AccountId);
        Assert.Equal(["test@example.test"], account.Emails);
        Assert.Equal("Test User", account.DisplayName);
    }

    [Fact]
    public void Email_array_is_read_and_only_confirmed_addresses_are_kept()
    {
        var account = TripItSignInDefinition.ParseAccount("""
            {"Profile":[{"@attributes":{"ref":"R1"},"display_name":"Shown Name",
              "ProfileEmailAddresses":{"ProfileEmailAddress":[
                {"address":"first@example.test","is_confirmed":"true"},
                {"address":"unflagged@example.test"},
                {"address":"claimed@example.test","is_confirmed":"false"},
                {"address":"third@example.test","is_confirmed":"TRUE"},
                {"address":"FIRST@example.test","is_confirmed":"true"}]}}]}
            """);

        Assert.NotNull(account);
        Assert.Equal("R1", account.AccountId);
        Assert.Equal(["first@example.test", "third@example.test"], account.Emails);
        Assert.Equal("Shown Name", account.DisplayName);
    }

    /// <summary>
    /// The allowlist matches on these addresses, so anything short of an explicit "true" must
    /// keep an address out: an address someone added but never confirmed must not unlock the server.
    /// </summary>
    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("true", true)]          // a JSON boolean
    [InlineData(null, false)]            // no is_confirmed at all
    [InlineData("null", false)]
    [InlineData("\"\"", false)]
    [InlineData("\"false\"", false)]
    [InlineData("false", false)]
    [InlineData("\"0\"", false)]
    [InlineData("\"1\"", false)]
    [InlineData("1", false)]
    [InlineData("\"no\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("{}", false)]
    public void Address_counts_only_when_is_confirmed_is_exactly_true(string? isConfirmedJson, bool counted)
    {
        var flag = isConfirmedJson is null ? "" : "," + "\"is_confirmed\":" + isConfirmedJson;
        var account = TripItSignInDefinition.ParseAccount(
            """{"Profile":{"@attributes":{"ref":"R1"},"ProfileEmailAddresses":{"ProfileEmailAddress":{"address":"owner@example.test" """
            + flag + "}}}}");

        Assert.NotNull(account);
        Assert.Equal(counted, account.Emails.Contains("owner@example.test"));
    }

    [Fact]
    public void Screen_name_is_the_fallback_id_and_name()
    {
        var account = TripItSignInDefinition.ParseAccount("""{"Profile":{"screen_name":"tester"}}""");

        Assert.NotNull(account);
        Assert.Equal("tester", account.AccountId);
        Assert.Equal("tester", account.DisplayName);
        Assert.Empty(account.Emails);
    }

    [Theory]
    [InlineData("""{"Profile":{}}""")]
    [InlineData("""{"Error":{"code":"401"}}""")]
    [InlineData("<html>Access Denied</html>")]
    [InlineData("")]
    public void No_profile_means_no_account(string body)
    {
        Assert.Null(TripItSignInDefinition.ParseAccount(body));
    }

    [Fact]
    public void Defaults_match_what_tripit_serves()
    {
        var definition = new TripItSignInDefinition(new ConfigurationBuilder().Build());

        Assert.Equal("tripit", definition.Slug);
        Assert.Equal("https://www.tripit.com/account/login?redirect_url=https%3A%2F%2Fwww.tripit.com%2Fapp%2Ftrips", definition.SignInStartUrl.OriginalString);
        Assert.Equal("https://www.tripit.com/app/trips", definition.RefreshUrl.AbsoluteUri);
        Assert.Equal(["www.tripit.com", "tripit.com", "accounts.google.com"], definition.AllowedNavigationHosts);
    }

    [Fact]
    public void Allowed_navigation_hosts_can_be_configured()
    {
        var definition = new TripItSignInDefinition(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Multiplex:Sites:tripit:AllowedNavigationHosts:0"] = "www.tripit.com",
            ["Multiplex:Sites:tripit:AllowedNavigationHosts:1"] = "appleid.apple.com",
        }).Build());

        Assert.Equal(["www.tripit.com", "appleid.apple.com"], definition.AllowedNavigationHosts);
    }
}
