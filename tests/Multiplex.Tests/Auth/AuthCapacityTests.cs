using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.Auth;
using Multiplex.Web.Auth.Store;
using static Multiplex.Tests.Auth.OAuthDriver;

namespace Multiplex.Tests.Auth;

/// <summary>
/// Callers who have not signed in can make the server store registered clients and pending
/// authorizations; both are capped, and when full new ones are refused while what exists keeps working.
/// </summary>
public class AuthCapacityTests
{
    [Fact]
    public async Task Register_WhenTheClientRegistryIsFull_IsRefusedAndExistingClientsKeepWorking()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        await FillAsync(app, AuthCapacity.RegisteredClients - 1, (s, i, now) =>
            s.Clients[$"filler-{i}"] = new RegisteredClient($"filler-{i}", null, [ClaudeRedirect], now, now));

        var last = await driver.RegisterAsync();
        var refused = await driver.RegisterRawAsync(new { redirect_uris = new[] { ClaudeRedirect } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
        Assert.Equal("temporarily_unavailable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        await driver.StartAsync(AuthorizeParameters(last, NewPkce()));

        // Unused registrations age out after 90 days, and registration opens again.
        app.Clock.Advance(TimeSpan.FromDays(91));
        await driver.RegisterAsync();
    }

    [Fact]
    public async Task Authorize_WhenTooManyAuthorizationsArePending_RedirectsWithTemporarilyUnavailable()
    {
        using var app = new AuthTestApp();
        var driver = new OAuthDriver(app);
        var clientId = await driver.RegisterAsync();
        await FillAsync(app, AuthCapacity.PendingAuthorizations - 1, (s, i, now) =>
            s.PendingAuthorizations[$"filler-{i}"] = new PendingAuthorizationRecord(
                $"filler-{i}", "tripit", clientId, "Claude", ClaudeRedirect, "s", "c", AuthTestApp.TripItResource, "tripit",
                now + TimeSpan.FromMinutes(10)));

        var pendingId = await driver.StartAsync(AuthorizeParameters(clientId, NewPkce()));
        var refused = await driver.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce()));

        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        var query = Query(refused.Headers.Location!);
        Assert.Equal("temporarily_unavailable", query["error"]);
        Assert.Equal("state-123", query["state"]);
        Assert.Equal(AuthTestApp.BaseUrl, query["iss"]);

        // An authorization already started still finishes, which frees its place.
        await driver.Completer.CompleteAsync(pendingId, Account(), CancellationToken.None);
        await driver.StartAsync(AuthorizeParameters(clientId, NewPkce()));
    }

    private static Task FillAsync(AuthTestApp app, int count, Action<AuthState, int, DateTimeOffset> add)
    {
        var now = app.Clock.GetUtcNow();
        return app.Services.GetRequiredService<IAuthStore>().WriteAsync(s =>
        {
            for (var i = 0; i < count; i++) add(s, i, now);
            return StoreWrite.Changed(count);
        }, CancellationToken.None);
    }
}
