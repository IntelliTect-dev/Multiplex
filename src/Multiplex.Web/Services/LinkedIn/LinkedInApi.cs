namespace Multiplex.Web.Services.LinkedIn;

/// <summary>A LinkedIn member as the tools show them.</summary>
/// <param name="ProfileId">The member's public profile id: the part of their profile URL after <c>/in/</c>.</param>
/// <param name="Name">Their name as LinkedIn shows it.</param>
/// <param name="Headline">Their headline, when LinkedIn sends one.</param>
public sealed record LinkedInPerson(string ProfileId, string Name, string? Headline)
{
    /// <summary>The member's profile page, so a person can check who a tool means.</summary>
    public string ProfileUrl => $"https://www.linkedin.com/in/{Uri.EscapeDataString(ProfileId)}/";
}

public enum InvitationDirection
{
    Received,
    Sent,
}

/// <param name="InvitationId">Opaque id the accept and ignore tools take.</param>
/// <param name="Person">Who sent it (received) or who it was sent to (sent).</param>
/// <param name="Note">The note that came with it, if any.</param>
public sealed record LinkedInInvitation(string InvitationId, LinkedInPerson Person, string? Note, DateTimeOffset? SentAt);

/// <param name="NextStart">Where the next page starts, or null on the last page.</param>
public sealed record LinkedInInvitationPage(IReadOnlyList<LinkedInInvitation> Invitations, int? NextStart);

/// <param name="ConversationId">Opaque id the read and reply tools take.</param>
/// <param name="Participants">Everyone in it except the signed-in person.</param>
public sealed record LinkedInConversation(
    string ConversationId,
    IReadOnlyList<LinkedInPerson> Participants,
    string? LastMessage,
    DateTimeOffset? LastActivityAt,
    bool Unread);

/// <param name="NextCursor">Pass back to get the next (older) page, or null on the last page.</param>
public sealed record LinkedInConversationPage(IReadOnlyList<LinkedInConversation> Conversations, string? NextCursor);

/// <param name="From">The sender; null when it is the signed-in person.</param>
public sealed record LinkedInMessage(string MessageId, LinkedInPerson? From, string Text, DateTimeOffset SentAt);

/// <param name="Messages">Oldest first.</param>
/// <param name="OlderCursor">Pass back to get the messages before these, or null when there are none.</param>
public sealed record LinkedInMessagePage(IReadOnlyList<LinkedInMessage> Messages, string? OlderCursor);

/// <summary>
/// The LinkedIn operations the invitation and messaging tools use, for one signed-in account.
/// Every change acts on exactly one invitation or one conversation.
/// </summary>
public interface ILinkedInApi
{
    Task<LinkedInInvitationPage> ListInvitationsAsync(InvitationDirection direction, int start, int count, CancellationToken ct);

    /// <summary>A received invitation that is still waiting, or null when there is none with that id.</summary>
    Task<LinkedInInvitation?> FindReceivedInvitationAsync(string invitationId, CancellationToken ct);

    Task AcceptInvitationAsync(string invitationId, CancellationToken ct);

    Task IgnoreInvitationAsync(string invitationId, CancellationToken ct);

    Task<LinkedInConversationPage> ListConversationsAsync(string? cursor, CancellationToken ct);

    /// <summary>A conversation by id, or null when the account has none with that id.</summary>
    Task<LinkedInConversation?> FindConversationAsync(string conversationId, CancellationToken ct);

    Task<LinkedInMessagePage> ReadConversationAsync(string conversationId, string? olderCursor, CancellationToken ct);

    Task ReplyAsync(string conversationId, string text, CancellationToken ct);
}

/// <summary>Creates the <see cref="ILinkedInApi"/> of one account for one request.</summary>
public interface ILinkedInApiFactory
{
    Task<ILinkedInApi> CreateAsync(string accountId, IServiceProvider requestServices, CancellationToken ct);
}

/// <summary>LinkedIn refused or failed a request; <see cref="Exception.Message"/> is short and safe to show.</summary>
public class LinkedInApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The account is signed out of LinkedIn; the connector must reconnect.</summary>
public sealed class LinkedInSessionEndedException()
    : LinkedInApiException("The LinkedIn session ended (signed out).");

/// <summary>
/// LinkedIn stopped the request with a security check or a block. The service never tries to get
/// past one: the person deals with it on LinkedIn.
/// </summary>
public sealed class LinkedInChallengeException(string message) : LinkedInApiException(message);
