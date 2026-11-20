using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>The flat parameters the MCP tools and PowerShell commands share.</summary>
public class SearchParametersTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);

    [Fact]
    public void Flat_parameters_build_the_same_request_the_page_sent_for_its_most_filtered_state()
    {
        var recorded = HarFixture.Load("sort-and-multi-city-held-2026-11-19")[0].InnerRequest;
        var parameters = new SearchParameters
        {
            Origin = "SEA", Destination = "DEN", DepartureDate = Dec15,
            Stops = SearchParameters.Text.Stops("nonstop"),
            Airlines = ["DL", "AS"],
            DepartAfterHour = 6,
            ArriveBeforeHour = 23,
            MaxDurationHours = 6,
            MaxPrice = 400,
            ExcludeBasicEconomy = true,
            Sort = SearchParameters.Text.Sort("price"),
        };

        Assert.Equal(recorded, RequestEncoder.ShoppingResults(parameters.ToRequest().Normalize("USD", "US")));
    }

    [Fact]
    public void A_return_date_makes_a_round_trip_and_legs_make_a_multi_city_trip()
    {
        var roundTrip = new SearchParameters { Origin = "SEA", Destination = "DEN", DepartureDate = Dec15, ReturnDate = Dec15.AddDays(7) }.ToRequest();
        var multi = new SearchParameters { Legs = [SearchParameters.Text.Leg("SEA-DEN-2026-12-15"), SearchParameters.Text.Leg("den-lax-2026-12-19")] }.ToRequest();

        Assert.Equal(TripType.RoundTrip, roundTrip.TripType);
        Assert.Equal(("DEN", "SEA"), (roundTrip.Legs[1].Origin, roundTrip.Legs[1].Destination));
        Assert.Equal(TripType.MultiCity, multi.TripType);
        Assert.Equal(new DateOnly(2026, 12, 19), multi.Normalize("USD", "US").Legs[1].Date);
    }

    [Fact]
    public void Filters_apply_to_every_leg()
    {
        var multi = new SearchParameters
        {
            Legs = [new FlightLeg("SEA", "DEN", Dec15), new FlightLeg("DEN", "LAX", Dec15.AddDays(4))],
            Stops = MaxStops.Nonstop,
            Alliances = [Alliance.SkyTeam],
        }.ToRequest();

        Assert.All(multi.Legs, l => Assert.Equal(MaxStops.Nonstop, l.Filters.Stops));
        Assert.All(multi.Legs, l => Assert.Equal([Alliance.SkyTeam], l.Filters.Alliances));
    }

    [Theory]
    [InlineData("nonstop", MaxStops.Nonstop)]
    [InlineData("One", MaxStops.OneOrFewer)]
    [InlineData("2", MaxStops.TwoOrFewer)]
    [InlineData(null, MaxStops.Any)]
    public void Stops_read_the_words_people_use(string? text, MaxStops expected) =>
        Assert.Equal(expected, SearchParameters.Text.Stops(text));

    [Theory]
    [InlineData("star alliance", Alliance.StarAlliance)]
    [InlineData("STAR_ALLIANCE", Alliance.StarAlliance)]
    [InlineData("oneworld", Alliance.Oneworld)]
    public void Alliances_read_any_spelling(string text, Alliance expected) =>
        Assert.Equal(expected, SearchParameters.Text.Alliance(text));

    [Fact]
    public void A_missing_or_contradictory_input_names_itself()
    {
        Assert.Contains("origin", Assert.Throws<ArgumentException>(() => new SearchParameters { Destination = "DEN", DepartureDate = Dec15 }.ToRequest()).Message);
        Assert.Contains("departure date", Assert.Throws<ArgumentException>(() => new SearchParameters { Origin = "SEA", Destination = "DEN" }.ToRequest()).Message);
        Assert.Contains("not both", Assert.Throws<ArgumentException>(() =>
            new SearchParameters { Origin = "SEA", Legs = [new FlightLeg("SEA", "DEN", Dec15)] }.ToRequest()).Message);
        Assert.Contains("yyyy-MM-dd", Assert.Throws<ArgumentException>(() => SearchParameters.Text.Date("12/15/2026", "departure_date")).Message);
        Assert.Contains("one of", Assert.Throws<ArgumentException>(() => SearchParameters.Text.Cabin("steerage")).Message);
        Assert.Throws<ArgumentException>(() => new SearchParameters { Origin = "SEA", Destination = "DEN", DepartureDate = Dec15, DepartAfterHour = 20, DepartBeforeHour = 6 }.ToRequest());
    }

    [Fact]
    public void A_calendar_with_a_trip_length_is_a_round_trip_of_that_length()
    {
        var calendar = new SearchParameters { Origin = "SEA", Destination = "DEN" }
            .ToCalendarRequest(Dec15, Dec15.AddDays(10), tripLengthDays: 7, returnFrom: null, returnTo: null);

        Assert.Equal(TripType.RoundTrip, calendar.Search.TripType);
        Assert.Equal(Dec15.AddDays(7), calendar.Search.Legs[1].Date);
        Assert.Null(calendar.ReturnFrom);
    }

    [Fact]
    public void A_calendar_without_a_trip_length_or_returns_is_one_way()
    {
        var calendar = new SearchParameters { Origin = "SEA", Destination = "DEN" }
            .ToCalendarRequest(Dec15, Dec15.AddDays(10), tripLengthDays: null, returnFrom: null, returnTo: null);

        Assert.Equal(TripType.OneWay, calendar.Search.TripType);
    }
}
