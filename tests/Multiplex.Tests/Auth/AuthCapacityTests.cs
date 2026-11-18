using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

    [Fact]
    public async Task Authorize_OneCallerCannotHoldMoreThanItsShare_WhileOthersStillGetIn()
    {
        using var app = new TunnelledApp();
        var first = Caller(app, "203.0.113.7");
        var clientId = await first.RegisterAsync();
        for (var i = 0; i < AuthCapacity.PendingAuthorizationsPerCaller; i++)
        {
            await first.StartAsync(AuthorizeParameters(clientId, NewPkce()));
        }

        AssertTemporarilyUnavailable(await first.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce())));

        var second = Caller(app, "198.51.100.4");
        await second.StartAsync(AuthorizeParameters(clientId, NewPkce()));
        AssertTemporarilyUnavailable(await first.AuthorizeAsync(AuthorizeParameters(clientId, NewPkce())));
    }

    [Fact]
    public async Task Authorize_AddressesInOneIPv6Slash64_CountAsOneCaller()
    {
        using var app = new TunnelledApp();
        var clientId = await Caller(app, "2001:db8:1:2::1").RegisterAsync();
        for (var i = 1; i <= AuthCapacity.PendingAuthorizationsPerCaller; i++)
        {
            await Caller(app, $"2001:db8:1:2::{i:x}").StartAsync(AuthorizeParameters(clientId, NewPkce()));
        }

        AssertTemporarilyUnavailable(await Caller(app, "2001:db8:1:2:ffff::99").AuthorizeAsync(AuthorizeParameters(clientId, NewPkce())));
        await Caller(app, "2001:db8:1:3::1").StartAsync(AuthorizeParameters(clientId, NewPkce()));
    }

    [Fact]
    public async Task PendingAuthorization_KeepsOnlyAShortHashOfTheCaller()
    {
        using var app = new TunnelledApp();
        var caller = Caller(app, "203.0.113.7");
        var pendingId = await caller.StartAsync(AuthorizeParameters(await caller.RegisterAsync(), NewPkce()));

        var stored = await app.Services.GetRequiredService<IAuthStore>().ReadAsync(s => s.PendingAuthorizations[pendingId], CancellationToken.None);

        Assert.NotNull(stored.CallerKey);
        Assert.InRange(stored.CallerKey.Length, 8, 22);
        Assert.DoesNotContain("203.0.113", JsonSerializer.Serialize(stored));
    }

    private static OAuthDriver Caller(AuthTestApp app, string address)
    {
        var driver = new OAuthDriver(app);
        driver.Http.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return driver;
    }

    private static void AssertTemporarilyUnavailable(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = Query(response.Headers.Location!);
        Assert.Equal("temporarily_unavailable", query["error"]);
        Assert.Equal("state-123", query["state"]);
    }

    /// <summary>
    /// The app as reached through a tunnel on this PC: every connection is from loopback, so the
    /// caller is whoever <c>X-Forwarded-For</c> names, exactly as behind Tailscale Funnel.
    /// </summary>
    private sealed class TunnelledApp : AuthTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, LoopbackConnections>());
        }

        private sealed class LoopbackConnections : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, rest) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    return rest(context);
                });
                next(app);
            };
        }
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
