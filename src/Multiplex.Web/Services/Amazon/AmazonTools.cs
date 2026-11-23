using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Multiplex.Web.HtmlSites;
using Multiplex.Web.Mcp;

namespace Multiplex.Web.Services.Amazon;

[McpServerToolType]
[McpEndpoint("amazon")]
public static class AmazonTools
{
    [McpServerTool(Name = "amazon_search"),
     Description("Searches amazon.com (US, signed out) and returns products with price, stars, number of ratings and more. " +
                 "Sponsored results are dropped and nothing rated below 3 stars is returned. " +
                 "sort: 'relevance' is Amazon's own order; 'price' is cheapest first among products with 4+ stars and 20+ ratings " +
                 "(pair it with minPrice or filters when the person wants better than the very cheapest); " +
                 "'rating' is best first by weightedRating (stars weighted by how many people rated), among products with 20+ ratings. " +
                 "pages reads up to 10 result pages in one call (about 4 seconds each); nextPage says where to continue. " +
                 "availableFilters lists Amazon's own filters for this search (brand, size and so on): pass a code back in filters " +
                 "on the next call, one code per group. Every sortable value is a plain number. " +
                 "If the answer has handOver, or the tool fails saying a person must prove they're human, give the person that link " +
                 "and call again only after they say it is done: a refused call is not retried by itself.")]
    public static async Task<CallToolResult> Search(
        AmazonClient amazon,
        [Description("What to search for, as typed into Amazon's search box.")] string query,
        [Description("relevance (default), price or rating.")] AmazonSort sort = AmazonSort.Relevance,
        [Description("How many result pages to read, 1 to 10. Default 1.")] int pages = 1,
        [Description("The first page to read, 1 to 20. Default 1.")] int startPage = 1,
        [Description("Lowest price in US dollars.")] decimal? minPrice = null,
        [Description("Highest price in US dollars.")] decimal? maxPrice = null,
        [Description("Lowest star rating, 3 to 5. Default 3 (4 for sort 'price').")] double? minRating = null,
        [Description("Fewest ratings a product may have. Default 20 for sort 'price' and 'rating'.")] int? minRatingsCount = null,
        [Description("Filter codes from availableFilters in an earlier answer, e.g. [\"p_123:3271\"]. At most 10, one per group.")] string[]? filters = null,
        CancellationToken ct = default)
    {
        try
        {
            var answer = await amazon.SearchAsync(
                new AmazonSearchRequest(query, sort, pages, startPage, minPrice, maxPrice, minRating, minRatingsCount, filters), ct);
            return Text(answer);
        }
        catch (SiteException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "amazon_get_product"),
     Description("Reads one amazon.com product page (US, signed out): price, list price and deal, availability and seller, " +
                 "stars, number of ratings and the star breakdown, Amazon's AI 'Customers say' summary with each aspect's positive " +
                 "and negative counts and short review quotes, features, description, details, Best Sellers Rank, variations and " +
                 "image links. The main image is also returned as an image. Individual reviews are not included: Amazon shows " +
                 "them only to signed-in visitors. fetchedAt says when the page was read; prices change often. " +
                 "If the tool fails saying a person must prove they're human, give the person that link and call again only " +
                 "after they say it is done.")]
    public static async Task<CallToolResult> GetProduct(
        AmazonClient amazon,
        [Description("A 10-character ASIN such as B088NRLMPV, or a www.amazon.com product link.")] string product,
        CancellationToken ct = default)
    {
        try
        {
            var (found, image) = await amazon.GetProductAsync(product, ct);
            var result = Text(found);
            if (image is not null) result.Content.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
            return result;
        }
        catch (SiteException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static CallToolResult Text<T>(T answer) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(answer, AmazonJson.Options) }],
    };
}
