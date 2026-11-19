using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplex.Tests.Auth;
using Multiplex.Tests.Services.TripIt;
using Multiplex.Web.Services.LinkedIn;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// The real app, with its real OAuth protection at <c>/linkedin/mcp</c>, in front of
/// <see cref="FakeLinkedIn"/>. Each test account connects through the actual register,
/// authorize and token endpoints.
/// </summary>
public sealed class LinkedInTestApp : AuthTestApp
{
    public const string Resource = BaseUrl + "/linkedin/mcp";
    private readonly ConcurrentDictionary<string, Task<string>> _tokens = new();

    public FakeLinkedIn LinkedIn { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILinkedInApiFactory>();
            services.AddSingleton<ILinkedInApiFactory>(LinkedIn);
        });
    }

    /// <summary>An MCP client connected as LinkedIn account <paramref name="account"/>.</summary>
    public McpTestClient McpFor(string account) =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }),
            () => _tokens.GetOrAdd(account, async a =>
                (await new OAuthDriver(this).ConnectAsync(OAuthDriver.Account(a, "linkedin"), Resource, "linkedin")).AccessToken),
            "/linkedin/mcp");
}

/// <summary>A stand-in LinkedIn: one <see cref="FakeLinkedInAccount"/> per signed-in account.</summary>
public sealed class FakeLinkedIn : ILinkedInApiFactory
{
    private readonly ConcurrentDictionary<string, FakeLinkedInAccount> _accounts = new(StringComparer.Ordinal);

    public FakeLinkedInAccount Account(string accountId) => _accounts.GetOrAdd(accountId, _ => new FakeLinkedInAccount());

    public Task<ILinkedInApi> CreateAsync(string accountId, IServiceProvider requestServices, CancellationToken ct)
        => Task.FromResult<ILinkedInApi>(Account(accountId));
}

/// <summary>One account's invitations, members and conversations; every change is logged in <see cref="Changes"/>.</summary>
public sealed class FakeLinkedInAccount : ILinkedInApi
{
    private readonly object _gate = new();

    public List<LinkedInInvitation> Received { get; } = [];
    public List<LinkedInInvitation> Sent { get; } = [];
    public Dictionary<string, LinkedInProfile> Profiles { get; } = new(StringComparer.Ordinal);
    public List<LinkedInConversation> Conversations { get; } = [];
    public Dictionary<string, List<LinkedInMessage>> Messages { get; } = new(StringComparer.Ordinal);

    /// <summary>Every change LinkedIn was asked to make, in order, e.g. <c>accept inv-1</c>.</summary>
    public List<string> Changes { get; } = [];

    /// <summary>Thrown by the next call, to play LinkedIn failing.</summary>
    public Exception? Fail { get; set; }

    public static LinkedInPerson Person(string id, string name, string? headline = null) => new(id, name, headline);

    public void AddProfile(LinkedInPerson person, ConnectionDegree degree, bool? following = false, bool? invitationPending = false)
        => Profiles[person.ProfileId] = new LinkedInProfile(person, degree, following, invitationPending);

    public Task<LinkedInInvitationPage> ListInvitationsAsync(InvitationDirection direction, int start, int count, CancellationToken ct)
    {
        ThrowIfFailing();
        var all = direction == InvitationDirection.Received ? Received : Sent;
        var page = all.Skip(start).Take(count).ToList();
        return Task.FromResult(new LinkedInInvitationPage(page, start + page.Count < all.Count ? start + page.Count : null));
    }

    public Task<LinkedInInvitation?> FindReceivedInvitationAsync(string invitationId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Received.FirstOrDefault(i => i.InvitationId == invitationId));
    }

    public Task AcceptInvitationAsync(string invitationId, CancellationToken ct) => Change($"accept {invitationId}",
        () => Received.RemoveAll(i => i.InvitationId == invitationId));

    public Task IgnoreInvitationAsync(string invitationId, CancellationToken ct) => Change($"ignore {invitationId}",
        () => Received.RemoveAll(i => i.InvitationId == invitationId));

    public Task<LinkedInProfile?> FindProfileAsync(string profileId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Profiles.GetValueOrDefault(profileId));
    }

    public Task SendInvitationAsync(string profileId, string? note, CancellationToken ct)
        => Change($"invite {profileId} note={note ?? "(none)"}", () => { });

    public Task SetFollowingAsync(string profileId, bool follow, CancellationToken ct)
        => Change($"{(follow ? "follow" : "unfollow")} {profileId}", () => { });

    public Task<LinkedInConversationPage> ListConversationsAsync(string? cursor, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(new LinkedInConversationPage(Conversations, null));
    }

    public Task<LinkedInConversation?> FindConversationAsync(string conversationId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(Conversations.FirstOrDefault(c => c.ConversationId == conversationId));
    }

    public Task<LinkedInMessagePage> ReadConversationAsync(string conversationId, string? olderCursor, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(new LinkedInMessagePage(Messages.GetValueOrDefault(conversationId) ?? [], null));
    }

    public Task ReplyAsync(string conversationId, string text, CancellationToken ct)
        => Change($"reply {conversationId}: {text}", () => { });

    public async Task<string?> StartConversationAsync(string profileId, string text, CancellationToken ct)
    {
        await Change($"start {profileId}: {text}", () => { });
        return "conv-new";
    }

    private Task Change(string what, Action apply)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            Changes.Add(what);
            apply();
        }
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Fail is { } fail)
        {
            Fail = null;
            throw fail;
        }
    }
}
