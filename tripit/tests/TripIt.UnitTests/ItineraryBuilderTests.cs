using System.Text.Json;
using TripIt.Models;
using Xunit;

namespace TripIt.UnitTests;

public sealed class ItineraryBuilderTests
{
    private static TripDetail Detail()
    {
        using var doc = JsonDocument.Parse(Fixtures.TripWithObjects);
        return TripsClient.ParseTripDetail(doc.RootElement.Clone());
    }

    [Fact]
    public void Build_emits_one_line_per_flight_segment_in_chronological_order()
    {
        var items = Detail().Itinerary;

        Assert.Equal(
            ["Flight EX 1 JFK → LIS", "Stay: Hotel Example", "Flight EX 2 LIS → JFK"],
            items.Select(i => i.Title));
    }

    [Fact]
    public void Flight_line_carries_times_with_offsets_route_and_confirmation()
    {
        var leg = Detail().Itinerary.First();

        Assert.Equal("air", leg.Type);
        Assert.Equal(Fixtures.AirUuid, leg.ObjectUuid);
        Assert.Equal("seg-1", leg.SegmentUuid);
        Assert.Equal("2027-03-01T18:00:00-05:00", leg.Start);
        Assert.Equal("America/New_York", leg.StartTimezone);
        Assert.Equal("2027-03-02T06:30:00+00:00", leg.End);
        Assert.Equal("JFK (New York)", leg.From);
        Assert.Equal("LIS (Lisbon)", leg.To);
        Assert.Equal("ABC123", leg.Confirmation);
        Assert.Equal("Example Air", leg.Supplier);
        Assert.Equal("Test User", leg.Details!["travelers"]);
        Assert.False(leg.Details.ContainsKey("seats")); // empty string omitted
    }

    [Fact]
    public void Flight_status_code_is_translated()
    {
        var returnLeg = Detail().Itinerary.Last();
        Assert.Equal("delayed", returnLeg.Details!["flight_status"]);
        Assert.Equal("12A", returnLeg.Details["seats"]);
    }

    [Fact]
    public void Lodging_line_has_address_and_room()
    {
        var stay = Detail().Itinerary.Single(i => i.Type == "lodging");
        Assert.Equal("1 Example Street, Lisbon", stay.Location);
        Assert.Equal("H-9", stay.Confirmation);
        Assert.Equal("Double", stay.Details!["room_type"]);
        Assert.Equal("2027-03-05T11:00:00+00:00", stay.End);
    }

    [Fact]
    public void Weather_is_excluded_unless_requested()
    {
        var detail = Detail();
        Assert.DoesNotContain(detail.Itinerary, i => i.Type == "weather");
        Assert.Contains(ItineraryBuilder.Build(detail.Objects, includeWeather: true), i => i.Type == "weather");
    }

    [Fact]
    public void Undated_items_sort_last()
    {
        using var doc = JsonDocument.Parse("""{"NoteObject":{"uuid":"n","display_name":"Undated"},"ParkingObject":{"uuid":"p","display_name":"Lot","StartDateTime":{"date":"2027-01-01"}}}""");
        var items = ItineraryBuilder.Build(ItineraryBuilder.ExtractObjects(doc.RootElement));
        Assert.Equal(["parking", "note"], items.Select(i => i.Type));
    }

    [Theory]
    [InlineData("300", "scheduled")]
    [InlineData("400", "cancelled")]
    [InlineData("999", "999")]
    public void FlightStatus_maps_known_codes(string code, string expected)
        => Assert.Equal(expected, ItineraryBuilder.FlightStatus(code));
}
