using System.Runtime.CompilerServices;
using System.Text.Json;
using Multiplex.Web.Services.Amazon;
using Xunit.Abstractions;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>
/// Runs only where a developer keeps whole recorded Amazon pages in the gitignored
/// <c>Recorded/</c> folder beside this file (<c>search-*.html</c>, <c>product-*.html</c>). Each page
/// must parse with nothing reported as a changed layout, and the same invariants the live tests
/// check must hold. It also writes a trimmed copy of each page to <c>Recorded/trimmed/</c>: that
/// is how the committed fixtures in <c>Fixtures/</c> are refreshed.
/// </summary>
public class AmazonRecordedPagesTests(ITestOutputHelper output)
{
    private static string RecordedFolder([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Recorded");

    public static IEnumerable<object[]> Pages(string prefix)
    {
        var folder = RecordedFolder();
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, prefix + "-*.html") : [];
        // A theory needs a row; with nothing recorded, the one row is a no-op.
        return files.Length == 0 ? [[""]] : files.Select(f => new object[] { Path.GetFileName(f) });
    }

    [Theory]
    [MemberData(nameof(Pages), "search")]
    public void ARecordedSearchPage_Parses_AndTrimsToTheSameResults(string file)
    {
        if (file.Length == 0) return;
        var html = File.ReadAllText(Path.Combine(RecordedFolder(), file));

        var page = AmazonSearchPage.Parse(html);
        output.WriteLine($"{file}: {page.Items.Count} results, {page.Items.Count(i => i.Sponsored)} sponsored, total '{page.TotalResults}', {page.Filters.Count} filter groups");
        // A search with no matches still comes back with a few ads, and nothing else.
        if (!file.Contains("no-results"))
        {
            var unpaid = page.Items.Where(i => !i.Sponsored).ToList();
            Assert.True(unpaid.Count >= 10, $"only {unpaid.Count} unpaid results");
            Assert.True(unpaid.Count(i => i.Price is not null && i.Rating is not null && i.RatingsCount is not null) >= unpaid.Count * 0.8,
                "under 80% of results have a price, a rating and an exact count");
        }
        Assert.Empty(page.Warnings);

        var trimmed = FixtureTrimmer.TrimSearch(html);
        Assert.Equal(JsonSerializer.Serialize(page), JsonSerializer.Serialize(AmazonSearchPage.Parse(trimmed)));
        WriteTrimmed(file, trimmed);
    }

    [Theory]
    [MemberData(nameof(Pages), "product")]
    public void ARecordedProductPage_Parses_AndTrimsToTheSameFacts(string file)
    {
        if (file.Length == 0) return;
        var html = File.ReadAllText(Path.Combine(RecordedFolder(), file));
        var at = DateTimeOffset.UnixEpoch;

        var product = AmazonProductPage.Parse(html, at);
        output.WriteLine(JsonSerializer.Serialize(product, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Matches("^[A-Z0-9]{10}$", product.Asin);
        if (product.RatingHistogram is { } histogram) Assert.InRange(histogram.Values.Sum(), 97, 103);
        Assert.Empty(product.Warnings);

        var trimmed = FixtureTrimmer.TrimProduct(html);
        var again = AmazonProductPage.Parse(trimmed, at);
        // Prose is replaced and the gallery cut to two pictures; every other fact must survive the trim.
        Assert.Equal(
            JsonSerializer.Serialize(Facts(product)),
            JsonSerializer.Serialize(Facts(again)));
        Assert.Equal(product.Features.Count, again.Features.Count);
        Assert.Equal(product.CustomersSay?.Aspects.Select(a => (a.Name, a.Mentions, a.Positive, a.Negative, a.Quotes.Count)),
            again.CustomersSay?.Aspects.Select(a => (a.Name, a.Mentions, a.Positive, a.Negative, a.Quotes.Count)));
        Assert.Equal(product.MainImageUrl, again.MainImageUrl);
        WriteTrimmed(file, trimmed);
    }

    private static AmazonProduct Facts(AmazonProduct product) => product with
    {
        CustomersSay = null,
        Features = [],
        Description = null,
        DescriptionTruncated = false,
        Images = [],
        // A fixture cuts any text over 200 characters; long values need only start the same.
        Overview = product.Overview.ToDictionary(p => p.Key, p => Start(p.Value)),
        Details = product.Details.ToDictionary(p => p.Key, p => Start(p.Value)),
    };

    private static string Start(string value) => value.Length > 100 ? value[..100] : value;

    private void WriteTrimmed(string file, string trimmed)
    {
        var folder = Path.Combine(RecordedFolder(), "trimmed");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), trimmed);
        output.WriteLine($"trimmed: {trimmed.Length / 1024} KB");
    }
}
