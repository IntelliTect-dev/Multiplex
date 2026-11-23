using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>How the client behaves toward Amazon: who it says it is, how it paces, and what it does with each answer.</summary>
public class AmazonClientTests
{
    private readonly AmazonHarness _h = new();

    // ---- identity ----------------------------------------------------------------------------

    [Fact]
    public async Task SendsAnHonestIdentity()
    {
        await _h.Search();
        await _h.Product();

        Assert.All(_h.Amazon.Requests, r =>
        {
            Assert.StartsWith("Multiplex-Amazon/", r.Headers["User-Agent"]);
            Assert.DoesNotContain("Mozilla", r.Headers["User-Agent"]);
            Assert.DoesNotContain(r.Headers.Keys, k => k.StartsWith("sec-ch-ua", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Referer", r.Headers.Keys);
            Assert.DoesNotContain("Cookie", r.Headers.Keys);
            Assert.Equal("en-US", r.Headers["Accept-Language"]);
        });
    }

    [Fact]
    public async Task OnlyHttpsIsEverRequested()
    {
        _h.Amazon.On("/dp/B088NRLMPV", () => FakeAmazon.Redirect("http://www.amazon.com/dp/B09LCJPZ1P"));

        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.All(_h.Amazon.Requests, r => Assert.Equal("https", r.Url.Scheme));
        Assert.DoesNotContain(_h.Amazon.Requests, r => r.Url.AbsolutePath == "/dp/B09LCJPZ1P");
    }

    // ---- pacing ------------------------------------------------------------------------------

    [Fact]
    public async Task SpacesRequestsFromSeparateCalls_AtLeast3SecondsApart()
    {
        await _h.Search("first");
        await _h.Search("second");

        var pages = _h.Amazon.Fetches;
        Assert.Equal(2, pages.Count);
        Assert.True(pages[1].At - pages[0].At >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ASearchOf3Pages_MakesThreeRequests_InOrder_2To3SecondsApart()
    {
        var answer = await _h.Search(pages: 3);

        var pages = _h.Amazon.Fetches;
        Assert.Equal(["/s?k=usb%20c%20charging%20cable&s=relevanceblender", "/s?k=usb%20c%20charging%20cable&s=relevanceblender&page=2", "/s?k=usb%20c%20charging%20cable&s=relevanceblender&page=3"],
            pages.Select(p => p.Url.PathAndQuery));
        Assert.All(pages.Zip(pages.Skip(1)), pair => Assert.InRange(pair.Second.At - pair.First.At, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));
        Assert.Equal([1, 2, 3], answer.PagesRead);
        Assert.Equal(4, answer.NextPage);
        Assert.Equal(3, answer.Sources.Count);
    }

    [Fact]
    public async Task ATenPageSearch_FitsTheDeadline()
    {
        // 1.6 seconds a page was the slowest fetch seen against the real site.
        _h.Amazon.On(u => u.AbsolutePath == "/s", _ =>
        {
            _h.Time.Advance(TimeSpan.FromSeconds(1.6));
            return FakeAmazon.Fixture("search-results.html");
        });
        var started = _h.Time.GetUtcNow();

        var answer = await _h.Search(pages: 10);

        Assert.Equal(10, answer.PagesRead.Count);
        Assert.True(_h.Time.GetUtcNow() - started < AmazonClient.CallDeadline);
        Assert.Empty(answer.Warnings);
    }

    [Fact]
    public async Task ASearchThatReachesItsDeadline_StartsNoNewPage_AndReturnsWhatItRead()
    {
        _h.Amazon.On(u => u.AbsolutePath == "/s", _ =>
        {
            _h.Time.Advance(TimeSpan.FromSeconds(20));
            return FakeAmazon.Fixture("search-results.html");
        });

        var answer = await _h.Search(pages: 10);

        // Pages 1 to 3 each start inside the 50 seconds (at about 0, 22 and 44); page 4 would start after them.
        Assert.Equal([1, 2, 3], answer.PagesRead);
        Assert.Equal(4, answer.NextPage);
        Assert.Contains(answer.Warnings, w => w.StartsWith("Page 4 was not read") && w.Contains("ran out of time"));
        Assert.Equal(3, _h.Amazon.Fetches.Count);
        Assert.NotEmpty(answer.Results);
    }

    [Fact]
    public async Task AmazonsLastPage_EndsTheSearch()
    {
        _h.Amazon.On(u => u.Query.Contains("page=2"), _ => FakeAmazon.Html(
            AmazonFixture.Read("search-page-2.html").Replace("s-pagination-next", "s-pagination-gone")));

        var answer = await _h.Search(pages: 5);

        Assert.Equal([1, 2], answer.PagesRead);
        Assert.Null(answer.NextPage);
        Assert.Equal(2, _h.Amazon.Fetches.Count);
    }

    [Fact]
    public async Task StartPage15WithPages10_StopsAtPage20()
    {
        var answer = await _h.Client.SearchAsync(new AmazonSearchRequest("cable", Pages: 10, StartPage: 15), CancellationToken.None);

        // Six pages fit the deadline or not, but none is ever asked for past page 20.
        Assert.All(answer.PagesRead, p => Assert.InRange(p, 15, 20));
        Assert.DoesNotContain(_h.Amazon.Fetches, r => r.Url.Query.Contains("page=21"));
    }

    [Fact]
    public async Task TheHourlyCap_EndsASearchPartWay_WithWhatItRead_AndSaysWhenItFreesUp()
    {
        var capped = new AmazonHarness(new SiteGateOptions { Site = "Amazon", HourlyCap = 3, DailyCap = 500 });

        var answer = await capped.Search(pages: 5);

        // robots.txt counted as the first of the three.
        Assert.Equal([1, 2], answer.PagesRead);
        Assert.Equal(3, answer.NextPage);
        Assert.Contains(answer.Warnings, w => w.Contains("this hour's limit of 3") && w.Contains("try again after"));
        Assert.Null(answer.HandOver);
    }

    [Fact]
    public async Task ACapReachedOnTheFirstPage_IsAnError()
    {
        var capped = new AmazonHarness(new SiteGateOptions { Site = "Amazon", HourlyCap = 1, DailyCap = 500 });

        await Assert.ThrowsAsync<SiteBusyException>(() => capped.Search());
    }

    // ---- blocks and the pause ----------------------------------------------------------------

    [Theory]
    [InlineData("blocked-automated-access.html", 503, "automated access needs its permission")]
    [InlineData("blocked-script-check.html", 200, "only a browser can pass")]
    public async Task ABlock_IsFetchedOnce_NeverRetried_AndPausesTheService(string fixture, int status, string reason)
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Fixture(fixture, status));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains(reason, blocked.Message);
        Assert.Contains("at 14:05 UTC", blocked.Message);
        Assert.Contains("paused until 14:20 UTC", blocked.Message);
        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task APause_FailsLaterCallsWithoutContactingAmazon_Until15MinutesHavePassed()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Fixture("blocked-automated-access.html", 503));
        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());
        var sent = _h.Amazon.Requests.Count;

        var paused = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());
        Assert.Contains("paused until 14:20 UTC", paused.Message);
        Assert.Equal(sent, _h.Amazon.Requests.Count);

