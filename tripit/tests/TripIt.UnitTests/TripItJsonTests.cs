using System.Text.Json;
using TripIt.Json;
using TripIt.Models;
using Xunit;

namespace TripIt.UnitTests;

public sealed class TripItJsonTests
{
    [Fact]
    public void AsList_reads_single_object_as_one_item_list()
    {
        using var doc = JsonDocument.Parse("""{"Trip":{"id":"1"}}""");
        Assert.Single(TripItJson.AsList(doc.RootElement, "Trip"));
    }

    [Fact]
    public void AsList_reads_array_and_absent_property()
    {
        using var doc = JsonDocument.Parse("""{"Trip":[{"id":"1"},{"id":"2"}]}""");
        Assert.Equal(2, TripItJson.AsList(doc.RootElement, "Trip").Count);
        Assert.Empty(TripItJson.AsList(doc.RootElement, "AirObject"));
    }

    [Fact]
    public void Options_deserialize_string_encoded_numbers_and_booleans()
    {
        // #7: every scalar is a JSON string; plain System.Text.Json throws on int?/bool?.
        var list = JsonSerializer.Deserialize<ListResponse<Trip>>(Fixtures.ListTripSingle, TripItJson.Options)!;
        Assert.Equal(1, list.PageNum);
        Assert.Equal(1, list.TotalItems);
        var trip = Assert.Single(list.ItemsOf("Trip"));
        Assert.False(trip.IsPrivate);
        Assert.True(trip.IsProEnabled);
    }

    [Fact]
    public void ListResponse_Items_skips_Profile_and_handles_single_object()
    {
        var list = JsonSerializer.Deserialize<ListResponse<Trip>>(Fixtures.ListTripSingle, TripItJson.Options)!;
        var trip = Assert.Single(list.Items);
        Assert.Equal("Test Trip to Lisbon", trip.DisplayName);
    }

    [Fact]
    public void Prune_removes_empty_values_and_noise_keys()
    {
        using var doc = JsonDocument.Parse("""{"a":"x","b":"","c":{},"d":[],"relative_url":"/y","e":{"f":" "},"g":[{"h":""},{"i":"1"}]}""");
        var pruned = TripItJson.Prune(doc.RootElement)!.ToJsonString();
        Assert.Equal("""{"a":"x","g":[{"i":"1"}]}""", pruned);
    }

    [Fact]
    public void Str_returns_null_for_blank_and_missing()
    {
        using var doc = JsonDocument.Parse("""{"a":"","b":"v","n":5}""");
        Assert.Null(TripItJson.Str(doc.RootElement, "a"));
        Assert.Null(TripItJson.Str(doc.RootElement, "missing"));
        Assert.Equal("v", TripItJson.Str(doc.RootElement, "b"));
        Assert.Equal("5", TripItJson.Str(doc.RootElement, "n"));
    }

    [Theory]
    [InlineData("air", TripItObjectType.Air)]
    [InlineData("AirObject", TripItObjectType.Air)]
    [InlineData("flight", TripItObjectType.Air)]
    [InlineData("Lodging", TripItObjectType.Lodging)]
    [InlineData("directions", TripItObjectType.Directions)]
    public void ObjectType_parses_path_key_name_and_display(string input, TripItObjectType expected)
        => Assert.Equal(expected, TripItObjectTypes.Parse(input));

    [Fact]
    public void ObjectType_parse_rejects_unknown_with_valid_list()
    {
        var ex = Assert.Throws<ArgumentException>(() => TripItObjectTypes.Parse("spaceship"));
        Assert.Contains("lodging", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectType_round_trips_every_type()
    {
        foreach (var t in TripItObjectTypes.All)
        {
            Assert.Equal(t, TripItObjectTypes.Parse(t.ToPathSegment()));
            Assert.Equal(t, TripItObjectTypes.FromEnvelopeKey(t.ToEnvelopeKey()));
        }
    }
}
