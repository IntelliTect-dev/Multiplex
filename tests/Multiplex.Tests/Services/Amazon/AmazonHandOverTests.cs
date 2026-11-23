using Multiplex.Web.HtmlSites;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

public class AmazonRobotCheckPageTests
{
    private static readonly Uri Page = new("https://www.amazon.com/dp/B088NRLMPV");

    [Fact]
    public void TheRealContinueShoppingCheck_IsAFormWithEveryFieldFilledIn_AndNothingToType()
    {
        var check = AmazonRobotCheckPage.TryParse(AmazonFixture.Read("blocked-continue-shopping.html"), Page)!;

        Assert.False(check.NeedsTyping);
        Assert.Null(check.Picture);
        Assert.Equal("https://www.amazon.com/errors_page/validateCaptcha?amzn=TOKEN-PLACEHOLDER&amzn-r=%2F&field-keywords=ABCDEF",
            check.AnswerUrl(null).AbsoluteUri);
    }

    [Fact]
    public void TheTypeTheCharactersCheck_HasAPictureAndOneFieldToType()
    {
        var check = AmazonRobotCheckPage.TryParse(AmazonFixture.Read("blocked-robot-check.html"), Page)!;

        Assert.True(check.NeedsTyping);
        Assert.Equal("images-na.ssl-images-amazon.com", check.Picture!.Host);
        Assert.Equal("https://www.amazon.com/errors/validateCaptcha?amzn=TOKEN-PLACEHOLDER&amzn-r=%2Fs%3Fk%3Dcable&field-keywords=KX7%20P",
            check.AnswerUrl("KX7 P").AbsoluteUri);
    }

    [Theory]
    [InlineData("action=\"/errors/validateCaptcha\"", "action=\"https://example.com/errors/validateCaptcha\"")] // off Amazon
    [InlineData("action=\"/errors/validateCaptcha\"", "action=\"http://www.amazon.com/errors/validateCaptcha\"")] // not HTTPS
    [InlineData("action=\"/errors/validateCaptcha\"", "action=\"/gp/cart/validateCaptcha\"")]                     // not a check address
    [InlineData("method=\"get\"", "method=\"post\"")]                                                             // an answer is only ever a GET
    [InlineData("https://images-na.ssl-images-amazon.com/captcha/", "https://images.example.com/captcha/")]       // a picture from elsewhere
    [InlineData("https://images-na.ssl-images-amazon.com/captcha/", "http://images-na.ssl-images-amazon.com/captcha/")]
    public void ACheckWhoseAddressesAreNotAmazons_IsNotOneToHandOver(string from, string to)
    {
        var html = AmazonFixture.Read("blocked-robot-check.html");
        Assert.Contains(from, html);

        Assert.Null(AmazonRobotCheckPage.TryParse(html.Replace(from, to), Page));
    }

    [Fact]
    public void APageWithNoCheckForm_IsNotOne()
    {
        Assert.Null(AmazonRobotCheckPage.TryParse(AmazonFixture.Read("blocked-script-check.html"), Page));
        Assert.Null(AmazonRobotCheckPage.TryParse(AmazonFixture.Read("product-popular.html"), Page));
    }
}

/// <summary>What happens when Amazon asks for a person: the link, the wait, the answer and what follows it.</summary>
public class AmazonHandOverTests
{
    private readonly AmazonHarness _h = new();

    private ISiteHandOver HandOver => _h.Client;

    /// <summary>Makes product pages answer with a robot check that sets a session cookie, and returns the hand-over the next call opens.</summary>
    private async Task<SiteHandOverException> MeetACheck(string fixture = "blocked-continue-shopping.html", string asin = "B088NRLMPV")
    {
        _h.Amazon.On(u => u.AbsolutePath == "/dp/" + asin, _ => FakeAmazon.Fixture(fixture).WithCookie("session-id=check-session; Domain=.amazon.com; Path=/"));
        return await Assert.ThrowsAsync<SiteHandOverException>(() => _h.Product(asin));
    }

    private static string IdOf(SiteHandOverException check) => check.Url.Segments[^1];

