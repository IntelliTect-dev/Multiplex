using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;
using TripIt.Json;
using TripIt.Models;

namespace TripIt.PowerShell;

/// <summary>Converts PowerShell input (hashtable, PSCustomObject or JSON string) to a JsonObject.</summary>
internal static class PSJson
{
    public static JsonObject ToJsonObject(object? input, string parameterName)
    {
        var node = ToNode(input is PSObject pso ? pso.BaseObject : input);
        return node as JsonObject
               ?? throw new ArgumentException($"-{parameterName} must be a hashtable, object or JSON object string.");
    }

    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case PSObject pso when pso.BaseObject is PSCustomObject:
            {
                var o = new JsonObject();
                foreach (var p in pso.Properties) o[p.Name] = ToNode(p.Value);
                return o;
            }
            case PSObject pso:
                return ToNode(pso.BaseObject);
            case string s when s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('['):
                return JsonNode.Parse(s);
            case string s:
                return s;
            case IDictionary dict:
            {
                var o = new JsonObject();
                foreach (DictionaryEntry e in dict) o[e.Key.ToString()!] = ToNode(e.Value);
                return o;
            }
            case IEnumerable seq:
                return new JsonArray([.. seq.Cast<object?>().Select(ToNode)]);
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            case IFormattable f:
                return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    /// <summary>Converts a TripIt object to a PSCustomObject (empty fields removed).</summary>
    public static PSObject ToPSObject(PSCmdlet cmdlet, TripItObjectType type, JsonElement obj)
    {
        var json = TripItJson.Prune(obj)?.ToJsonString() ?? "{}";
        var converted = cmdlet.InvokeCommand.InvokeScript("param($j) $j | ConvertFrom-Json -Depth 64", json)
            .FirstOrDefault() ?? new PSObject();
        converted.Properties.Add(new PSNoteProperty("ObjectType", type.ToPathSegment()));
        return converted;
    }
}

/// <summary>
/// <para type="synopsis">Creates a TripIt trip.</para>
/// <para type="description">The trip is private (not shared with the Inner Circle) unless
/// -ShareWithInnerCircle is given.</para>
/// </summary>
[Cmdlet(VerbsCommon.New, "TripItTrip", SupportsShouldProcess = true)]
[OutputType(typeof(Trip))]
public sealed class NewTripItTripCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)] public string Destination { get; set; } = "";
    [Parameter(Mandatory = true)] public DateTime StartDate { get; set; }
    [Parameter(Mandatory = true)] public DateTime EndDate { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public string? Description { get; set; }
    [Parameter] public SwitchParameter ShareWithInnerCircle { get; set; }
    [Parameter] [ValidateSet("Leisure", "Business")] public string Purpose { get; set; } = "Leisure";
    /// <summary>Create the trip with me as organiser/planner rather than traveler.</summary>
    [Parameter] public SwitchParameter NotTraveling { get; set; }

    protected override void ProcessRecord()
    {
        var input = new TripInput
        {
            DisplayName = Name,
            PrimaryLocation = Destination,
            StartDate = StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            EndDate = EndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Description = Description,
            IsPrivate = !ShareWithInnerCircle.IsPresent,
            PurposeTypeCode = Purpose == "Business" ? "B" : "L",
            IsOwnerTraveler = !NotTraveling.IsPresent,
        };
        if (!ShouldProcess($"{Name ?? Destination} ({input.StartDate} – {input.EndDate})", "Create TripIt trip")) return;
        var client = GetClient();
        WriteObject(RunSync(ct => client.Trips.CreateAsync(input, ct)));
    }
}

