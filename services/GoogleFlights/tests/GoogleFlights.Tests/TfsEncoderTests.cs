using System.Text.Json;
using Multiplex.Sites.GoogleFlights.Wire;

namespace Multiplex.Sites.GoogleFlights.Tests;

/// <summary>The <c>tfs</c> link encoder against every <c>tfs</c> the page itself produced (protocol.md section 7).</summary>
public class TfsEncoderTests
{
    public static TheoryData<string, int> PageRequests()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in HarFixture.AllNames())
        {
            var har = HarFixture.Load(name);
            for (var i = 0; i < har.Count; i++)
            {
                var e = har[i];
                if (e.RefererTfs is not null && e.Url.Contains("FlightsFrontendService", StringComparison.Ordinal))
                    data.Add(name, i);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PageRequests))]
    public void The_filter_encodes_to_the_tfs_of_the_page_that_sent_it(string reference, int entry)
    {
        var e = HarFixture.Load(reference)[entry];
        var filter = JsonDocument.Parse(e.InnerRequest).RootElement[1];
        var expected = e.RefererTfs!;
        ProtoMessage.TryParseBase64(expected, out var page);

        Assert.Equal(expected, TfsEncoder.Encode(filter, (int)(page.Varint(2) ?? 2)));
    }

    [Fact]
    public void Sort_by_price_is_the_tfu_the_page_used()
    {
        // Capture I: the page's URL after choosing "Price" carried tfu=EgYIAhAAGAA.
        Assert.Equal("EgYIAhAAGAA", TfsEncoder.SortParameter(FlightSort.Price));
        Assert.Null(TfsEncoder.SortParameter(FlightSort.Best));
    }
}