    /// <summary>From here on Amazon accepts the check's answer the way it does a real one: a redirect back, with a cookie.</summary>
    private void AmazonAcceptsTheAnswer(string backTo = "/s?k=cable") =>
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Redirect(backTo).WithCookie("x-amz-captcha-1=passed; Path=/"));

    private void AmazonAnswersNormallyAgain() =>
        _h.Amazon.On(u => u.AbsolutePath.StartsWith("/dp/"), _ => FakeAmazon.Fixture("product-popular.html"));

    [Fact]
    public async Task ARobotCheck_FailsAtOnceWithALink_AndLaterCallsContactNothing()
    {
        var check = await MeetACheck();

        Assert.Matches("^https://mcp\\.example\\.test/check/amazon/[0-9a-f]{32}$", check.Url.AbsoluteUri);
        Assert.Equal(_h.Time.GetUtcNow() + AmazonClient.CheckLifetime, check.ExpiresAt);
        Assert.Equal(
            $"Amazon wants a person to prove they're human before it answers more requests. Open {check.Url} and press the button there, " +
            "then ask me to try again. The link works until 14:15 UTC.", check.Message);
        var sent = _h.Amazon.Requests.Count;

        var again = await Assert.ThrowsAsync<SiteHandOverException>(() => _h.Search());
        var andAgain = await Assert.ThrowsAsync<SiteHandOverException>(() => _h.Product("B09LCJPZ1P"));

        Assert.Equal(check.Url, again.Url);
        Assert.Equal(check.Url, andAgain.Url);
        Assert.Equal(sent, _h.Amazon.Requests.Count);
    }

    [Fact]
    public async Task ACallQueuedBehindACheck_GetsTheSameLink_WithoutReachingAmazon()
    {
        var release = new TaskCompletionSource();
        _h.Amazon.On(u => u.AbsolutePath == "/dp/B088NRLMPV", _ =>
        {
            release.Task.Wait();
            return FakeAmazon.Fixture("blocked-continue-shopping.html");
        });
        await _h.Search("warm up");

        var first = Task.Run(() => _h.Product("B088NRLMPV"));
        while (_h.Amazon.Fetches.Count < 2) await Task.Delay(10);
        var second = _h.Product("B09LCJPZ1P");
        release.SetResult();

        var opened = await Assert.ThrowsAsync<SiteHandOverException>(() => first);
        var queued = await Assert.ThrowsAsync<SiteHandOverException>(() => second);
        Assert.Equal(opened.Url, queued.Url);
        Assert.DoesNotContain(_h.Amazon.Requests, r => r.Url.AbsolutePath == "/dp/B09LCJPZ1P");
    }

    [Fact]
    public async Task CachedAnswers_AreStillServed_WhileACheckIsOpen()
    {
        var before = await _h.Search("cable");
        await MeetACheck();

        var during = await _h.Search("cable");

        Assert.Equal(before.FetchedAt, during.FetchedAt);
    }

    [Fact]
    public async Task ASearchThatMeetsACheckOnPage3_ReturnsPages1And2_WithTheLinkAtTheTop()
    {
        _h.Amazon.On(u => u.Query.Contains("page=3"), _ => FakeAmazon.Fixture("blocked-continue-shopping.html"));

        var answer = await _h.Search(pages: 5);

        Assert.Equal([1, 2], answer.PagesRead);
        Assert.Equal(3, answer.NextPage);
        Assert.NotNull(answer.HandOver);
        Assert.StartsWith("https://mcp.example.test/check/amazon/", answer.HandOver.Url);
        Assert.Contains(answer.Warnings, w => w.StartsWith("Page 3 was not read") && w.Contains(answer.HandOver.Url));
        Assert.NotEmpty(answer.Results);
    }

    [Fact]
    public async Task TheAnswer_IsSubmittedInTheChecksOwnSession_AsItself()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        var view = HandOver.Find(IdOf(check))!;
        Assert.False(view.NeedsTyping);

        var result = await HandOver.AnswerAsync(IdOf(check), typed: "ignored: this check has nothing to type", CancellationToken.None);

        Assert.Equal(HandOverOutcome.Passed, result.Outcome);
        var sent = _h.Amazon.Requests[^1];
        // Amazon's own form, field for field, with the value Amazon itself filled in.
        Assert.Equal("https://www.amazon.com/errors_page/validateCaptcha?amzn=TOKEN-PLACEHOLDER&amzn-r=%2F&field-keywords=ABCDEF", sent.Url.AbsoluteUri);
        Assert.Equal("session-id=check-session", sent.Headers["Cookie"]);
        Assert.StartsWith("Multiplex-Amazon/", sent.Headers["User-Agent"]);
        Assert.DoesNotContain("Mozilla", sent.Headers["User-Agent"]);
        // Through the gate like any request: spaced after the page that showed the check.
        Assert.True(sent.At - _h.Amazon.Requests[^2].At >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task APassedCheck_ReopensAmazonRequests_AndLaterRequestsCarryItsCookies_ToAmazonOnly()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        AmazonAnswersNormallyAgain();

        var (product, image) = await _h.Product();

        Assert.Equal("B088NRLMPV", product.Asin);
        Assert.NotNull(image);
        var page = _h.Amazon.Requests.Last(r => r.Url.AbsolutePath == "/dp/B088NRLMPV");
        Assert.Equal("session-id=check-session; x-amz-captcha-1=passed", page.Headers["Cookie"]);
        Assert.All(_h.Amazon.Requests.Where(r => r.Url.Host != "www.amazon.com"), r => Assert.DoesNotContain("Cookie", r.Headers.Keys));
        Assert.Null(HandOver.Find(IdOf(check)));
    }

    [Theory]
    [InlineData("/s?k=cable")]
    [InlineData("/")]
    [InlineData("/Anker-Cable/dp/B088NRLMPV")]
    public async Task ACorrectAnswerThatRedirectsBack_IsAPass_NotABlock(string backTo)
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer(backTo);

        var result = await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);

        Assert.Equal(HandOverOutcome.Passed, result.Outcome);
    }

    [Fact]
    public async Task AnAnswerThatComesBackAsANormalPage_IsAPass()
    {
        var check = await MeetACheck();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Fixture("search-results.html"));

        Assert.Equal(HandOverOutcome.Passed, (await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("/ap/signin?return_to=x")]
    [InlineData("https://example.com/")]
    [InlineData("/gp/cart/view.html")]
    public async Task AnAnswerRedirectingToSignInOrOffAmazon_IsNotAPass_AndPausesTheService(string location)
    {
        var check = await MeetACheck();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Redirect(location));

        var result = await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);

        Assert.Equal(HandOverOutcome.Closed, result.Outcome);
        Assert.Contains("paused until", result.Message);
        Assert.Null(HandOver.Find(IdOf(check)));
        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());
        Assert.DoesNotContain(_h.Amazon.Requests, r => r.Url.Host == "example.com" || r.Url.AbsolutePath.StartsWith("/ap/"));
    }

    [Fact]
    public async Task TheTypeTheCharactersCheck_ShowsAmazonsPicture_AndSendsWhatThePersonTyped()
    {
        var check = await MeetACheck("blocked-robot-check.html");
        Assert.Contains("type the characters shown", check.Message);
        Assert.True(HandOver.Find(IdOf(check))!.NeedsTyping);

        var picture = await HandOver.PictureAsync(IdOf(check), CancellationToken.None);
        await HandOver.PictureAsync(IdOf(check), CancellationToken.None); // a reload of the page

        Assert.Equal(FakeAmazon.Jpeg, picture!.Data);
        Assert.Equal("image/jpeg", picture.MimeType);
        var fetched = Assert.Single(_h.Amazon.Requests, r => r.Url.Host == "images-na.ssl-images-amazon.com" && r.Url.AbsolutePath != "/robots.txt");
        Assert.DoesNotContain("Cookie", fetched.Headers.Keys);

        AmazonAcceptsTheAnswer();
        var result = await HandOver.AnswerAsync(IdOf(check), " kx7pq ", CancellationToken.None);

        Assert.Equal(HandOverOutcome.Passed, result.Outcome);
        Assert.EndsWith("&field-keywords=kx7pq", _h.Amazon.Requests[^1].Url.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a&amzn=x")]
    [InlineData("waytoolongtobeacaptcha")]
    public async Task SomethingThatIsNotCharactersFromAPicture_IsNotSent(string typed)
    {
        var check = await MeetACheck("blocked-robot-check.html");
        var sent = _h.Amazon.Requests.Count;

        var result = await HandOver.AnswerAsync(IdOf(check), typed, CancellationToken.None);

        Assert.Equal(HandOverOutcome.NotSent, result.Outcome);
        Assert.Equal(sent, _h.Amazon.Requests.Count);
        Assert.NotNull(HandOver.Find(IdOf(check)));
    }

    [Fact]
    public async Task AWrongAnswer_ShowsANewPicture_AndThreeWrongAnswersCloseIntoTheTimedPause()
    {
        var check = await MeetACheck("blocked-robot-check.html");
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Html(
            AmazonFixture.Read("blocked-robot-check.html").Replace("Captcha_placeholder", "Captcha_second")));

        var first = await HandOver.AnswerAsync(IdOf(check), "aaaa", CancellationToken.None);
        Assert.Equal(HandOverOutcome.WrongAnswer, first.Outcome);
        Assert.Equal(1, HandOver.Find(IdOf(check))!.WrongAnswers);
        await HandOver.PictureAsync(IdOf(check), CancellationToken.None);
        Assert.Contains(_h.Amazon.Requests, r => r.Url.AbsolutePath.Contains("Captcha_second"));

        Assert.Equal(HandOverOutcome.WrongAnswer, (await HandOver.AnswerAsync(IdOf(check), "bbbb", CancellationToken.None)).Outcome);
        var third = await HandOver.AnswerAsync(IdOf(check), "cccc", CancellationToken.None);

        Assert.Equal(HandOverOutcome.Closed, third.Outcome);
        Assert.Contains("paused until", third.Message);
        Assert.Null(HandOver.Find(IdOf(check)));
        var paused = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());
        Assert.Contains("not passed after the answers a person gave", paused.Message);
    }

    [Fact]
    public async Task AnUnansweredCheck_ExpiresAfter10Minutes_IntoTheTimedPause()
    {
        var check = await MeetACheck();
        var sent = _h.Amazon.Requests.Count;
        _h.Time.Advance(AmazonClient.CheckLifetime);

        Assert.Null(HandOver.Find(IdOf(check)));
        Assert.Equal(HandOverOutcome.Gone, (await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None)).Outcome);
        var paused = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Search());

        Assert.Contains("its robot check was not answered within 10 minutes", paused.Message);
        Assert.Contains("paused until 14:30 UTC", paused.Message);
        Assert.Equal(sent, _h.Amazon.Requests.Count);

        _h.Time.Advance(AmazonClient.FirstPause);
        AmazonAnswersNormallyAgain();
        await _h.Product();
    }

    [Fact]
    public async Task ACheckLongExpired_HasAlreadyServedItsPause()
    {
        await MeetACheck();
        _h.Time.Advance(AmazonClient.CheckLifetime + AmazonClient.FirstPause);
        AmazonAnswersNormallyAgain();

        await _h.Product();
    }

    [Fact]
    public async Task ACheckWithin15MinutesOfAPass_StopsOfferingLinks_AndSaysThePassDidNotHold()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        _h.Time.Advance(TimeSpan.FromMinutes(5));

        var again = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains("the answer is not holding", again.Message);
        Assert.Contains("paused until", again.Message);
        Assert.DoesNotContain("/check/amazon/", again.Message);
    }

    [Fact]
    public async Task ANewCheckLater_OpensANewLink_AndDropsTheOldSessionsCookies()
    {
        var first = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(first), null, CancellationToken.None);
        _h.Time.Advance(AmazonClient.PassMustHold + AmazonClient.CacheLifetime);

        _h.Amazon.On(u => u.AbsolutePath == "/dp/B09LCJPZ1P", _ => FakeAmazon.Fixture("blocked-continue-shopping.html").WithCookie("session-id=second-session; Path=/"));
        var second = await Assert.ThrowsAsync<SiteHandOverException>(() => _h.Product("B09LCJPZ1P"));
        await HandOver.AnswerAsync(IdOf(second), null, CancellationToken.None);

        Assert.NotEqual(first.Url, second.Url);
        Assert.Equal("session-id=second-session", _h.Amazon.Requests[^1].Headers["Cookie"]);
    }

    [Fact]
    public async Task ALinkAlreadyUsed_OrNeverIssued_OffersNothingToAnswer()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        var sent = _h.Amazon.Requests.Count;

        Assert.Null(HandOver.Find(IdOf(check)));
        Assert.Null(HandOver.Find("00000000000000000000000000000000"));
        Assert.Null(HandOver.Find(""));
        Assert.Equal(HandOverOutcome.Gone, (await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None)).Outcome);
        Assert.Null(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
        Assert.Equal(sent, _h.Amazon.Requests.Count);
    }

    [Fact]
    public async Task ASecondAnswerWhileTheFirstIsOut_IsRefused()
    {
        var check = await MeetACheck();
        var release = new TaskCompletionSource();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ =>
        {
            release.Task.Wait();
            return FakeAmazon.Redirect("/");
        });

        var first = Task.Run(() => HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None));
        while (!_h.Amazon.Requests.Any(r => r.Url.AbsolutePath.EndsWith("/validateCaptcha"))) await Task.Delay(10);
        var second = await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        release.SetResult();

        Assert.Equal(HandOverOutcome.NotSent, second.Outcome);
        Assert.Equal(HandOverOutcome.Passed, (await first).Outcome);
        Assert.Single(_h.Amazon.Requests, r => r.Url.AbsolutePath.EndsWith("/validateCaptcha"));
    }

    [Fact]
    public async Task AnAnswerOnItsWayWhenTheLinkExpires_StillCounts()
    {
        var check = await MeetACheck();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ =>
        {
            // The link's ten minutes run out while Amazon is still answering, and another call comes in.
            _h.Time.Advance(AmazonClient.CheckLifetime + TimeSpan.FromMinutes(1));
            var other = Assert.Throws<AggregateException>(() => _h.Search("something else").Wait());
            Assert.IsType<SiteHandOverException>(other.InnerException);
            return FakeAmazon.Redirect("/").WithCookie("x-amz-captcha-1=passed; Path=/");
        });

        var result = await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);

        Assert.Equal(HandOverOutcome.Passed, result.Outcome);
        AmazonAnswersNormallyAgain();
        await _h.Product();
        Assert.Equal("session-id=check-session; x-amz-captcha-1=passed", _h.Amazon.Requests.Last(r => r.Url.AbsolutePath == "/dp/B088NRLMPV").Headers["Cookie"]);
    }

    [Fact]
    public async Task APersonClosingThePageMidAnswer_DoesNotLoseWhatAmazonSaid()
    {
        var check = await MeetACheck();
        using var browser = new CancellationTokenSource();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ =>
        {
            browser.Cancel();
            return FakeAmazon.Redirect("/");
        });

        var result = await HandOver.AnswerAsync(IdOf(check), null, browser.Token);

        Assert.Equal(HandOverOutcome.Passed, result.Outcome);
        Assert.Null(HandOver.Find(IdOf(check)));
    }

    [Fact]
    public async Task APictureThatWillNotCome_IsNotAskedForAgainOnEveryReload()
    {
        var check = await MeetACheck("blocked-robot-check.html");
        _h.Amazon.On(u => u.AbsolutePath.Contains("/captcha/"), _ => FakeAmazon.Status(503));
        int Asked() => _h.Amazon.Requests.Count(r => r.Url.AbsolutePath.Contains("/captcha/"));

        Assert.Null(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
        Assert.Null(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
        Assert.Null(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
        Assert.Equal(1, Asked());

        _h.Time.Advance(AmazonClient.PictureRetryAfterFailure);
        _h.Amazon.On(u => u.AbsolutePath.Contains("/captcha/"), _ => FakeAmazon.Image());
        Assert.NotNull(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
        Assert.Equal(2, Asked());
    }

    [Fact]
    public async Task ManyRequestsForThePictureAtOnce_ShareOneFetch()
    {
        var check = await MeetACheck("blocked-robot-check.html");

        var pictures = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => HandOver.PictureAsync(IdOf(check), CancellationToken.None)));

        Assert.All(pictures, Assert.NotNull);
        Assert.Single(_h.Amazon.Requests, r => r.Url.AbsolutePath.Contains("/captcha/"));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/webp")]
    public async Task APictureOfATypeThatCouldCarryMoreThanAPicture_IsNotPassedOn(string type)
    {
        var check = await MeetACheck("blocked-robot-check.html");
        _h.Amazon.On(u => u.AbsolutePath.Contains("/captcha/"), _ => FakeAmazon.Image(type));

        Assert.Null(await HandOver.PictureAsync(IdOf(check), CancellationToken.None));
    }

    [Fact]
    public async Task AfterAPass_TheSessionFollowsTheCookiesAmazonSetsLater_AndDropsTheOnesItClears()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        _h.Amazon.On(u => u.AbsolutePath == "/dp/B09LCJPZ1P", _ => FakeAmazon.Fixture("product-popular.html")
            .WithCookie("session-id=rotated; Path=/")
            .WithCookie("x-amz-captcha-1=; Max-Age=0; Path=/")
            .WithCookie("i18n-prefs=USD; Expires=Tue, 23 Nov 2027 07:47:28 GMT; Path=/")
            .WithCookie("old=gone; Expires=Thu, 01 Jan 1970 00:00:00 GMT"));
        await _h.Product("B09LCJPZ1P");
        AmazonAnswersNormallyAgain();

        await _h.Search("after");

        Assert.Equal("session-id=rotated; i18n-prefs=USD", _h.Amazon.Requests[^1].Headers["Cookie"]);
    }

    [Fact]
    public async Task BeforeAnyCheck_CookiesAmazonSetsAreNotKept()
    {
        _h.Amazon.On(u => u.AbsolutePath == "/s", _ => FakeAmazon.Fixture("search-results.html").WithCookie("session-id=visitor; Path=/"));

        await _h.Search("one");
        await _h.Search("two");

        Assert.All(_h.Amazon.Requests, r => Assert.DoesNotContain("Cookie", r.Headers.Keys));
    }

    [Fact]
    public async Task APause_EndsTheSessionAPassHadOpened()
    {
        var check = await MeetACheck();
        AmazonAcceptsTheAnswer();
        await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);
        _h.Amazon.On(u => u.AbsolutePath == "/dp/B09LCJPZ1P", _ => FakeAmazon.Fixture("blocked-automated-access.html", 503));
        await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product("B09LCJPZ1P"));
        _h.Time.Advance(AmazonClient.FirstPause);

        await _h.Search("after the pause");

        Assert.DoesNotContain("Cookie", _h.Amazon.Requests[^1].Headers.Keys);
    }

    [Fact]
    public async Task ARefusedAnswerWhoseNewPageCannotBeRead_ClosesIntoAPause()
    {
        var check = await MeetACheck("blocked-robot-check.html");
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => FakeAmazon.Html(
            AmazonFixture.Read("blocked-robot-check.html").Replace("method=\"get\"", "method=\"post\"")));

        var result = await HandOver.AnswerAsync(IdOf(check), "aaaa", CancellationToken.None);

        Assert.Equal(HandOverOutcome.Closed, result.Outcome);
        Assert.Null(HandOver.Find(IdOf(check)));
    }

    [Fact]
    public async Task AnAnswerThatCouldNotBeSent_LeavesTheCheckOpen()
    {
        var check = await MeetACheck();
        _h.Amazon.On(u => u.AbsolutePath.EndsWith("/validateCaptcha"), _ => throw new HttpRequestException("connection reset"));

        var result = await HandOver.AnswerAsync(IdOf(check), null, CancellationToken.None);

        Assert.Equal(HandOverOutcome.NotSent, result.Outcome);
        Assert.Contains("could not be reached", result.Message);
        Assert.NotNull(HandOver.Find(IdOf(check)));
    }

    [Theory]
    [InlineData("action=\"/errors_page/validateCaptcha\"", "action=\"https://example.com/errors_page/validateCaptcha\"")]
    [InlineData("method=\"get\"", "method=\"post\"")]
    public async Task ACheckFormThatDoesNotGoToAmazonsCheckAddress_IsNotHandedOver_AndPauses(string from, string to)
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Html(AmazonFixture.Read("blocked-continue-shopping.html").Replace(from, to)));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains("cannot hand to a person", blocked.Message);
        Assert.DoesNotContain("/check/amazon/", blocked.Message);
        Assert.All(_h.Amazon.Requests, r => Assert.NotEqual("example.com", r.Url.Host));
    }

    [Fact]
    public async Task ARedirectToACheck_HasNoFormToHandOver_AndPauses()
    {
        _h.Amazon.On("/dp/", () => FakeAmazon.Redirect("/errors/validateCaptcha?amzn=x"));

        var blocked = await Assert.ThrowsAsync<SiteBlockedException>(() => _h.Product());

        Assert.Contains("it showed a robot check", blocked.Message);
        Assert.Single(_h.Amazon.Fetches);
    }

    [Fact]
    public async Task WhatThePersonTyped_IsNeverLogged()
    {
        var check = await MeetACheck("blocked-robot-check.html");
        AmazonAcceptsTheAnswer();

        await HandOver.AnswerAsync(IdOf(check), "zq7xk", CancellationToken.None);

        Assert.Contains(_h.Log.Lines, l => l.Contains("robot check answered by a person"));
        Assert.All(_h.Log.Lines, l =>
        {
            Assert.DoesNotContain("zq7xk", l, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TOKEN-PLACEHOLDER", l);
            Assert.DoesNotContain("check-session", l);
        });
    }
}