/// <summary>
/// <para type="synopsis">Changes a trip's name, destination, dates, description or sharing.</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "TripItTrip", SupportsShouldProcess = true)]
[OutputType(typeof(Trip))]
public sealed class SetTripItTripCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public string Uuid { get; set; } = "";
    [Parameter] public string? Name { get; set; }
    [Parameter] public string? Destination { get; set; }
    [Parameter] public DateTime? StartDate { get; set; }
    [Parameter] public DateTime? EndDate { get; set; }
    [Parameter] public string? Description { get; set; }
    [Parameter] public bool? ShareWithInnerCircle { get; set; }
    /// <summary>Whether the trip owner travels ($true) or only plans the trip ($false).</summary>
    [Parameter] public bool? IAmTraveling { get; set; }

    protected override void ProcessRecord()
    {
        if (!ShouldProcess(Uuid, "Update TripIt trip")) return;
        static string? D(DateTime? d) => d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var client = GetClient();
        WriteObject(RunSync(ct => client.Trips.UpdateAsync(Uuid, t => t with
        {
            DisplayName = Name ?? t.DisplayName,
            PrimaryLocation = Destination ?? t.PrimaryLocation,
            StartDate = D(StartDate) ?? t.StartDate,
            EndDate = D(EndDate) ?? t.EndDate,
            Description = Description ?? t.Description,
            IsPrivate = ShareWithInnerCircle is { } s ? !s : t.IsPrivate,
            IsOwnerTraveler = IAmTraveling ?? t.IsOwnerTraveler,
        }, ct)));
    }
}

/// <summary>
/// <para type="synopsis">Permanently deletes a trip and every plan in it.</para>
/// </summary>
[Cmdlet(VerbsCommon.Remove, "TripItTrip", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveTripItTripCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public string Uuid { get; set; } = "";

    protected override void ProcessRecord()
    {
        var client = GetClient();
        var detail = RunSync(ct => client.Trips.GetAsync(Uuid, includeObjects: true, ct));
        if (!ShouldProcess($"'{detail.Trip.DisplayName}' ({Uuid}) and its {detail.ObjectCount} plan(s)", "Permanently delete TripIt trip"))
        {
            return;
        }
        RunSync(ct => client.Trips.DeleteAsync(Uuid, ct));
    }
}

/// <summary>
/// <para type="synopsis">Adds a plan (flight, hotel, car, note, …) using TripIt field names.</para>
/// <para type="description">-Fields takes a hashtable, object or JSON string with TripIt's field
/// names, e.g. @{ supplier_name='Hotel Borg'; StartDateTime=@{date='2027-01-18';time='15:00'};
/// EndDateTime=@{date='2027-01-21'} }. Get-TripItPlanSchema lists the fields per type.</para>
/// </summary>
[Cmdlet(VerbsCommon.New, "TripItPlan", SupportsShouldProcess = true)]
[OutputType(typeof(PSObject))]
public sealed class NewTripItPlanCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string Type { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1)] public object? Fields { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)] public string? TripUuid { get; set; }

    protected override void ProcessRecord()
    {
        var type = TripItObjectTypes.Parse(Type);
        var fields = PSJson.ToJsonObject(Fields, nameof(Fields));
        if (!ShouldProcess(TripUuid ?? "(auto-filed)", $"Add {type.ToDisplayName()}")) return;
        var client = GetClient();
        var result = RunSync(ct => client.Objects.CreateAsync(type, fields, TripUuid, ct));
        foreach (var w in result.Warnings.Where(w => !w.Contains("marketing_airline_code", StringComparison.Ordinal))) WriteWarning(w);
        WriteObject(PSJson.ToPSObject(this, type, result.Object));
    }
}

/// <summary>
/// <para type="synopsis">Changes fields of a plan; everything not mentioned is kept.</para>
/// <para type="description">Nested objects merge (@{StartDateTime=@{time='16:00'}} changes only
/// the time); $null removes a field. -SegmentUuid targets one leg of a flight, train, transfer
/// or cruise.</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "TripItPlan", SupportsShouldProcess = true)]
[OutputType(typeof(PSObject))]
public sealed class SetTripItPlanCommand : TripItCmdletBase
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectType")]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string Type { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectUuid")]
    public string Uuid { get; set; } = "";

    [Parameter(Mandatory = true, Position = 2)] public object? Changes { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)] public string? SegmentUuid { get; set; }

    protected override void ProcessRecord()
    {
        var type = TripItObjectTypes.Parse(Type);
        var changes = PSJson.ToJsonObject(Changes, nameof(Changes));
        if (!ShouldProcess($"{type.ToDisplayName()} {Uuid}", "Update TripIt plan")) return;
        var client = GetClient();
        var result = RunSync(ct => client.Objects.UpdateAsync(type, Uuid, changes, SegmentUuid, ct));
        WriteObject(PSJson.ToPSObject(this, type, result.Object));
    }
}

