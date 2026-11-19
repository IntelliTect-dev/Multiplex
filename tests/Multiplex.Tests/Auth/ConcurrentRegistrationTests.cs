using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Auth;
using static Multiplex.Tests.Auth.OAuthDriver;

namespace Multiplex.Tests.Auth;

/// <summary>
/// Apps that connect at the same moment, for example all at once after a restart, are each answered.
/// Every OAuth and discovery response in the process is written with one shared set of JSON settings.
/// </summary>
public class ConcurrentRegistrationTests
{
    private const int Apps = 64;

    /// <summary>
    /// Building a JSON response fills in the type resolver when it is missing on settings that are not yet
    /// locked. Two responses doing that at once on shared settings make one of them answer 500 (#87).
    /// So the shared settings must be complete and locked before the first response is built with them,
    /// and writing a response must leave them as they were.
    /// </summary>
    /// <remarks>
    /// Checked on a fresh instance, so the answer does not depend on which test wrote the first OAuth
    /// response in this process. A load test cannot show this: the gap is too narrow to hit on demand.
    /// </remarks>
    [Fact]
    public async Task ResponseJsonSettings_AreLockedBeforeFirstUse_AndWritingAResponseLeavesThemAsTheyWere()
    {
        var settings = OAuthEndpoints.CreateJsonOptions();

        Assert.True(settings.IsReadOnly, "The shared settings can still be changed by the first response written with them.");
        var resolver = settings.TypeInfoResolver;

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await Results.Json(new { client_id = "abc", client_name = (string?)null }, settings, statusCode: StatusCodes.Status201Created)
            .ExecuteAsync(context);

        Assert.Same(resolver, settings.TypeInfoResolver);
        context.Response.Body.Position = 0;
        Assert.Equal("""{"client_id":"abc"}""", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    /// <summary>
    /// End to end: many apps fetching the discovery document and registering at once on a fresh host
    /// each get their own answer. The race behind #87 is too rare for this to catch it reliably; the
    /// test above is the one that guards it.
    /// </summary>
    [Fact]
    public async Task Connect_ManyAppsAtOnceOnAFreshHost_EachGetsMetadataAndIsRegistered()
    {
        using var app = new AuthTestApp();
        var http = app.Client();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var metadata = Enumerable.Range(0, Apps).Select(_ => Task.Run(async () =>
        {
            await go.Task;
            var response = await http.GetAsync("/.well-known/oauth-authorization-server");
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        })).ToArray();
        var registrations = Enumerable.Range(0, Apps).Select(i => Task.Run(async () =>
        {
            await go.Task;
            var response = await http.PostAsJsonAsync("/oauth/register", new
            {
                client_name = $"App {i}",
                redirect_uris = new[] { ClaudeRedirect },
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
            });
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        })).ToArray();
        go.SetResult();
        var metadataAnswers = await Task.WhenAll(metadata);
        var registrationAnswers = await Task.WhenAll(registrations);

        Assert.All(metadataAnswers, a => Assert.True(a.StatusCode == HttpStatusCode.OK, $"Metadata answered {(int)a.StatusCode}: {a.Body}"));
        Assert.All(registrationAnswers, a => Assert.True(a.StatusCode == HttpStatusCode.Created, $"Register answered {(int)a.StatusCode}: {a.Body}"));
        var clientIds = registrationAnswers.Select(a =>
        {
            using var body = JsonDocument.Parse(a.Body);
            return body.RootElement.GetProperty("client_id").GetString();
        }).ToHashSet();
        Assert.Equal(Apps, clientIds.Count);
    }
}
