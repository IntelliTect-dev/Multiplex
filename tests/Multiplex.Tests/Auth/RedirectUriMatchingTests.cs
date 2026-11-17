using Multiplex.Web.Auth.Clients;

namespace Multiplex.Tests.Auth;

public class RedirectUriMatchingTests
{
    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback")]
    [InlineData("http://127.0.0.1:33418/callback", "http://127.0.0.1:33418/callback")]
    [InlineData("http://127.0.0.1:33418/callback", "http://127.0.0.1:51007/callback")] // RFC 8252 7.3: any loopback port
    [InlineData("http://localhost:6276/oauth/callback", "http://localhost:6300/oauth/callback")]
    [InlineData("http://[::1]:5000/cb", "http://[::1]:5001/cb")]
    public void Matches(string registered, string requested) =>
        Assert.True(RedirectUris.Matches(registered, requested));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect", "https://chatgpt.com:8443/connector_platform_oauth_redirect")] // ports only float on loopback
    [InlineData("http://127.0.0.1:33418/callback", "http://127.0.0.1:33418/other")]
    [InlineData("http://127.0.0.1:33418/callback", "http://localhost:33418/callback")]
    [InlineData("http://127.0.0.1:33418/callback", "https://127.0.0.1:33418/callback")]
    [InlineData("http://127.0.0.1:33418/callback", "http://127.0.0.1:33418/callback?x=1")]
    [InlineData("http://127.0.0.1:33418/callback", "http://evil@127.0.0.1:33418/callback")]
    [InlineData("http://127.0.0.1:33418/callback", "http://127.0.0.2:33418/callback")]
    public void DoesNotMatch(string registered, string requested) =>
        Assert.False(RedirectUris.Matches(registered, requested));
}
