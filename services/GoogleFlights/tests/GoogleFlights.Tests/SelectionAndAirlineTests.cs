using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

public class SelectionHandleTests
{
    private static readonly DateOnly Dec15 = new(2026, 12, 15);

    [Fact]
    public void A_handle_brings_back_the_same_search_choices_and_token()
    {
        var request = FlightSearchRequest.RoundTrip("SEA", "DEN", Dec15, new DateOnly(2026, 12, 22),
                new LegFilters { Stops = MaxStops.Nonstop, Airlines = ["DL"], Alliances = [Alliance.SkyTeam], DepartureHours = new HourRange(6, 20) })
            .Normalize("USD", "US") with { Cabin = CabinClass.Business, Passengers = new Passengers(2, 1), MaxPrice = 900 };
        var selection = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1358")]], "CjRI+/abc=");

        var back = FlightSelection.FromHandle(selection.ToHandle());

        // The same bytes go to Google either way: that is what "the same selection" means to the client.
        Assert.Equal(RequestEncoder.ShoppingResults(request, selection), RequestEncoder.ShoppingResults(back.Request, back));
        Assert.Equal(selection.Token, back.Token);
        Assert.Equal(selection.ChosenLegs[0], back.ChosenLegs[0]);
        Assert.False(back.IsComplete);
    }

    [Fact]
    public void A_handle_is_url_safe()
    {
        var request = FlightSearchRequest.OneWay("SEA", "DEN", Dec15).Normalize("USD", "US");
        var handle = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1")]], "a+b/c=").ToHandle();

        Assert.Matches("^[A-Za-z0-9_-]+$", handle);
    }

    [Fact]
    public void A_handle_that_expands_beyond_64_KB_is_refused_without_reading_it_all()
    {
        var request = FlightSearchRequest.OneWay("SEA", "DEN", Dec15).Normalize("USD", "US");
        var huge = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1")]], new string('A', 200_000)).ToHandle();

        Assert.True(huge.Length < 2_000, "Brotli makes it small on the wire; only expanding it shows its size.");
        Assert.Throws<ArgumentException>(() => FlightSelection.FromHandle(huge));
    }

    [Fact]
    public void A_handle_holding_a_flight_not_in_Googles_form_is_refused()
    {
        var request = FlightSearchRequest.OneWay("SEA", "DEN", Dec15).Normalize("USD", "US");
        var forged = new FlightSelection(request, [[new ChosenSegment("SEA", Dec15, "DEN", "DL", "1; DROP")]], "token").ToHandle();

        Assert.Contains("not in Google's form", Assert.Throws<ArgumentException>(() => FlightSelection.FromHandle(forged)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-handle")]
    [InlineData("eyJ4IjoxfQ")] // {"x":1}: valid JSON, not a selection
    public void Anything_else_is_refused_with_a_clear_message(string handle)
    {
        var e = Assert.Throws<ArgumentException>(() => FlightSelection.FromHandle(handle));

        Assert.Contains("Search again", e.Message, StringComparison.Ordinal);
    }
}

public class AirlineDirectoryTests
{
    private static AirlineDirectory Directory => GoogleFlightsClient.Airlines;

    [Fact]
    public void The_data_file_is_versioned()
    {
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", Directory.Version);
    }

    [Theory]
    [InlineData("DL", "Delta")]
    [InlineData("dl", "Delta")]
    [InlineData(" AS ", "Alaska")]
    public void A_code_finds_its_airline(string code, string name)
    {
        Assert.Equal(name, Assert.Single(Directory.Lookup(code)).Name);
    }

    [Theory]
    [InlineData("SkyTeam", Alliance.SkyTeam)]
    [InlineData("star alliance", Alliance.StarAlliance)]
    [InlineData("STAR_ALLIANCE", Alliance.StarAlliance)]
    [InlineData("oneworld", Alliance.Oneworld)]
    public void An_alliance_lists_its_members(string query, Alliance alliance)
    {
        var members = Directory.Lookup(query);

        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.Equal(alliance, m.Alliance));
    }

    [Fact]
    public void Part_of_a_name_finds_every_airline_with_it()
    {
        Assert.Contains(Directory.Lookup("air"), a => a.Code == "AC");
        Assert.Contains(Directory.Lookup("air"), a => a.Code == "AF");
    }

    [Fact]
    public void Every_airline_Google_named_in_the_references_is_in_the_file_with_Googles_name()
    {
        // Seen in the recorded answers' airline filters (protocol.md section 6, index 7.1).
        (string Code, string Name)[] seen =
        [
            ("AA", "American"), ("AS", "Alaska"), ("AT", "Royal Air Maroc"), ("B6", "JetBlue"), ("DE", "Condor"),
            ("DL", "Delta"), ("EK", "Emirates"), ("F9", "Frontier"), ("FJ", "Fiji Airways"), ("GF", "Gulf Air"),
            ("JX", "STARLUX Airlines"), ("MU", "China Eastern"), ("PR", "Philippine Airlines"), ("SV", "Saudia"),
            ("UA", "United"), ("UX", "Air Europa"), ("WN", "Southwest"), ("WS", "WestJet"), ("Y4", "Volaris"),
        ];

        Assert.All(seen, s => Assert.Equal(s.Name, Directory.Find(s.Code)?.Name));
    }

    [Fact]
    public void Nothing_matches_an_unknown_name()
    {
        Assert.Empty(Directory.Lookup("Zeppelin Air Lines"));
    }
}
