using ModelContextProtocol;
using Multiplex.Web.Services.LinkedIn;

namespace Multiplex.Tests.Services.LinkedIn;

public sealed class LinkedInProfileIdTests
{
    [Theory]
    [InlineData("jane-doe", "jane-doe")]
    [InlineData("  jane-doe  ", "jane-doe")]
    [InlineData("https://www.linkedin.com/in/jane-doe/", "jane-doe")]
    [InlineData("https://www.linkedin.com/in/jane-doe", "jane-doe")]
    [InlineData("www.linkedin.com/in/jane-doe/?trk=people-guest", "jane-doe")]
    [InlineData("linkedin.com/in/jane-doe", "jane-doe")]
    [InlineData("https://de.linkedin.com/in/jane-doe-123/", "jane-doe-123")]
    [InlineData("https://www.linkedin.com/in/%E5%BC%A0%E4%BC%9F/", "张伟")]
    public void A_profile_id_or_profile_URL_gives_the_public_id(string value, string expected)
        => Assert.Equal(expected, LinkedInProfileId.Normalize(value, "person"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jane doe")]
    [InlineData("http://www.linkedin.com/in/jane-doe/")]
    [InlineData("https://www.linkedin.com.evil.example/in/jane-doe/")]
    [InlineData("https://notlinkedin.com/in/jane-doe/")]
    [InlineData("https://www.linkedin.com/company/contoso/")]
    [InlineData("https://www.linkedin.com/in/jane-doe/overlay/contact-info/")]
    [InlineData("https://www.linkedin.com/in/")]
    [InlineData("jane-doe?trk=x")]
    [InlineData("jane-doe#top")]
    [InlineData("..%2Fcompany%2Fcontoso")]
    [InlineData("urn:li:fsd_profile:ACoAAA")]
    public void Anything_else_is_refused(string value)
    {
        var error = Assert.Throws<McpException>(() => LinkedInProfileId.Normalize(value, "person"));
        Assert.StartsWith("person ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlong_id_is_refused()
        => Assert.Throws<McpException>(() => LinkedInProfileId.Normalize(new string('a', 101), "person"));
}
