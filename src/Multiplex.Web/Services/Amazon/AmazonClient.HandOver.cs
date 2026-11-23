using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Web.Services.Amazon;

/// <summary>Where this server is reached from the internet, for the link a person opens to answer a robot check.</summary>
public sealed record AmazonLinks(Func<string> BaseUrl);

/// <summary>
/// The robot-check hand-over (design section 5.7). When Amazon shows a check a person can
/// answer, the client keeps that check and its session's cookies, and every call fails at once
/// with a link to a Multiplex page. A person answers there; the client sends that answer to
/// Amazon from the session the check was shown to, because Amazon records a pass in that
/// session's cookies. Nothing here reads, guesses or fills in an answer.
/// </summary>
public sealed partial class AmazonClient : ISiteHandOver
{
    public static readonly TimeSpan CheckLifetime = TimeSpan.FromMinutes(10);
    /// <summary>A check this soon after a person's answer means the answer did not hold.</summary>
    public static readonly TimeSpan PassMustHold = TimeSpan.FromMinutes(15);
    public const int MaxWrongAnswers = 3;

    private sealed class OpenCheck
    {
        public required string Id { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required AmazonRobotCheck Form { get; set; }
        public required Dictionary<string, string> Cookies { get; init; }
        public int WrongAnswers { get; set; }
        public bool Answering { get; set; }
        /// <summary>The one fetch of this check's picture, shared by every request for it, and when it was started.</summary>
        public Task<HandOverPicture?>? PictureFetch { get; set; }
        public DateTimeOffset PictureFetchStarted { get; set; }
    }

    /// <summary>How long a picture that would not come is remembered, so reloading the page does not ask Amazon again and again.</summary>
    public static readonly TimeSpan PictureRetryAfterFailure = TimeSpan.FromSeconds(30);
    /// <summary>The only picture types passed on to a person's browser: none of them can carry script.</summary>
    private static readonly string[] PictureTypes = ["image/jpeg", "image/png", "image/gif"];

    private readonly object _checkLock = new();
    private OpenCheck? _check;
    private Dictionary<string, string> _cookies = [];
    private DateTimeOffset? _lastPassAt;

    [GeneratedRegex("^[A-Za-z0-9]{1,12}$")]
    private static partial Regex TypedAnswer();

    string ISiteHandOver.Slug => "amazon";

    string ISiteHandOver.Site => "Amazon";

    /// <summary>
    /// Throws when no request may go to Amazon: a check is waiting for a person (the same link
    /// every time), or the service is paused. A check nobody answered in time becomes a pause.
    /// </summary>
    private void ThrowIfStopped()
    {
        DateTimeOffset? lapsed = null;
        lock (_checkLock)
        {
            if (_check is { } check)
            {
                // While a person's answer is on its way to Amazon the check stays open, even past its time:
                // closing it now would throw away an answer Amazon may be accepting.
                if (_time.GetUtcNow() < check.ExpiresAt || check.Answering) throw HandOver(check);
                _check = null;
                lapsed = check.ExpiresAt;
            }
        }
        // An unanswered check falls back to the timed pause, unless that long has already gone by since.
        if (lapsed is { } at && _time.GetUtcNow() - at < FirstPause && _breaker.Pause is null)
            throw Pause("its robot check was not answered within 10 minutes");
        ThrowIfPaused();
    }

    /// <summary>What to throw for a robot check Amazon just showed: a hand-over when a person can answer it, else a pause.</summary>
    private SiteException OpenCheckFor(string body, Uri pageUrl, IReadOnlyDictionary<string, string?> cookiesSet)
    {
        lock (_checkLock)
        {
            if (_check is { } open && (_time.GetUtcNow() < open.ExpiresAt || open.Answering)) return HandOver(open);
            if (_lastPassAt is { } passed && _time.GetUtcNow() - passed < PassMustHold)
            {
                // Sending the person round again would get the same result.
                return Pause("it showed another robot check straight after a person answered one, so the answer is not holding");
            }
            if (AmazonRobotCheckPage.TryParse(body, pageUrl) is not { } form)
                return Pause("it showed a robot check that this service cannot hand to a person");

            // The answer has to come back in the session the check was shown to: keep that session's cookies, and only those.
            _cookies = [];
            _check = new OpenCheck
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                ExpiresAt = _time.GetUtcNow() + CheckLifetime,
                Form = form,
                Cookies = Merge([], cookiesSet),
            };
            _log.LogWarning("Amazon showed a robot check ({Kind}); waiting for a person until {Until:HH:mm} UTC.",
                form.NeedsTyping ? "type the characters" : "continue shopping", _check.ExpiresAt);
            return HandOver(_check);
        }
    }

