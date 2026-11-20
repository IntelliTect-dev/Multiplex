using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>Reading Google's framed answers (protocol.md section 3) from recorded responses.</summary>
public class ResponseFramesTests
{
    [Fact]
    public void An_answered_search_yields_its_payloads_and_no_errors()
    {
        var answer = HarFixture.Load("direct-http-flow-2026-11-20")[0];

        var frames = ResponseFrames.Parse(answer.ResponseText);

        Assert.Empty(frames.Errors);
        Assert.NotEmpty(frames.Payloads);
        // Flights arrive in the payloads at index 2 (best) and 3 (other).
        Assert.Contains(frames.Payloads, p => p.GetArrayLength() > 3 && p[2].ValueKind == System.Text.Json.JsonValueKind.Array);
    }

    [Fact]
    public void The_direct_refusal_is_code_13_without_detail()
    {
        var refused = HarFixture.Load("direct-search-refused-2026-11-19")[0];

        var frames = ResponseFrames.Parse(refused.ResponseText);

        Assert.Empty(frames.Payloads);
        var error = Assert.Single(frames.Errors);
        Assert.Equal(13, error.Code);
        Assert.False(error.HasDetail);
    }

    [Fact]
    public void The_page_refusal_is_code_13_with_an_error_response_detail()
    {
        var refused = HarFixture.Load("round-trip-search-refused-2026-11-19")[0];

        var frames = ResponseFrames.Parse(refused.ResponseText);

        Assert.Empty(frames.Payloads);
        var error = Assert.Single(frames.Errors);
        Assert.Equal(13, error.Code);
        Assert.True(error.HasDetail);
    }

    [Fact]
    public void A_body_without_the_guard_is_not_framed()
    {
        // flights.google.com answers the RPC with an HTML 405 page (probe P7).
        var notFramed = HarFixture.Load("direct-http-probes-2026-11-20")[3];

        Assert.False(ResponseFrames.TryParse(notFramed.ResponseText, out _));
    }

    [Fact]
    public void An_autocomplete_answer_is_framed_the_same_way()
    {
        var answer = HarFixture.Load("airport-autocomplete-2026-11-19")[3];

        var frames = ResponseFrames.Parse(answer.ResponseText);

        Assert.Empty(frames.Errors);
        Assert.Single(frames.Payloads);
    }
}
