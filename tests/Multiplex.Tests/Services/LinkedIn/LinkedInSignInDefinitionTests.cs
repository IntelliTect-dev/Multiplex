using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Multiplex.Web.Services.LinkedIn;
using Multiplex.Web.Services.LinkedIn.SignIn;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// What LinkedIn's sign-in looks like to the connect pages: who is signed in (from the
/// <c>/voyager/api/me</c> answer, in its recorded shape with a made-up member), and which pages
/// mean "signed out". A security check is never "signed out": that would end the connection.
/// </summary>
public sealed class LinkedInSignInDefinitionTests
{
    private static readonly LinkedInSignInDefinition s_definition = new(new ConfigurationBuilder().Build());

    private static string Me(string profileId = "ACoAAMember000000000000000000000000000001") => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["data"] = new Dictionary<string, object?>
        {
            ["plainId"] = 1234567,
            ["*miniProfile"] = "urn:li:fs_miniProfile:" + profileId,
            ["$type"] = "com.linkedin.voyager.common.Me",
        },
        ["included"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["entityUrn"] = "urn:li:fs_miniProfile:" + profileId,
                ["dashEntityUrn"] = "urn:li:fsd_profile:" + profileId,
                ["firstName"] = "Mark",
                ["lastName"] = "Member",
                ["occupation"] = "Engineer",
                ["$type"] = "com.linkedin.voyager.identity.shared.MiniProfile",
            },
        },
    });

    [Fact]
    public void Hint_warns_that_a_google_or_microsoft_password_goes_through_this_server_too()
    {
        var hint = s_definition.SignInHint;

        Assert.NotNull(hint);
        Assert.Contains("If you sign in with Google or Microsoft here, that password goes through this server too.", hint, StringComparison.Ordinal);
        Assert.Contains("approve it there", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_signed_in_member_is_their_profile_id_and_name()
    {
        var account = LinkedInSignInDefinition.ParseAccount(Me());

        Assert.NotNull(account);
        Assert.Equal("linkedin", account.Site);
        Assert.Equal("ACoAAMember000000000000000000000000000001", account.AccountId);
        Assert.Equal("Mark Member", account.DisplayName);
        Assert.Empty(account.Emails);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"*miniProfile":"urn:li:fs_miniProfile:X"},"included":[]}""")]
    public void An_answer_without_a_member_signs_no_one_in(string json)
        => Assert.Null(LinkedInSignInDefinition.ParseAccount(json));

    [Theory]
    [InlineData("https://www.linkedin.com/login", true)]
    [InlineData("https://www.linkedin.com/login?session_redirect=https%3A%2F%2Fwww.linkedin.com%2Ffeed%2F", true)]
    [InlineData("https://www.linkedin.com/uas/login?trk=x", true)]
    [InlineData("https://www.linkedin.com/authwall?trk=x", true)]
    [InlineData("https://www.linkedin.com/checkpoint/challenge/AgE?ut=x", false)]
    [InlineData("https://www.linkedin.com/checkpoint/lg/login-submit", false)]
    [InlineData("https://www.linkedin.com/feed/", false)]
    [InlineData("https://evil.example/login", false)]
    [InlineData("chrome-error://chromewebdata/", false)]
    public void Only_a_sign_in_page_means_signed_out(string url, bool signedOut)
        => Assert.Equal(signedOut, LinkedInSignInDefinition.IsSignInPage(url));

    [Fact]
    public void The_sign_in_starts_on_LinkedIns_own_page_and_stays_on_LinkedIn_and_its_identity_providers()
    {
        Assert.Equal("https://www.linkedin.com/login", s_definition.SignInStartUrl.AbsoluteUri);
        Assert.Contains("www.linkedin.com", s_definition.AllowedNavigationHosts);
        Assert.DoesNotContain(s_definition.AllowedNavigationHosts, h => h.Contains('*', StringComparison.Ordinal));
    }

    [Fact]
    public void A_snapshot_keeps_the_browser_headers_but_never_a_cookie_or_token_among_them()
    {
        var json = LinkedInSignInDefinition.BuildSnapshot(
            "li_at=a; JSESSIONID=\"ajax:1\"",
            "\"ajax:1\"",
            "ACoAAMember000000000000000000000000000001",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 Test",
                ["x-li-track"] = "{}",
                ["Cookie"] = "leak",
                ["csrf-token"] = "leak",
                ["x-li-page-instance"] = "urn:li:page:x",
                ["referer"] = "https://www.linkedin.com/feed/",
            },
            DateTimeOffset.Parse("2026-11-19T12:00:00Z"));

        Assert.True(LinkedInSession.TryFromSnapshotJson(json, out var session, out var error), error);
        Assert.Equal("ajax:1", session.CsrfToken);
        Assert.Equal("Mozilla/5.0 Test", session.Headers["user-agent"]);
        Assert.True(session.Headers.ContainsKey("x-li-track"));
        Assert.DoesNotContain("leak", json, StringComparison.Ordinal);
        Assert.False(session.Headers.ContainsKey("x-li-page-instance"));
        Assert.False(session.Headers.ContainsKey("referer"));
    }
}
