using System.Text.Json;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>Reading the exact price and the UTC offsets out of Google's opaque values (protocol.md section 6).</summary>
public class TokensTests
{
    [Fact]
    public void A_selection_token_carries_the_exact_price_and_the_flight_key()
    {
        // F2's request carried the token of the Delta outbound F1 offered at 275 USD.
        var request = JsonDocument.Parse(HarFixture.Load("direct-http-flow-2026-11-20")[1].InnerRequest).RootElement;
        var token = request.Str(0, 1);

        Assert.Equal(new Money(275.00m, "USD"), Tokens.Price(token));
        Assert.Equal("DL1358", Tokens.FlightKey(token));
    }

    [Fact]
    public void A_calendar_cell_token_carries_the_cells_exact_price()
    {
        var answer = ResponseFrames.Parse(HarFixture.Load("direct-http-flow-2026-11-20")[3].ResponseText).Last!.Value;
        var first = answer.At(1, 0)!.Value;

        Assert.Equal("2026-12-10", first.Str(0));
        Assert.Equal(285, first.Int(2, 0, 1));
        Assert.Equal(new Money(285.00m, "USD"), Tokens.Price(first.Str(2, 1)));
    }

    [Fact]
    public void An_itinerary_detail_carries_each_segments_times_with_their_utc_offsets()
    {
        var answer = ResponseFrames.Parse(HarFixture.Load("direct-http-flow-2026-11-20")[0].ResponseText).LastWith(2)!.Value;
        var itinerary = answer.At(2, 0, 0)!.Value;

        var times = Tokens.SegmentTimes(itinerary.Str(8));

        var segment = Assert.Single(times);
        Assert.Equal("SEA", segment.From);
        Assert.Equal(new DateTimeOffset(2026, 12, 15, 7, 25, 0, TimeSpan.FromHours(-8)), segment.Departs);
        Assert.Equal("DEN", segment.To);
        Assert.Equal(new DateTimeOffset(2026, 12, 15, 11, 10, 0, TimeSpan.FromHours(-7)), segment.Arrives);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    public void An_unreadable_token_has_no_price(string? token) => Assert.Null(Tokens.Price(token));
}
