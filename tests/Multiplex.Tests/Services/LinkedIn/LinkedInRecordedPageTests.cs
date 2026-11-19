using Multiplex.Web.Services.LinkedIn;
using Xunit.Abstractions;

namespace Multiplex.Tests.Services.LinkedIn;

/// <summary>
/// Opt-in: reads a real, locally saved LinkedIn invitations page (and the answer to opening
/// LinkedIn's accept warning) with the same reader the service uses. Recordings hold other
/// people's names, so they never enter the repository; point the variables at local copies:
/// <c>LINKEDIN_RECORDED_INVITATIONS_HTML</c> and, optionally, <c>LINKEDIN_RECORDED_WARNING</c>.
/// Prints only counts and yes/no checks.
/// </summary>
public sealed class LinkedInRecordedPageTests(ITestOutputHelper output)
{
    [Fact]
    public void A_recorded_invitations_page_reads_with_every_person_tied_to_their_own_invitation()
    {
        if (Environment.GetEnvironmentVariable("LINKEDIN_RECORDED_INVITATIONS_HTML") is not { Length: > 0 } path) return;

        var invitations = LinkedInInvitationsPage.Parse(File.ReadAllText(path));

        output.WriteLine($"invitations: {invitations.Count}, with headline: {invitations.Count(i => i.Person.Headline is not null)}, " +
                         $"with note: {invitations.Count(i => i.Note is not null)}, warned: {invitations.Count(i => i.WarningTitle is not null)}");
        Assert.NotEmpty(invitations);
        Assert.All(invitations, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Person.Name));
            Assert.StartsWith("ACoA", i.Person.ProfileId, StringComparison.Ordinal);
            Assert.Contains(i.InvitationId, i.IgnoreAction, StringComparison.Ordinal);
            Assert.True(i.AcceptAction is not null ^ i.WarningNavigation is not null);
            if (i.AcceptAction is not null) Assert.Contains(i.InvitationId, i.AcceptAction, StringComparison.Ordinal);
            if (i.WarningNavigation is not null) Assert.Contains(i.InvitationId, i.WarningNavigation, StringComparison.Ordinal);
        });
        Assert.Equal(invitations.Count, invitations.Select(i => i.InvitationId).Distinct().Count());
    }

    [Fact]
    public void A_recorded_warning_answer_carries_the_accept_action_for_its_invitation()
    {
        if (Environment.GetEnvironmentVariable("LINKEDIN_RECORDED_WARNING") is not { Length: > 0 } path) return;

        var flight = File.ReadAllText(path);
        var actions = LinkedInSdui.Objects(flight,
            """{"$type":"proto.sdui.actions.core.ServerRequest","value":{"requestId":"com.linkedin.sdui.requests.mynetwork.addaInvitationAction""").ToList();

        output.WriteLine($"accept actions in the warning answer: {actions.Count}");
        var action = Assert.Single(actions);
        Assert.Contains("InviteeActionType_ACCEPT", action, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"com.linkedin.sdui.requests.mynetwork.addaInvitationAction\"",
            LinkedInClient.ActionEnvelope(System.Text.Json.JsonDocument.Parse(action).RootElement.GetProperty("value").GetRawText(), "screen"),
            StringComparison.Ordinal);
    }
}