        _h.Time.Advance(TimeSpan.FromMinutes(15));
        await _h.Search();
        Assert.Equal(sent + 1, _h.Amazon.Requests.Count);
    }

    [Fact]
    public async Task RepeatedBlocksDoubleThePause_UpTo2Hours_AndASuccessResetsIt()
    {
        var blocking = true;
        _h.Amazon.On(u => u.AbsolutePath.StartsWith("/dp/") && blocking, _ => FakeAmazon.Fixture("blocked-automated-access.html", 503));
        var pauses = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            pauses.Add(await BlockedMinutes());
            _h.Time.Advance(TimeSpan.FromMinutes(pauses[^1] + 1));
        }
        Assert.Equal([15, 30, 60, 120, 120, 120], pauses);

        blocking = false;
        await _h.Product();
        blocking = true;
        _h.Time.Advance(AmazonClient.CacheLifetime);
        Assert.Equal(15, await BlockedMinutes());

        // How long the pause a blocked call reports is, to the nearest minute.
        async Task<int> BlockedMinutes()
        {
            var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());
            var until = TimeOnly.ParseExact(blocked.Message[^10..^5], "HH:mm").ToTimeSpan();
            var length = until - _h.Time.GetUtcNow().TimeOfDay;
            if (length < TimeSpan.FromMinutes(-1)) length += TimeSpan.FromDays(1);
            return (int)Math.Round(length.TotalMinutes);
        }
    }

    [Fact]
    public async Task ACallQueuedBehindABlock_FailsWithoutReachingAmazon()
    {
        var release = new TaskCompletionSource();
        _h.Amazon.On(u => u.AbsolutePath == "/dp/B088NRLMPV", _ =>
        {
            release.Task.Wait();
            return FakeAmazon.Fixture("blocked-automated-access.html", 503);
        });
        await _h.Search("warm up"); // robots.txt is in memory before the two calls race

        var first = Task.Run(() => _h.Product("B088NRLMPV"));
        while (_h.Amazon.Fetches.Count < 2) await Task.Delay(10);
        var second = _h.Product("B09LCJPZ1P"); // queues behind the first, which is in flight
        release.SetResult();

        await Assert.ThrowsAsync<SiteBlockedException>(() => first);
        await Assert.ThrowsAsync<SiteBlockedException>(() => second);
        Assert.DoesNotContain(_h.Amazon.Requests, r => r.Url.AbsolutePath == "/dp/B09LCJPZ1P");
    }

    // ---- throttling and errors ---------------------------------------------------------------

    [Fact]
    public async Task A429WithRetryAfter_IsRetriedOnce_AfterThatWait()
    {
        var answers = new Queue<Func<HttpResponseMessage>>([
            () => FakeAmazon.Status(429, TimeSpan.FromSeconds(20)),
            () => FakeAmazon.Fixture("product-popular.html")]);
        _h.Amazon.On("/dp/", () => answers.Dequeue()());

        var (product, _) = await _h.Product();

        Assert.Equal("B088NRLMPV", product.Asin);
        var tries = _h.Amazon.Fetches.Where(r => r.Url.AbsolutePath.StartsWith("/dp/")).ToList();
        Assert.Equal(2, tries.Count);
        Assert.True(tries[1].At - tries[0].At >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ASecond429_IsTheAnswer()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Status(429, TimeSpan.FromSeconds(5)));

        var failed = await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Product());

        Assert.Contains("too many requests", failed.Message);
        Assert.Equal(2, _h.Amazon.Fetches.Count);
    }

    [Fact]
    public async Task A429WithoutRetryAfter_PausesTheService()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Status(429));

        var paused = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains("too many requests", paused.Message);
        Assert.Single(_h.Amazon.Fetches);
        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());
        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task ARetryAfterOver60Seconds_IsNotWaitedOut_AndTheErrorNamesTheTime()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Status(429, TimeSpan.FromMinutes(25)));

        var failed = await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Product());

        Assert.Contains("until 14:30 UTC", failed.Message);
        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task APlain503_IsRetriedOnceAfter5Seconds_ThenReportedAsUnavailable()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Status(503));

        var failed = await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Product());

        Assert.Contains("error (503)", failed.Message);
        var tries = _h.Amazon.Fetches;
        Assert.Equal(2, tries.Count);
        Assert.True(tries[1].At - tries[0].At >= TimeSpan.FromSeconds(5));
        // An error is not a block: nothing is paused.
        _h.Amazon.On("/dp/", () => FakeAmazon.Fixture("product-popular.html"));
        await _h.Product();
    }

    [Fact]
    public async Task WhenAmazonCannotBeReached_TheErrorSaysSo_AndNothingIsPaused()
    {
        var down = true;
        _h.Amazon.On(u => u.AbsolutePath.StartsWith("/dp/") && down, _ => throw new HttpRequestException("connection refused"));

        var failed = await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Product());

        Assert.Equal("Amazon could not be reached; try again in a few minutes.", failed.Message);
        down = false;
        await _h.Product();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATimeoutOrADroppedConnectionOnPage3_ReturnsPages1And2_WithAWarning(bool timeout)
    {
        _h.Amazon.On(u => u.Query.Contains("page=3"), _ => timeout
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")
            : throw new HttpRequestException("connection reset"));

        var answer = await _h.Search(pages: 5);

        Assert.Equal([1, 2], answer.PagesRead);
        Assert.Equal(3, answer.NextPage);
        Assert.Contains(answer.Warnings, w => w == "Page 3 was not read: Amazon could not be reached; try again in a few minutes.");
        Assert.NotEmpty(answer.Results);
    }

    [Fact]
    public async Task AnUnknownAsin_IsNotFound_NamingIt()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Fixture("not-found.html", 404));

        var missing = await Assert.ThrowsAsync<SiteNotFoundException>(() => _h.Product("B0ZZZZZZZ9"));

        Assert.Equal("Amazon has no product with the ASIN B0ZZZZZZZ9.", missing.Message);
    }

    [Fact]
    public async Task AChangedLayout_IsANamedError_NotAnEmptyAnswer()
    {
        _h.Amazon.On("/s", () => FakeAmazon.Html("<html><body><div id='new-layout'>results</div></body></html>"));

        var changed = await Assert.ThrowsAsync<SiteLayoutChangedException>(() => _h.Search());

        Assert.Equal("the search results list", changed.Missing);
    }

    // ---- redirects ---------------------------------------------------------------------------

    [Fact]
    public async Task ARedirectToAnotherProductPage_IsFollowedOnce_BackThroughTheGate()
    {
        _h.Amazon.On(u => u.AbsolutePath == "/dp/B088NRLMPV", _ => FakeAmazon.Redirect("/Anker-Cable/dp/B088NRLMPV?th=1"));
        _h.Amazon.On(u => u.AbsolutePath == "/Anker-Cable/dp/B088NRLMPV", _ => FakeAmazon.Fixture("product-popular.html"));

        var (product, _) = await _h.Product();

        Assert.Equal("B088NRLMPV", product.Asin);
        var pages = _h.Amazon.Fetches.Where(r => r.Url.Host == "www.amazon.com").ToList();
        Assert.Equal(["/dp/B088NRLMPV", "/Anker-Cable/dp/B088NRLMPV"], pages.Select(p => p.Url.AbsolutePath));
        Assert.True(pages[1].At - pages[0].At >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ASecondRedirect_IsAnError()
    {
        _h.Amazon.On("/dp/B088NRLMPV", () => FakeAmazon.Redirect("/dp/B09LCJPZ1P"));
        _h.Amazon.On("/dp/B09LCJPZ1P", () => FakeAmazon.Redirect("/dp/B088NRLMPV"));

        await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Product());

        Assert.Equal(2, _h.Amazon.Fetches.Count);
    }

    [Theory]
    [InlineData("/ap/signin?return_to=x", "asked for a sign-in")]
    [InlineData("/gp/cart/view.html", "redirected or refused")]
    [InlineData("https://example.com/dp/B088NRLMPV", "redirected or refused")]
    public async Task ARedirectOutsideProductPages_IsTreatedAsABlock_AndNotFollowed(string location, string reason)
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Redirect(location));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains(reason, blocked.Message);
        Assert.Single(_h.Amazon.Fetches);
        Assert.All(_h.Amazon.Requests, r => Assert.NotEqual("example.com", r.Url.Host));
    }

    // ---- robots.txt --------------------------------------------------------------------------

    [Fact]
    public async Task RobotsTxtIsFetchedFirst_AndAtMostOnceADay()
    {
        await _h.Search("one");
        await _h.Search("two");
        Assert.Equal("/robots.txt", _h.Amazon.Requests[0].Url.AbsolutePath);
        Assert.Single(_h.Amazon.Requests, r => r.Url.AbsolutePath == "/robots.txt");

        _h.Time.Advance(TimeSpan.FromDays(1));
        await _h.Search("three");
        Assert.Equal(2, _h.Amazon.Requests.Count(r => r.Url.AbsolutePath == "/robots.txt"));
        // A stale copy was refreshed before the call queued, and the call still went out.
        Assert.Equal("/s", _h.Amazon.Requests[^1].Url.AbsolutePath);
    }

    [Fact]
    public async Task APathRobotsTxtDisallows_IsRefused_WithoutARequest()
    {
        _h.Amazon.On("/robots.txt", () => FakeAmazon.Html("User-agent: Multiplex-Amazon\nDisallow: /s\n").AsPlainText());

        var refused = await Assert.ThrowsAsync<SiteBusyException>(() => _h.Search());

        Assert.Equal("Amazon's robots.txt does not allow this service to read /s on www.amazon.com.", refused.Message);
        Assert.Empty(_h.Amazon.Fetches);
        // Product pages are still allowed by that file.
        await _h.Product();
    }

    [Fact]
    public async Task AMissingRobotsTxt_AllowsEverything()
    {
        _h.Amazon.On("/robots.txt", () => FakeAmazon.Status(404));

        await _h.Search();

        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task ABlockOnRobotsTxt_PausesTheService()
    {
        _h.Amazon.On("/robots.txt", () => FakeAmazon.Fixture("blocked-continue-shopping.html"));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());

        Assert.Contains("robots.txt", blocked.Message);
        Assert.Empty(_h.Amazon.Fetches);
        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());
        Assert.Single(_h.Amazon.Requests);
    }

    [Fact]
    public async Task ARobotsTxtThatCannotBeRead_StopsTheCall_UnlessAnOlderCopyIsHeld()
    {
        _h.Amazon.On(u => u.AbsolutePath == "/robots.txt" && u.Host == "www.amazon.com", _ => FakeAmazon.Status(500));
        var failed = await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Search());
        Assert.Contains("robots.txt could not be read", failed.Message);
        Assert.Empty(_h.Amazon.Fetches);

        var held = new AmazonHarness();
        await held.Search("first");
        held.Time.Advance(TimeSpan.FromDays(1));
        held.Amazon.On("/robots.txt", () => FakeAmazon.Status(500));
        await held.Search("second"); // the day-old copy still stands
        Assert.Equal(2, held.Amazon.Fetches.Count);
        // And the refresh is not tried again on every call, only after a few minutes.
        await held.Search("third");
        Assert.Equal(2, held.Amazon.Requests.Count(r => r.Url.AbsolutePath == "/robots.txt"));
        held.Time.Advance(AmazonClient.RobotsRetryAfterFailure);
        await held.Search("fourth");
        Assert.Equal(3, held.Amazon.Requests.Count(r => r.Url.AbsolutePath == "/robots.txt"));
    }

    [Fact]
    public async Task AWebPageWhereRobotsTxtShouldBe_IsNotReadAsNoRules()
    {
        _h.Amazon.On("/robots.txt", () => FakeAmazon.Html("<html><body>Welcome</body></html>"));

        await Assert.ThrowsAsync<SiteUnavailableException>(() => _h.Search());

        Assert.Empty(_h.Amazon.Fetches);
    }

    // ---- cache -------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondLookupWithin10Minutes_IsServedFromCache_WithTheSameFetchedAt()
    {
        var (first, firstImage) = await _h.Product();
        _h.Time.Advance(TimeSpan.FromMinutes(9));
        var sent = _h.Amazon.Requests.Count;

        var (second, secondImage) = await _h.Product("https://www.amazon.com/dp/B088NRLMPV?th=1");

        Assert.Equal(sent, _h.Amazon.Requests.Count);
        Assert.Equal(first.FetchedAt, second.FetchedAt);
        Assert.NotNull(firstImage);
        Assert.Same(firstImage, secondImage);
    }

    [Fact]
    public async Task After10Minutes_ItFetchesAgain()
    {
        var (first, _) = await _h.Product();
        _h.Time.Advance(AmazonClient.CacheLifetime);

        var (second, _) = await _h.Product();

        Assert.True(second.FetchedAt > first.FetchedAt);
        Assert.Equal(2, _h.Amazon.Fetches.Count(r => r.Url.AbsolutePath == "/dp/B088NRLMPV"));
    }

    [Fact]
    public async Task ASearchAfterAnEarlierOne_ReadsSharedPagesFromTheCache_AndReSortingCostsNothing()
    {
        var one = await _h.Search(pages: 1);
        var three = await _h.Search(pages: 3);
        Assert.Equal(3, _h.Amazon.Fetches.Count);
        Assert.Equal(one.FetchedAt, three.FetchedAt); // the oldest page's time: an answer never claims to be newer than it is

        // Our own minimum is applied after the cache: another value over the same pages asks Amazon nothing.
        var strict = await _h.Search(pages: 3, minRatingsCount: 50_000);
        Assert.Equal(3, _h.Amazon.Fetches.Count);
        Assert.True(strict.Results.Count < three.Results.Count);
        Assert.All(strict.Results, r => Assert.True(r.RatingsCount >= 50_000));
    }

    [Fact]
    public async Task TheCacheHoldsAtMost200Answers()
    {
        // The gate spaces 201 searches over about ten minutes of fake time, which is also the cache's
        // lifetime, so this harness has roomy caps and the checks come straight after the last search.
        var h = new AmazonHarness(new SiteGateOptions { Site = "Amazon", HourlyCap = 10_000, DailyCap = 10_000, PageSpacing = TimeSpan.FromSeconds(1), PageJitter = TimeSpan.Zero });
        for (var i = 0; i < AmazonClient.CacheSize + 1; i++) await h.Search($"query {i}");
        var sent = h.Amazon.Fetches.Count;

        await h.Search($"query {AmazonClient.CacheSize}"); // the newest is still held
        await h.Search("query 1");                          // and so is the second oldest
        Assert.Equal(sent, h.Amazon.Fetches.Count);
        await h.Search("query 0");                          // the oldest was evicted
        Assert.Equal(sent + 1, h.Amazon.Fetches.Count);
    }

    [Fact]
    public async Task TwoCallsForTheSameProductAtOnce_ShareOneFetch()
    {
        var release = new TaskCompletionSource();
        _h.Amazon.On("/dp/", () =>
        {
            release.Task.Wait();
            return FakeAmazon.Fixture("product-popular.html");
        });
        await _h.Search("warm up");

        var first = Task.Run(() => _h.Product());
        while (!_h.Amazon.Fetches.Any(r => r.Url.AbsolutePath.StartsWith("/dp/"))) await Task.Delay(10);
        var second = _h.Product();
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(_h.Amazon.Fetches, r => r.Url.AbsolutePath.StartsWith("/dp/"));
        Assert.Same(results[0].Product, results[1].Product);
    }

    // ---- the main image ----------------------------------------------------------------------

    [Fact]
    public async Task AProductCall_FetchesItsMainImage_FromTheImageHost_AfterItsRobotsTxt()
    {
        var (product, image) = await _h.Product();

        Assert.Equal(FakeAmazon.Jpeg, image!.Data);
        Assert.Equal("image/jpeg", image.MimeType);
        var media = _h.Amazon.Requests.Where(r => r.Url.Host == "m.media-amazon.com").ToList();
        Assert.Equal(["/robots.txt", new Uri(product.MainImageUrl!).AbsolutePath], media.Select(r => r.Url.AbsolutePath));
        Assert.Equal("image/*", media[1].Headers["Accept"]);
        Assert.Empty(product.Warnings);
    }

    [Fact]
    public async Task ImageRequests_GoThroughTheGate_AndCountTowardTheCaps()
    {
        // Five requests: both hosts' robots.txt, the page, the image, and one more page.
        var capped = new AmazonHarness(new SiteGateOptions { Site = "Amazon", HourlyCap = 4, DailyCap = 500 });

        var (_, image) = await capped.Product();
        Assert.NotNull(image);
        var page = capped.Amazon.Requests.Single(r => r.Url.AbsolutePath == "/dp/B088NRLMPV");
        var picture = capped.Amazon.Requests.Last();
        Assert.True(picture.At - page.At >= TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<SiteBusyException>(() => capped.Search());
    }

    [Theory]
    [InlineData("https://images.example.com/I/1.jpg")]
    [InlineData("http://m.media-amazon.com/images/I/1.jpg")]
    public async Task AnImageOnAnUnlistedHost_OrNotOverHttps_IsNotFetched(string address)
    {
        var html = AmazonFixture.Read("product-popular.html");
        var real = AmazonProductPage.Gallery(html).Main!;
        _h.Amazon.On("/dp/", () => FakeAmazon.Html(html.Replace(real, address)));

        var (product, image) = await _h.Product();

        Assert.Null(image);
        Assert.Contains(product.Warnings, w => w.Contains("not on one of Amazon's image hosts"));
        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImage_IsDropped_AndTheProductStillComesBack()
    {
        _h.Amazon.On(u => u.Host == "m.media-amazon.com" && u.AbsolutePath != "/robots.txt", _ => FakeAmazon.Html("<html>not a picture</html>"));

        var (product, image) = await _h.Product();

        Assert.Null(image);
        Assert.Equal("B088NRLMPV", product.Asin);
        Assert.Contains(product.Warnings, w => w.Contains("main image could not be fetched"));
    }

    [Fact]
    public async Task AFailedImageFetch_StillReturnsTheProduct_WithAWarning()
    {
        _h.Amazon.On(u => u.Host == "m.media-amazon.com" && u.AbsolutePath != "/robots.txt", _ => FakeAmazon.Status(403));

        var (product, image) = await _h.Product();

        Assert.Null(image);
        Assert.Contains(product.Warnings, w => w.Contains("main image could not be fetched"));
        // A picture that would not come is not a block: the service is not paused.
        await _h.Search();
    }

    [Fact]
    public async Task ARefusalMetWhileFetchingTheImage_IsTheCallsAnswer_NotAWarning()
    {
        _h.Amazon.On(u => u.Host == "m.media-amazon.com" && u.AbsolutePath == "/robots.txt",
            _ => FakeAmazon.Fixture("blocked-automated-access.html", 503));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains("paused until", blocked.Message);
    }

    [Fact]
    public async Task AnImageTheImageHostsRobotsTxtDisallows_IsNotFetched()
    {
        _h.Amazon.On(u => u.Host == "m.media-amazon.com" && u.AbsolutePath == "/robots.txt",
            _ => FakeAmazon.Html("User-agent: *\nDisallow: /images/\n").AsPlainText());

        var (product, image) = await _h.Product();

        Assert.Null(image);
        Assert.DoesNotContain(_h.Amazon.Fetches, r => r.Url.Host == "m.media-amazon.com");
        Assert.NotEmpty(product.Warnings);
    }

    [Fact]
    public async Task AnOversizedImage_IsNotIncluded()
    {
        _h.Amazon.On(u => u.Host == "m.media-amazon.com" && u.AbsolutePath != "/robots.txt",
            _ => FakeAmazon.Image(data: new byte[AmazonClient.MaxImageBytes + 1]));

        var (product, image) = await _h.Product();

        Assert.Null(image);
        Assert.Contains(product.Warnings, w => w.Contains("too large"));
        Assert.True(AmazonClient.MaxImageBytes <= 512 * 1024); // what one cached product can hold
    }

    // ---- logging -----------------------------------------------------------------------------

    [Fact]
    public async Task TheLogNeverHoldsSearchTextOrPageBodies()
    {
        await _h.Search("zebra-striped unicycle helmet");
        await _h.Product();

        Assert.NotEmpty(_h.Log.Lines);
        Assert.Contains(_h.Log.Lines, l => l.Contains("search page 1") && l.Contains("200"));
        Assert.All(_h.Log.Lines, l =>
        {
            Assert.DoesNotContain("zebra", l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unicycle", l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<div", l);
            Assert.DoesNotContain("Anker USB C", l);
        });
    }
}

internal static class FakeAmazonResponses
{
    /// <summary>The same answer, setting a cookie the way Amazon does.</summary>
    public static HttpResponseMessage WithCookie(this HttpResponseMessage response, string setCookie)
    {
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
        return response;
    }

    /// <summary>The same body served as <c>text/plain</c>, the way a robots.txt is.</summary>
    public static HttpResponseMessage AsPlainText(this HttpResponseMessage response)
    {
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        return response;
    }
}
