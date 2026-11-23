using System.ComponentModel;
using System.Management.Automation;
using Docs = Multiplex.Sites.GoogleFlights.GoogleFlightsDocs;

namespace Multiplex.Sites.GoogleFlights.PowerShell;

/// <summary>
/// The commands' shared plumbing: one client for the whole PowerShell session, so the cache, pacing and refusal
/// cool-down carry across commands, and the client's errors turned into PowerShell errors.
/// </summary>
public abstract class GoogleFlightsCmdlet : PSCmdlet
{
    private static readonly Lazy<GoogleFlightsClient> SharedClient = new(() => new GoogleFlightsClient());
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The client the commands use; tests replace it.</summary>
    internal static Func<GoogleFlightsClient> ClientFactory { get; set; } = () => SharedClient.Value;

    protected static GoogleFlightsClient Client => ClientFactory();

    protected CancellationToken Cancellation => _stopping.Token;

    protected override void StopProcessing() => _stopping.Cancel();

    /// <summary>Runs one client call; a failure becomes an error record that says what to do.</summary>
    protected T? Run<T>(Func<CancellationToken, Task<T>> call, object? target) where T : class
    {
        try
        {
            return call(Cancellation).GetAwaiter().GetResult();
        }
        catch (ArgumentException e)
        {
            WriteError(new ErrorRecord(e, "InvalidArgument", ErrorCategory.InvalidArgument, target));
        }
        catch (GoogleFlightsThrottledException e)
        {
            WriteError(new ErrorRecord(e, $"GoogleFlights{e.Reason}", ErrorCategory.ResourceBusy, target)
            {
                ErrorDetails = new ErrorDetails($"{e.Message} Try again after {e.RetryAfter.ToLocalTime():t}."),
            });
        }
        catch (GoogleFlightsException e)
        {
            WriteError(new ErrorRecord(e, e.GetType().Name, ErrorCategory.ReadError, target));
        }
        catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
        {
        }
        return null;
    }

    /// <summary>Writes an object with the result's metadata attached as properties.</summary>
    protected void WriteResult(object value, params (string Name, object? Value)[] notes)
    {
        var pso = PSObject.AsPSObject(value);
        foreach (var (name, note) in notes)
            pso.Properties.Add(new PSNoteProperty(name, note));
        WriteObject(pso);
    }

    protected void WriteFlights(FlightSearchResult result)
    {
        foreach (var (option, best) in result.BestFlights.Select(o => (o, true)).Concat(result.OtherFlights.Select(o => (o, false))))
        {
            WriteResult(option,
                ("Best", best), ("Leg", result.Leg + 1), ("RetrievedAt", result.RetrievedAt),
                ("FromCache", result.FromCache), ("GoogleFlightsUrl", result.GoogleFlightsUrl));
        }
    }

    protected static DateOnly Date(DateTime value) => DateOnly.FromDateTime(value);
}

[Cmdlet(VerbsCommon.Find, "Flight", DefaultParameterSetName = TripSet)]
[OutputType(typeof(FlightOption))]
[Description(Docs.SearchFlights)]
public sealed class FindFlightCommand : GoogleFlightsCmdlet
{
    private const string TripSet = "Trip";
    private const string MultiCitySet = "MultiCity";