    private SiteHandOverException HandOver(OpenCheck check)
    {
        var url = new Uri($"{_links.BaseUrl().TrimEnd('/')}/check/amazon/{check.Id}");
        var todo = check.Form.NeedsTyping ? "type the characters shown" : "press the button there";
        return new SiteHandOverException(
            $"Amazon wants a person to prove they're human before it answers more requests. Open {url} and {todo}, " +
            $"then ask me to try again. The link works until {check.ExpiresAt:HH:mm} UTC.",
            url, check.ExpiresAt);
    }

    HandOverView? ISiteHandOver.Find(string id)
    {
        lock (_checkLock)
        {
            return Current(id) is { } check
                ? new HandOverView(check.Id, check.ExpiresAt, check.Form.NeedsTyping, check.WrongAnswers)
                : null;
        }
    }

    /// <summary>The open, unexpired check with this id. Call with <see cref="_checkLock"/> held.</summary>
    private OpenCheck? Current(string id) =>
        _check is { } check && _time.GetUtcNow() < check.ExpiresAt
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(id ?? ""), System.Text.Encoding.UTF8.GetBytes(check.Id))
            ? check
            : null;

    async Task<HandOverPicture?> ISiteHandOver.PictureAsync(string id, CancellationToken ct)
    {
        Task<HandOverPicture?> fetch;
        lock (_checkLock)
        {
            if (Current(id) is not { } check || check.Form.Picture is not { } address) return null;
            // One fetch per check, shared by every request for it; a failed one is tried again only after a while.
            var failed = check.PictureFetch is { IsCompleted: true } done && (done.IsFaulted || done.Result is null);
            if (check.PictureFetch is null || (failed && _time.GetUtcNow() - check.PictureFetchStarted >= PictureRetryAfterFailure))
            {
                check.PictureFetchStarted = _time.GetUtcNow();
                // Started off this thread: nothing is sent while the lock is held.
                check.PictureFetch = Task.Run(() => FetchCheckPictureAsync(address));
            }
            fetch = check.PictureFetch;
        }
        return await fetch.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<HandOverPicture?> FetchCheckPictureAsync(Uri address)
    {
        try
        {
            var (image, _) = await FetchImageAsync(address, _time.GetUtcNow() + CallDeadline, ThrowIfPaused, CancellationToken.None).ConfigureAwait(false);
            return image is not null && PictureTypes.Contains(image.MimeType, StringComparer.OrdinalIgnoreCase)
                ? new HandOverPicture(image.Data, image.MimeType.ToLowerInvariant())
                : null;
        }
        catch (SiteException)
        {
            // Paused or refused while the check is open: there is no picture to show, which is not an error of the page's.
            return null;
        }
    }

    async Task<HandOverResult> ISiteHandOver.AnswerAsync(string id, string? typed, CancellationToken ct)
    {
        OpenCheck check;
        AmazonRobotCheck form;
        Dictionary<string, string> cookies;
        lock (_checkLock)
        {
            if (Current(id) is not { } current) return new(HandOverOutcome.Gone, "This link has expired or was already used.");
            if (current.Answering) return new(HandOverOutcome.NotSent, "An answer is already on its way to Amazon. Wait a moment.");
            typed = typed?.Trim();
            if (current.Form.NeedsTyping && (typed is null || !TypedAnswer().IsMatch(typed)))
                return new(HandOverOutcome.NotSent, "Type the characters shown in the picture: letters and digits only.");
            check = current;
            form = current.Form;
            cookies = new Dictionary<string, string>(current.Cookies);
            check.Answering = true;
        }
        // From here the caller's token is not used: if the person closes the page while the answer is on its
        // way, it is still sent and what Amazon said is still recorded.
        ct = CancellationToken.None;
        try
        {
            var url = form.AnswerUrl(form.NeedsTyping ? typed : null);
            var deadline = _time.GetUtcNow() + CallDeadline;
            RequireAmazonHttps(url);
            await EnsureRobotsAsync(url, deadline, ct).ConfigureAwait(false);
            ThrowIfDisallowed(url);
            var (status, headers, location, _, body, cookiesSet) = await _gate.RunAsync(SiteRequestKind.Page, null, deadline,
                () => { ThrowIfPaused(); ThrowIfDisallowed(url); },
                token => SendPageAsync(url, "the robot check's answer", cookies, token), ct).ConfigureAwait(false);
            Merge(cookies, cookiesSet);

            var answer = (int)status is >= 300 and < 400 && location is not null
                ? (IsPassTarget(AmazonPageClassifier.Resolve(url, location)) ? AmazonAnswer.Page : AmazonAnswer.Blocked)
                : AmazonPageClassifier.Classify(status, headers, url, location, body);
            _log.LogInformation("Amazon robot check answered by a person: {Status} {Answer}.", (int)status, answer);

            lock (_checkLock)
            {
                if (!ReferenceEquals(_check, check)) return new(HandOverOutcome.Gone, "This link has expired or was already used.");
                switch (answer)
                {
                    case AmazonAnswer.Page:
                        _cookies = cookies;
                        _lastPassAt = _time.GetUtcNow();
                        _check = null;
                        _breaker.Succeeded();
                        return new(HandOverOutcome.Passed, "Amazon accepted it.");
                    case AmazonAnswer.RobotCheck when AmazonRobotCheckPage.TryParse(body, url) is { } again
                                                      && check.WrongAnswers + 1 < MaxWrongAnswers:
                        check.WrongAnswers++;
                        check.Form = again;
                        check.PictureFetch = null;
                        Merge(check.Cookies, cookiesSet);
                        return new(HandOverOutcome.WrongAnswer, "Amazon did not accept that. Try again.");
                    default:
                        _check = null;
                        var paused = Pause(answer == AmazonAnswer.RobotCheck
                            ? "its robot check was not passed after the answers a person gave"
                            : "it did not accept a person's answer to its robot check");
                        return new(HandOverOutcome.Closed, paused.Message);
                }
            }
        }
        catch (SiteException ex)
        {
            // The answer never reached Amazon (the queue, a cap, the network): the check is still open.
            return new(HandOverOutcome.NotSent, ex.Message);
        }
        finally
        {
            lock (_checkLock) check.Answering = false;
        }
    }

    /// <summary>
    /// Where Amazon sends a visitor who passed: back to a page a reader may open. A redirect to
    /// another check, to sign-in or anywhere off www.amazon.com is not a pass.
    /// </summary>
    private static bool IsPassTarget(Uri target) =>
        target.Scheme == Uri.UriSchemeHttps && target.IsDefaultPort && target.UserInfo.Length == 0
        && target.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
        && (target.AbsolutePath is "/" or "/s" || AmazonPageClassifier.IsProductPath(target.AbsolutePath));

    /// <summary>Applies the cookies an answer set: a new value replaces the old, and one Amazon cleared (a null) is dropped.</summary>
    private static Dictionary<string, string> Merge(Dictionary<string, string> cookies, IReadOnlyDictionary<string, string?> set)
    {
        foreach (var (name, value) in set)
        {
            if (value is null) cookies.Remove(name);
            else cookies[name] = value;
        }
        return cookies;
    }

    /// <summary>After a pass, the session's cookies follow what Amazon sets on later pages, as a browser's would.</summary>
    private void KeepSessionCookies(IReadOnlyDictionary<string, string?> set)
    {
        if (set.Count == 0) return;
        lock (_checkLock)
        {
            if (_cookies.Count > 0) Merge(_cookies, set);
        }
    }

    /// <summary>The cookies to send with a request to www.amazon.com: none until a person has answered a check.</summary>
    private Dictionary<string, string> SessionCookies()
    {
        lock (_checkLock) return new Dictionary<string, string>(_cookies);
    }
}
