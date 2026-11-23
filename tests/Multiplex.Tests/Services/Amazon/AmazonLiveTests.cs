using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;
using Xunit.Abstractions;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>
/// The real amazon.com, through the real client: honest user agent, robots.txt, the gate and its
/// real waits. Opt in with MULTIPLEX_LIVE_TESTS=1. One run sends at most eight requests (two
/// robots.txt files, a two-page search, a filtered search, a product and its picture, a book),
/// so it takes about half a minute. It checks invariants, never values: these are the tests
/// that notice when Amazon changes its markup. On a failure the pages it fetched are saved to
/// the gitignored <c>Recorded/</c> folder, where <see cref="AmazonRecordedPagesTests"/> reads them.
/// </summary>
public class AmazonLiveTests(ITestOutputHelper output)
{
    [LiveHttpFact]
    public async Task SearchFiltersAndProducts_StillReadTheRealAmazon()
    {
        var recorder = new PageRecorder();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddHttpClient(AmazonClient.HttpClientName).AddHttpMessageHandler(() => recorder)));
        var amazon = factory.Services.GetRequiredService<AmazonClient>();
        try
        {
            var search = await amazon.SearchAsync(new AmazonSearchRequest("usb c cable", Pages: 2), CancellationToken.None);
            output.WriteLine($"search: pages {string.Join(",", search.PagesRead)}, {search.Results.Count} results, {search.SponsoredRemoved} ads dropped, total '{search.TotalResults}'");
            Assert.Equal([1, 2], search.PagesRead);
            Assert.Empty(search.Warnings);
            Assert.True(search.Results.Count >= 10, $"only {search.Results.Count} results");
            Assert.True(search.Results.Count(r => r.Price is not null && r.Rating is not null && r.RatingsCount is not null) >= search.Results.Count * 0.8,
                "under 80% of results have a price, a rating and an exact count");
            Assert.Equal(search.Results.Count, search.Results.Select(r => r.Asin).Distinct().Count());
            var brands = Assert.Single(search.AvailableFilters, g => g.Name == "Brands");
            Assert.All(brands.Options, o => Assert.Matches(@"^p_123:\d+$", o.Code));

            var filtered = await amazon.SearchAsync(new AmazonSearchRequest("usb c cable", AmazonSort.Price, MaxPrice: 10), CancellationToken.None);
            output.WriteLine($"price sort under $10: {filtered.Results.Count} results, {filtered.FilteredOut} filtered out, from {filtered.Results.FirstOrDefault()?.Price} to {filtered.Results.LastOrDefault()?.Price}");
            Assert.Equal(new AmazonAppliedMinimums(4, 20), filtered.AppliedMinimums);
            Assert.NotEmpty(filtered.Results);
            Assert.All(filtered.Results, r =>
            {
                Assert.True(r.Price <= 10);
                Assert.True(r.Rating >= 4);
                Assert.True(r.RatingsCount >= 20);
            });
            Assert.Equal(filtered.Results.Select(r => r.Price).Order(), filtered.Results.Select(r => r.Price));

            // A long-lived, much-reviewed product, so Amazon shows its "Customers say" summary.
            var (product, image) = await amazon.GetProductAsync("B088NRLMPV", CancellationToken.None);
            output.WriteLine($"product: {product.Title} | {product.Price} | {product.Rating} from {product.RatingsCount} | {product.CustomersSay?.Aspects.Count} aspects | image {image?.Data.Length} bytes");
            Assert.Equal("B088NRLMPV", product.Asin);
            Assert.NotNull(product.Price);
            Assert.InRange(product.Rating!.Value, 3, 5);
            Assert.True(product.RatingsCount > 1000);
            Assert.InRange(product.RatingHistogram!.Values.Sum(), 97, 103);
            Assert.True(product.CustomersSay!.Aspects.Count >= 3);
            Assert.All(product.CustomersSay.Aspects, a => Assert.True(a.Mentions > 0));
            Assert.NotEmpty(product.Features);
            Assert.NotEmpty(product.Images);
            Assert.NotNull(image);
            Assert.StartsWith("image/", image.MimeType);
            Assert.Empty(product.Warnings);

            var (book, _) = await amazon.GetProductAsync("0135972264", CancellationToken.None);
            output.WriteLine($"book: {book.Title} | {book.Price} | {book.Rating}");
            Assert.False(string.IsNullOrWhiteSpace(book.Title));
            Assert.NotNull(book.Rating);
            Assert.NotNull(book.Description);
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException or SiteException)
        {
            output.WriteLine($"Saved {recorder.Save()} fetched pages to Recorded/ for a look.");
            throw;
        }
    }

    /// <summary>Keeps the pages a live run fetched, so a failing run can leave them behind.</summary>
    private sealed class PageRecorder : DelegatingHandler
    {
        private readonly List<(Uri Url, string Body)> _pages = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                await response.Content.LoadIntoBufferAsync(ct);
                lock (_pages) _pages.Add((request.RequestUri!, response.Content.ReadAsStringAsync(ct).Result));
            }
            return response;
        }

        public int Save([CallerFilePath] string thisFile = "")
        {
            var folder = Path.Combine(Path.GetDirectoryName(thisFile)!, "Recorded");
            Directory.CreateDirectory(folder);
            lock (_pages)
            {
                for (var i = 0; i < _pages.Count; i++)
                {
                    var kind = _pages[i].Url.AbsolutePath == "/s" ? "search" : "product";
                    File.WriteAllText(Path.Combine(folder, $"{kind}-live-{i + 1}.html"), _pages[i].Body, Encoding.UTF8);
                }
                return _pages.Count;
            }
        }
    }
}