    [Parameter(Mandatory = true, Position = 0, ParameterSetName = TripSet, HelpMessage = Docs.Origin)]
    public string Origin { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1, ParameterSetName = TripSet, HelpMessage = Docs.Destination)]
    public string Destination { get; set; } = "";

    [Parameter(Mandatory = true, Position = 2, ParameterSetName = TripSet, HelpMessage = Docs.DepartureDate)]
    public DateTime DepartureDate { get; set; }

    [Parameter(Position = 3, ParameterSetName = TripSet, HelpMessage = Docs.ReturnDate)]
    public DateTime? ReturnDate { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = MultiCitySet, HelpMessage = Docs.Legs)]
    public string[] Leg { get; set; } = [];

    [Parameter(HelpMessage = Docs.MaxStops)]
    public MaxStops MaxStops { get; set; } = MaxStops.Any;

    [Parameter(HelpMessage = Docs.Airlines)]
    public string[] Airline { get; set; } = [];

    [Parameter(HelpMessage = Docs.Alliances)]
    public Alliance[] Alliance { get; set; } = [];

    [Parameter(HelpMessage = Docs.DepartAfterHour)]
    public int? DepartAfterHour { get; set; }

    [Parameter(HelpMessage = Docs.DepartBeforeHour)]
    public int? DepartBeforeHour { get; set; }

    [Parameter(HelpMessage = Docs.ArriveAfterHour)]
    public int? ArriveAfterHour { get; set; }

    [Parameter(HelpMessage = Docs.ArriveBeforeHour)]
    public int? ArriveBeforeHour { get; set; }

    [Parameter(HelpMessage = Docs.MaxDurationHours)]
    public double? MaxDurationHours { get; set; }

    [Parameter(HelpMessage = Docs.Cabin)]
    public CabinClass Cabin { get; set; } = CabinClass.Economy;

    /// <summary>On unless turned off with <c>-ExcludeBasicEconomy:$false</c> (owner's decision, 2026-11-22).</summary>
    [Parameter(HelpMessage = Docs.ExcludeBasicEconomy)]
    public SwitchParameter ExcludeBasicEconomy { get; set; } = true;

    [Parameter(HelpMessage = Docs.Adults)]
    public int Adults { get; set; } = 1;

    [Parameter(HelpMessage = Docs.Children)]
    public int Children { get; set; }

    [Parameter(HelpMessage = Docs.InfantsOnLap)]
    public int InfantsOnLap { get; set; }

    [Parameter(HelpMessage = Docs.InfantsInSeat)]
    public int InfantsInSeat { get; set; }

    [Parameter(HelpMessage = Docs.MaxPrice)]
    public int? MaxPrice { get; set; }

    [Parameter(HelpMessage = Docs.Sort)]
    public FlightSort SortBy { get; set; } = FlightSort.Best;

    [Parameter(HelpMessage = Docs.Currency)]
    public string? Currency { get; set; }

    [Parameter(HelpMessage = Docs.Region)]
    public string? Region { get; set; }

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        var parameters = new SearchParameters
        {
            Origin = ParameterSetName == TripSet ? Origin : null,
            Destination = ParameterSetName == TripSet ? Destination : null,
            DepartureDate = ParameterSetName == TripSet ? Date(DepartureDate) : null,
            ReturnDate = ReturnDate is { } r ? Date(r) : null,
            Legs = ParameterSetName == MultiCitySet ? [.. Leg.Select(SearchParameters.Text.Leg)] : null,
            Stops = MaxStops,
            Airlines = Airline,
            Alliances = Alliance,
            DepartAfterHour = DepartAfterHour,
            DepartBeforeHour = DepartBeforeHour,
            ArriveAfterHour = ArriveAfterHour,
            ArriveBeforeHour = ArriveBeforeHour,
            MaxDurationHours = MaxDurationHours,
            Cabin = Cabin,
            ExcludeBasicEconomy = ExcludeBasicEconomy,
            Adults = Adults,
            Children = Children,
            InfantsOnLap = InfantsOnLap,
            InfantsInSeat = InfantsInSeat,
            MaxPrice = MaxPrice,
            Sort = SortBy,
            Currency = Currency,
            Region = Region,
        };
        if (Run(ct => Client.SearchAsync(parameters.ToRequest(), Verify, ct), parameters) is { } result)
            WriteFlights(result);
    }
}

[Cmdlet(VerbsCommon.Get, "FlightReturn")]
[OutputType(typeof(FlightOption))]
[Description(Docs.GetReturnFlights)]
public sealed class GetFlightReturnCommand : GoogleFlightsCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true, HelpMessage = Docs.Selection)]
    public FlightSelection Selection { get; set; } = null!;

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        if (Run(ct => Client.GetReturnFlightsAsync(Selection, Verify, ct), Selection) is { } result)
            WriteFlights(result);
    }
}

[Cmdlet(VerbsCommon.Get, "FlightBookingOption")]
[OutputType(typeof(BookingOption))]
[Description(Docs.GetBookingOptions)]
public sealed class GetFlightBookingOptionCommand : GoogleFlightsCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true, HelpMessage = Docs.Selection)]
    public FlightSelection Selection { get; set; } = null!;

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        if (Run(ct => Client.GetBookingOptionsAsync(Selection, ct), Selection) is not { } booking)
            return;
        foreach (var option in booking.Options)
            WriteResult(option, ("RetrievedAt", booking.RetrievedAt), ("FromCache", false), ("GoogleFlightsUrl", booking.GoogleFlightsUrl));
    }
}