/// <summary>
/// <para type="synopsis">Permanently deletes a plan, or one segment (leg) of it.</para>
/// </summary>
[Cmdlet(VerbsCommon.Remove, "TripItPlan", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High,
    DefaultParameterSetName = PlanSet)]
public sealed class RemoveTripItPlanCommand : TripItCmdletBase
{
    private const string PlanSet = "Plan";
    private const string SegmentSet = "Segment";

    [Parameter(ParameterSetName = PlanSet, Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectType")]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string? Type { get; set; }

    [Parameter(ParameterSetName = PlanSet, Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectUuid")]
    public string? Uuid { get; set; }

    [Parameter(ParameterSetName = SegmentSet, Mandatory = true)]
    public string? SegmentUuid { get; set; }

    protected override void ProcessRecord()
    {
        var client = GetClient();
        if (ParameterSetName == SegmentSet)
        {
            if (ShouldProcess(SegmentUuid!, "Permanently delete TripIt segment"))
            {
                RunSync(ct => client.Objects.DeleteSegmentAsync(SegmentUuid!, ct));
            }
            return;
        }
        var type = TripItObjectTypes.Parse(Type!);
        var current = RunSync(ct => client.Objects.GetAsync(type, Uuid!, ct));
        var title = string.Join("; ", ItineraryBuilder.FromObject(type, current).Select(i => i.Title));
        if (ShouldProcess($"{title} ({Uuid})", "Permanently delete TripIt plan"))
        {
            RunSync(ct => client.Objects.DeleteAsync(type, Uuid!, ct));
        }
    }
}

/// <summary>
/// <para type="synopsis">Moves a plan to another trip, or to Unfiled Items.</para>
/// </summary>
[Cmdlet(VerbsCommon.Move, "TripItPlan", SupportsShouldProcess = true, DefaultParameterSetName = ToTripSet)]
[OutputType(typeof(PSObject))]
public sealed class MoveTripItPlanCommand : TripItCmdletBase
{
    private const string ToTripSet = "ToTrip";
    private const string UnfiledSet = "Unfiled";

    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectType")]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string Type { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [Alias("ObjectUuid")]
    public string Uuid { get; set; } = "";

    [Parameter(ParameterSetName = ToTripSet, Mandatory = true)] public string? DestinationTripUuid { get; set; }
    [Parameter(ParameterSetName = UnfiledSet, Mandatory = true)] public SwitchParameter Unfiled { get; set; }

    protected override void ProcessRecord()
    {
        var type = TripItObjectTypes.Parse(Type);
        var target = Unfiled.IsPresent ? null : DestinationTripUuid;
        if (!ShouldProcess($"{type.ToDisplayName()} {Uuid}", $"Move to {target ?? "Unfiled Items"}")) return;
        var client = GetClient();
        var result = RunSync(ct => client.Objects.MoveAsync(type, Uuid, target, ct));
        WriteObject(PSJson.ToPSObject(this, type, result.Object));
    }
}

/// <summary>
/// <para type="synopsis">Lists the fields TripIt accepts for a plan type.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItPlanSchema")]
[OutputType(typeof(PlanSchema.TypeSchema))]
public sealed class GetTripItPlanSchemaCommand : PSCmdlet
{
    [Parameter(Position = 0)]
    [ArgumentCompleter(typeof(ObjectTypeCompleter))]
    public string? Type { get; set; }

    protected override void ProcessRecord()
    {
        if (Type is null)
        {
            WriteObject(PlanSchema.Types.Values, enumerateCollection: true);
            return;
        }
        WriteObject(PlanSchema.For(TripItObjectTypes.Parse(Type)));
    }
}
