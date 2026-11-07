using System.Management.Automation;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Lists the people a trip is shared with, and their roles.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItParticipant")]
[OutputType(typeof(TripParticipant))]
public sealed class GetTripItParticipantCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Uuid")]
    public string TripUuid { get; set; } = "";

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var me = RunSync(client.GetMyProfileRefAsync);
        var detail = RunSync(ct => client.Trips.GetAsync(TripUuid, includeObjects: false, ct));
        foreach (var p in TripsClient.Participants(detail, me))
        {
            var pso = PSObject.AsPSObject(p);
            pso.Properties.Add(new PSNoteProperty("TripUuid", TripUuid));
            WriteObject(pso);
        }
    }
}

/// <summary>
/// <para type="synopsis">Shares a trip with people by email. TripIt emails each address an invitation.</para>
/// </summary>
[Cmdlet(VerbsCommon.Add, "TripItParticipant", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class AddTripItParticipantCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Uuid")]
    public string TripUuid { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1)] public string[] Email { get; set; } = [];

    [Parameter] [ValidateSet("Viewer", "Editor", "Traveler")] public string Role { get; set; } = "Viewer";

    [Parameter] public string? Message { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(string.Join(", ", Email), $"Email a TripIt invitation ({Role}) for trip {TripUuid}")) return;
        var client = GetClient();
        RunSync(ct => client.Trips.InviteAsync(TripUuid, Email, TripsClient.ParseRole(Role), Message, ct));
    }
}

/// <summary>
/// <para type="synopsis">Changes a participant's role on a trip (Viewer, Editor or Traveler).</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "TripItParticipant", SupportsShouldProcess = true)]
public sealed class SetTripItParticipantCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public string TripUuid { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)] public string ProfileRef { get; set; } = "";
    [Parameter(Mandatory = true)] [ValidateSet("Viewer", "Editor", "Traveler")] public string Role { get; set; } = "Viewer";

    protected override void ProcessRecord()
    {
        if (!ShouldProcess($"{ProfileRef} on {TripUuid}", $"Set role to {Role}")) return;
        var client = GetClient();
        RunSync(ct => client.Trips.ChangeRoleAsync(TripUuid, ProfileRef, TripsClient.ParseRole(Role), ct));
    }
}

/// <summary>
/// <para type="synopsis">Removes a participant from a trip.</para>
/// </summary>
[Cmdlet(VerbsCommon.Remove, "TripItParticipant", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveTripItParticipantCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public string TripUuid { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)] public string ProfileRef { get; set; } = "";

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var me = RunSync(client.GetMyProfileRefAsync);
        var detail = RunSync(ct => client.Trips.GetAsync(TripUuid, includeObjects: false, ct));
        var target = TripsClient.Participants(detail, me).FirstOrDefault(p => p.ProfileRef == ProfileRef)
                     ?? throw new ArgumentException($"{ProfileRef} is not a participant of trip {TripUuid}.");
        if (target.IsOwner) throw new ArgumentException("The trip owner cannot be removed. Use Remove-TripItTrip instead.");
        if (!ShouldProcess($"{target.Name ?? target.ScreenName ?? ProfileRef} from '{detail.Trip.DisplayName}'", "Remove TripIt participant")) return;
        RunSync(ct => client.Trips.RemoveParticipantAsync(TripUuid, ProfileRef, ct));
    }
}

/// <summary>
/// <para type="synopsis">Merges one trip into another; the source trip is deleted.</para>
/// </summary>
[Cmdlet(VerbsData.Merge, "TripItTrip", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
[OutputType(typeof(TripDetail))]
public sealed class MergeTripItTripCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)] public string SourceUuid { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1)] public string DestinationUuid { get; set; } = "";

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var source = RunSync(ct => client.Trips.GetAsync(SourceUuid, includeObjects: true, ct));
        var dest = RunSync(ct => client.Trips.GetAsync(DestinationUuid, includeObjects: false, ct));
        if (!ShouldProcess(
                $"'{source.Trip.DisplayName}' ({source.ObjectCount} plan(s)) into '{dest.Trip.DisplayName}'",
                "Merge TripIt trips (the source trip is deleted)"))
        {
            return;
        }
        WriteObject(RunSync(ct => client.Trips.MergeAsync(SourceUuid, DestinationUuid, ct)));
    }
}
