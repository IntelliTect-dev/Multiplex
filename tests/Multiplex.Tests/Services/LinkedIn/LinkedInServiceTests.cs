using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplex.Tests.Auth;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.Services.LinkedIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// The LinkedIn service wired as it runs: a tool call for a connected account loads that
/// account's stored session and calls (a fake) LinkedIn through the real client, pacing and
/// error handling; and the session rules shared with the TripIt service.
/// </summary>
public sealed class LinkedInServiceTests : IClassFixture<LinkedInServiceTests.WiredApp>
{
    private const string Member = "ACoAAMember000000000000000000000000000001";
    private readonly WiredApp _app;

    public LinkedInServiceTests(WiredApp app) => _app = app;

    private static string Snapshot(string cookie, string profileId = Member) => JsonSerializer.Serialize(new
    {
        cookie,
        csrf_token = "ajax:" + cookie,
        profile_id = profileId,
        headers = new Dictionary<string, string> { ["user-agent"] = "Test" },
        captured_at = "2026-11-19T12:00:00Z",
    });

    [Fact]
    public async Task A_tool_call_reads_LinkedIn_with_the_connected_accounts_own_session()
    {
        _app.Store.Data[Member] = Snapshot("li_at=member");
        _app.LinkedIn.Respond(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":{"messengerConversationsBySyncToken":{"elements":[]}}}""", Encoding.UTF8, "application/graphql"),
        });

        var (isError, text) = await _app.McpFor(Member).CallAsync("linkedin_list_conversations");

        Assert.False(isError, text);
        Assert.Empty(JsonDocument.Parse(text).RootElement.GetProperty("conversations").EnumerateArray());
        var request = Assert.Single(_app.LinkedIn.Requests);
        Assert.Equal("li_at=member", request.Headers["cookie"]);
        Assert.Contains($"urn%3Ali%3Afsd_profile%3A{Member}", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_with_no_stored_session_is_told_to_reconnect()
    {
        var (isError, text) = await _app.McpFor("ACoAANobody00000000000000000000000000009").CallAsync("linkedin_list_conversations");

        Assert.True(isError);
        Assert.Contains("Reconnect the LinkedIn connector", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_stored_for_another_member_is_never_used()
    {
        var sessions = new LinkedInAccountSessions(TimeProvider.System, Options.Create(new LinkedInServiceOptions()),
            NullLogger<LinkedInAccountSessions>.Instance);
        var store = new MemoryStore { Data = { ["acct"] = Snapshot("li_at=other", "ACoAAOther0000000000000000000000000000008") } };

        var session = await sessions.LoadAsync(Services(store), "acct", CancellationToken.None);

        Assert.False(session.IsConfigured);
    }

    [Fact]
    public async Task A_rejected_session_is_replaced_by_a_newer_stored_one_without_refreshing()
    {
        var store = new MemoryStore { Data = { [Member] = Snapshot("li_at=newer") } };
        var refresher = new CountingRefresher(() => Snapshot("li_at=refreshed"));
        var sessions = new LinkedInAccountSessions(TimeProvider.System, Options.Create(new LinkedInServiceOptions()),
            NullLogger<LinkedInAccountSessions>.Instance);

        var renewed = await sessions.RenewAsync(Services(store, refresher), Member, new LinkedInSession { Cookie = "li_at=old" }, CancellationToken.None);

        Assert.Equal("li_at=newer", renewed?.Cookie);
        Assert.Equal(0, refresher.Calls);
    }

    [Fact]
    public async Task A_signed_out_account_ends_the_session_and_a_second_refresh_waits_for_the_cool_down()
    {
        var store = new MemoryStore { Data = { [Member] = Snapshot("li_at=old") } };
        var rejected = new LinkedInSession { Cookie = "li_at=old" };
        var sessions = new LinkedInAccountSessions(TimeProvider.System, Options.Create(new LinkedInServiceOptions()),
            NullLogger<LinkedInAccountSessions>.Instance);

        await Assert.ThrowsAsync<LinkedInSessionEndedException>(() =>
            sessions.RenewAsync(Services(store, new CountingRefresher(() => null)), Member, rejected, CancellationToken.None));

        var again = new CountingRefresher(() => Snapshot("li_at=new"));
        Assert.Null(await sessions.RenewAsync(Services(store, again), Member, rejected, CancellationToken.None));
        Assert.Equal(0, again.Calls);
    }

    private static IServiceProvider Services(MemoryStore store, ISiteSessionRefresher? refresher = null)
    {
        var services = new ServiceCollection().AddSingleton<ISiteSessionStore>(store);
        if (refresher is not null) services.AddSingleton(refresher);
        return services.BuildServiceProvider();
    }

    /// <summary>The real app and its LinkedIn wiring, with an in-memory session store and a fake LinkedIn behind the real pacing.</summary>
    public sealed class WiredApp : AuthTestApp
    {
        private readonly ConcurrentDictionary<string, Task<string>> _tokens = new();

        public MemoryStore Store { get; } = new();
        public FakeLinkedInHttp LinkedIn { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{LinkedInServiceOptions.Section}:MinRequestSpacingMs", "0");
            builder.UseSetting($"{LinkedInServiceOptions.Section}:RequestJitterMs", "0");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ISiteSessionStore>(Store);
                services.AddHttpClient(LinkedInServiceRegistration.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => LinkedIn);
            });
        }

        public McpTestClient McpFor(string account) =>
            new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
                () => _tokens.GetOrAdd(account, async a =>
                    (await new OAuthDriver(this).ConnectAsync(OAuthDriver.Account(a, "linkedin"), LinkedInTestApp.Resource, "linkedin")).AccessToken),
                "/linkedin/mcp");
    }

    public sealed class MemoryStore : ISiteSessionStore
    {
        public ConcurrentDictionary<string, string> Data { get; } = new();

        public Task<string?> GetAsync(string site, string accountId, CancellationToken ct) =>
            Task.FromResult(site == "linkedin" && Data.TryGetValue(accountId, out var json) ? json : null);

        public Task SetAsync(string site, string accountId, string snapshotJson, CancellationToken ct)
        {
            Data[accountId] = snapshotJson;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string site, string accountId, CancellationToken ct)
        {
            Data.TryRemove(accountId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingRefresher(Func<string?> next) : ISiteSessionRefresher
    {
        public int Calls { get; private set; }

        public Task<string?> RefreshAsync(string site, string accountId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(next());
        }
    }
}
