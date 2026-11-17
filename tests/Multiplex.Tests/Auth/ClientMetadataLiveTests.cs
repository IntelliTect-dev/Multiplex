using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Auth.Clients;

namespace Multiplex.Tests.Auth;

/// <summary>A fact that fetches from the internet; opt in with MULTIPLEX_LIVE_TESTS=1 (no browser needed).</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MULTIPLEX_LIVE_TESTS") != "1")
            Skip = "Live test. Set MULTIPLEX_LIVE_TESTS=1 to fetch real client metadata documents.";
    }
}

/// <summary>The real fetcher (public-address connect, HTTPS-only redirects) against ChatGPT's published document.</summary>
public class ClientMetadataLiveTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [NetworkFact]
    public async Task ChatGptClientMetadataDocument_PassesValidation()
    {
        const string clientId = "https://chatgpt.com/oauth/client.json";
        var documents = factory.Services.GetRequiredService<ClientMetadataDocuments>();

        var lookup = await documents.GetAsync(clientId, CancellationToken.None);

        Assert.True(lookup.Client is not null, lookup.Error);
        Assert.NotEmpty(lookup.Client.RedirectUris);
    }
}