[Cmdlet(VerbsCommon.Get, "FlightPriceCalendar", DefaultParameterSetName = TripLengthSet)]
[OutputType(typeof(CalendarPrice))]
[Description(Docs.GetPriceCalendar)]
public sealed class GetFlightPriceCalendarCommand : GoogleFlightsCmdlet
{
    private const string TripLengthSet = "TripLength";
    private const string ReturnRangeSet = "ReturnRange";

    [Parameter(Mandatory = true, Position = 0, HelpMessage = Docs.Origin)]
    public string Origin { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1, HelpMessage = Docs.Destination)]
    public string Destination { get; set; } = "";

    [Parameter(Mandatory = true, Position = 2, HelpMessage = Docs.DepartureDateFrom)]
    public DateTime DepartureFrom { get; set; }

    [Parameter(Mandatory = true, Position = 3, HelpMessage = Docs.DepartureDateTo)]
    public DateTime DepartureTo { get; set; }

    [Parameter(ParameterSetName = TripLengthSet, HelpMessage = Docs.TripLengthDays)]
    public int? TripLengthDays { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = ReturnRangeSet, HelpMessage = Docs.ReturnDateFrom)]
    public DateTime ReturnFrom { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = ReturnRangeSet, HelpMessage = Docs.ReturnDateTo)]
    public DateTime ReturnTo { get; set; }

    [Parameter(HelpMessage = Docs.MaxStops)]
    public MaxStops MaxStops { get; set; } = MaxStops.Any;

    [Parameter(HelpMessage = Docs.Airlines)]
    public string[] Airline { get; set; } = [];

    [Parameter(HelpMessage = Docs.Cabin)]
    public CabinClass Cabin { get; set; } = CabinClass.Economy;

    [Parameter(HelpMessage = Docs.Adults)]
    public int Adults { get; set; } = 1;

    [Parameter(HelpMessage = Docs.Currency)]
    public string? Currency { get; set; }

    [Parameter(HelpMessage = Docs.Region)]
    public string? Region { get; set; }

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        var parameters = new SearchParameters
        {
            Origin = Origin,
            Destination = Destination,
            Stops = MaxStops,
            Airlines = Airline,
            Cabin = Cabin,
            Adults = Adults,
            Currency = Currency,
            Region = Region,
        };
        var range = ParameterSetName == ReturnRangeSet;
        var calendar = Run(ct => Client.GetCalendarAsync(
            parameters.ToCalendarRequest(Date(DepartureFrom), Date(DepartureTo), TripLengthDays,
                range ? Date(ReturnFrom) : null, range ? Date(ReturnTo) : null),
            Verify, ct), parameters);
        if (calendar is null)
            return;
        foreach (var price in calendar.Prices)
            WriteResult(price, ("RetrievedAt", calendar.RetrievedAt), ("FromCache", calendar.FromCache));
    }
}

[Cmdlet(VerbsCommon.Find, "Airport")]
[OutputType(typeof(Airport))]
[Description(Docs.LookupAirport)]
public sealed class FindAirportCommand : GoogleFlightsCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, HelpMessage = Docs.AirportQuery)]
    public string Name { get; set; } = "";

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        if (Run(ct => Client.LookupAirportsAsync(Name, Verify, ct), Name) is not { } lookup)
            return;
        foreach (var place in lookup.Places)
        {
            foreach (var airport in place.Airports)
                WriteResult(airport, ("Place", place.Name), ("RetrievedAt", lookup.RetrievedAt), ("FromCache", lookup.FromCache));
        }
    }
}

[Cmdlet(VerbsCommon.Get, "Airline")]
[OutputType(typeof(AirlineInfo))]
[Description(Docs.LookupAirline)]
public sealed class GetAirlineCommand : PSCmdlet
{
    [Parameter(Position = 0, ValueFromPipeline = true, HelpMessage = Docs.AirlineQuery)]
    public string? Name { get; set; }

    [Parameter(HelpMessage = Docs.Verify)]
    public SwitchParameter Verify { get; set; }

    protected override void ProcessRecord()
    {
        foreach (var airline in GoogleFlightsClient.Airlines.Lookup(Name))
            WriteObject(airline);
    }
}
