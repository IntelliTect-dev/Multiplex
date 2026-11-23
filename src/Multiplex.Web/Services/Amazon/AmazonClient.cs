using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using Multiplex.Web.HtmlSites;

namespace Multiplex.Web.Services.Amazon;

/// <summary>The Amazon service's own <see cref="SiteGate"/>: one for the whole host.</summary>
public sealed record AmazonGate(SiteGate Gate);

/// <summary>A product's main picture, as fetched.</summary>
public sealed record AmazonImage(byte[] Data, string MimeType);

/// <summary>
/// Reads Amazon's pages over plain HTTPS, as itself: an honest user agent, signed out, one request
/// at a time through the <see cref="SiteGate"/>, robots.txt obeyed, and a stop at any block.
/// One instance serves the whole host; what it remembers (the pause, robots.txt, ten minutes of
/// parsed pages) belongs to the process, never to an MCP session.
/// </summary>
public sealed partial class AmazonClient
{
    public const string HttpClientName = "amazon";
    public const string Host = AmazonPageClassifier.Host;
    /// <summary>The names this service answers to in robots.txt.</summary>
    public static readonly string[] RobotsNames = ["Multiplex-Amazon", "Multiplex"];
    /// <summary>The only hosts a picture is ever fetched from.</summary>
    public static readonly string[] ImageHosts = ["m.media-amazon.com", "images-na.ssl-images-amazon.com"];

    public static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(50);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    public const int CacheSize = 200;
    public static readonly TimeSpan RobotsLifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan FirstPause = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LongestPause = TimeSpan.FromHours(2);
    public static readonly TimeSpan LongestRetryAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryWithoutRetryAfter = TimeSpan.FromSeconds(5);
    public const int MaxImageBytes = 2 * 1024 * 1024;

    private static readonly string UserAgent =
        $"Multiplex-Amazon/{typeof(AmazonClient).Assembly.GetName().Version?.ToString(2) ?? "1.0"}";

    private readonly IHttpClientFactory _http;
    private readonly SiteGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<AmazonClient> _log;
    private readonly CircuitBreaker _breaker;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset At, object Value)> _cache = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RobotsRules Rules)> _robots = new();
    private long _sequence;

    public AmazonClient(IHttpClientFactory http, AmazonGate gate, TimeProvider time, ILogger<AmazonClient> log)
    {
        _http = http;
        _gate = gate.Gate;
        _time = time;
        _log = log;
        _breaker = new CircuitBreaker(time, FirstPause, LongestPause);
    }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex AsinPattern();

    [GeneratedRegex("/(?:dp|gp/product|clp)/([A-Za-z0-9]{10})(?:[/?]|$)")]
    private static partial Regex AsinInPath();

    /// <summary>The ASIN in <paramref name="product"/>: an ASIN itself, or an Amazon product link.</summary>
    /// <exception cref="McpException">It is neither; no request is made.</exception>
    public static string ParseAsin(string? product)
    {
        var text = product?.Trim() ?? "";
        if (AsinPattern().IsMatch(text.ToUpperInvariant())) return text.ToUpperInvariant();
        if (Uri.TryCreate(text, UriKind.Absolute, out var url)
            && url.Scheme is "https" or "http"
            && (url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || url.Host.Equals("amazon.com", StringComparison.OrdinalIgnoreCase))
            && AsinInPath().Match(url.AbsolutePath) is { Success: true } match)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }
        throw new McpException(
            $"'{Shorten(text)}' is not an Amazon product. Give a 10-character ASIN such as B088NRLMPV, or a www.amazon.com product link.");
    }

    private static string Shorten(string text) => text.Length > 60 ? text[..60] + "..." : text;

    // ---- search ------------------------------------------------------------------------------

    public async Task<AmazonSearchAnswer> SearchAsync(AmazonSearchRequest request, CancellationToken ct)
    {
        var plan = AmazonSearch.Plan(request);
        var deadline = _time.GetUtcNow() + CallDeadline;
        var sequence = $"search-{Interlocked.Increment(ref _sequence)}";

        var read = new List<(int Page, DateTimeOffset At, AmazonSearchPageData Data)>();
        var warnings = new List<string>();
        AmazonHandOver? handOver = null;
        int? nextPage = null;
        foreach (var page in plan.Pages)
        {
            try
            {
                if (read.Count > 0 && _time.GetUtcNow() >= deadline)
                    throw new SiteBusyException("The call ran out of time before Amazon could be asked again.");
                var (at, data) = await CachedAsync(plan.PageUrl(page).AbsoluteUri,
                    token => FetchSearchPageAsync(plan.PageUrl(page), page, sequence, deadline, token), ct).ConfigureAwait(false);
                read.Add((page, at, data));
                nextPage = data.HasNextPage && page < AmazonSearchPlan.LastPage ? page + 1 : null;
                if (!data.HasNextPage) break;
            }
            catch (SiteException ex) when (read.Count > 0)
            {
                // The pages already read are still an answer; say where it stopped and why.
                warnings.Add($"Page {page} was not read: {ex.Message}");
                if (ex is SiteHandOverException check) handOver = new AmazonHandOver(check.Url.AbsoluteUri, check.ExpiresAt);
                nextPage = page;
                break;
            }
        }

        var outcome = AmazonSearch.Combine(plan, read.Select(r => r.Data));
        warnings.AddRange(read.SelectMany(r => r.Data.Warnings).Distinct());
        return new AmazonSearchAnswer(
            plan.Request.Query,
            plan.Request.Sort,
            plan.Minimums,
            outcome.RatingBaseline,
            "USD",
            read.Select(r => r.Page).ToList(),
            nextPage,
            read[0].Data.TotalResults,
            outcome.SponsoredRemoved,
            outcome.FilteredOut,
            outcome.Results,
            outcome.Note,
            read[0].Data.Filters,
            warnings,
            handOver,
            read.Select(r => plan.PageUrl(r.Page).AbsoluteUri).ToList(),
            read.Min(r => r.At));
    }

    private async Task<AmazonSearchPageData> FetchSearchPageAsync(Uri url, int page, string sequence, DateTimeOffset deadline, CancellationToken ct)
    {
        var body = await FetchPageAsync(url, $"search page {page}", sequence, deadline, ct).ConfigureAwait(false);
        return AmazonSearchPage.Parse(body);
    }

    // ---- product -----------------------------------------------------------------------------

    public async Task<(AmazonProduct Product, AmazonImage? Image)> GetProductAsync(string product, CancellationToken ct)
    {
        var asin = ParseAsin(product);
        var deadline = _time.GetUtcNow() + CallDeadline;
        var url = new Uri($"https://{Host}/dp/{asin}");
        var (_, value) = await CachedAsync(url.AbsoluteUri, async token =>
        {
            string body;
            try
            {
                body = await FetchPageAsync(url, $"product {asin}", sequence: null, deadline, token).ConfigureAwait(false);
            }
            catch (SiteNotFoundException)
            {
                throw new SiteNotFoundException($"Amazon has no product with the ASIN {asin}.");
            }
            var parsed = AmazonProductPage.Parse(body, _time.GetUtcNow());
            var (image, warning) = await FetchMainImageAsync(parsed.MainImageUrl, deadline, token).ConfigureAwait(false);
            if (warning is not null) parsed = parsed with { Warnings = [.. parsed.Warnings, warning] };
            return (parsed, image);
        }, ct).ConfigureAwait(false);
        return value;
    }

    private async Task<(AmazonImage? Image, string? Warning)> FetchMainImageAsync(string? address, DateTimeOffset deadline, CancellationToken ct)
    {
        if (address is null) return (null, null);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !ImageHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
        {
            _log.LogWarning("Amazon named a product picture on a host that is not one of its image hosts; it was not fetched.");
            return (null, "The main image was not fetched: its address is not on one of Amazon's image hosts.");
        }
        try
        {
            await EnsureRobotsAsync(url, deadline, ct).ConfigureAwait(false);
            ThrowIfDisallowed(url);
            using var response = await _gate.RunAsync(SiteRequestKind.Image, null, deadline,
                () => { ThrowIfPaused(); ThrowIfDisallowed(url); },
                token => SendAsync(url, "image/*", token), ct).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode != HttpStatusCode.OK || type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Amazon image fetch answered {Status} {Type}; the product is returned without it.", (int)response.StatusCode, type);
                return (null, "The main image could not be fetched; the image links are in images.");
            }
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return data.Length is 0 or > MaxImageBytes
                ? (null, "The main image was too large to include; the image links are in images.")
                : (new AmazonImage(data, type), null);
        }
        catch (Exception ex) when (ex is SiteException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogInformation("Amazon image fetch failed ({Reason}); the product is returned without it.", ex.GetType().Name);
            return (null, "The main image could not be fetched; the image links are in images.");
        }
    }

    // ---- the request pipeline ----------------------------------------------------------------

    /// <summary>
    /// Fetches one page and returns its body once the classifier calls it a page. Anything else
    /// becomes a <see cref="SiteException"/>; a block also pauses the service.
    /// </summary>
    private async Task<string> FetchPageAsync(Uri url, string what, string? sequence, DateTimeOffset deadline, CancellationToken ct)
    {
        var retried = false;
        var redirected = false;
        while (true)
        {
            RequireAmazonHttps(url);
            ThrowIfPaused();
            await EnsureRobotsAsync(url, deadline, ct).ConfigureAwait(false);
            ThrowIfDisallowed(url);

            var started = Stopwatch.GetTimestamp();
            var target = url;
            var (status, headers, location, retryAfter, body) = await _gate.RunAsync(SiteRequestKind.Page, sequence, deadline,
                () => { ThrowIfPaused(); ThrowIfDisallowed(target); },
                async token =>
                {
                    using var response = await SendAsync(target, "text/html", token).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    return (response.StatusCode, response.Headers.Select(h => h.Key).ToList(), response.Headers.Location,
                        RetryAfter(response), text);
                }, ct).ConfigureAwait(false);

            var answer = AmazonPageClassifier.Classify(status, headers, url, location, body);
            _log.LogInformation("Amazon {What}: {Status} {Answer} in {Elapsed} ms.", what, (int)status, answer,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            switch (answer)
            {
                case AmazonAnswer.Page:
                    _breaker.Succeeded();
                    return body;
                case AmazonAnswer.NotFound:
                    throw new SiteNotFoundException("Amazon has no such page.");
                case AmazonAnswer.Redirect when !redirected:
                    redirected = true;
                    url = AmazonPageClassifier.Resolve(url, location!);
                    continue;
                case AmazonAnswer.Redirect:
                    throw new SiteUnavailableException("Amazon redirected the request twice; it was not followed further.");
                case AmazonAnswer.Throttled when retryAfter is null:
                    throw Pause("it said there were too many requests, without saying how long to wait");
                case AmazonAnswer.Throttled or AmazonAnswer.Unavailable:
                    var wait = retryAfter ?? RetryWithoutRetryAfter;
                    if (wait > LongestRetryAfter)
                    {
                        throw new SiteUnavailableException(
                            $"Amazon asked for a wait until {_time.GetUtcNow() + wait:HH:mm} UTC; try again then.");
                    }
                    if (retried || _time.GetUtcNow() + wait >= deadline)
                    {
                        throw new SiteUnavailableException(answer == AmazonAnswer.Throttled
                            ? "Amazon said there were too many requests; try again in a few minutes."
                            : $"Amazon answered with an error ({(int)status}); try again in a few minutes.");
                    }
                    retried = true;
                    await _gate.DelayAsync(wait, ct).ConfigureAwait(false);
                    continue;
                default:
                    throw Pause(answer switch
                    {
                        AmazonAnswer.RobotCheck => "it showed a robot check",
                        AmazonAnswer.AutomatedAccessRefused => "it said automated access needs its permission",
                        AmazonAnswer.Challenge => "it asked for a check that only a browser can pass",
                        AmazonAnswer.SignInWall => "it asked for a sign-in",
                        _ => "it redirected or refused the request",
                    });
            }
        }
    }

    private SiteBlockedException Pause(string reason)
    {
        var until = _breaker.Trip(reason);
        _log.LogWarning("Amazon requests are paused until {Until:HH:mm} UTC: {Reason}.", until, reason);
        return new SiteBlockedException(PausedMessage(_breaker.Pause!.Value.Reason, until));
    }

    private void ThrowIfPaused()
    {
        if (_breaker.Pause is { } pause) throw new SiteBlockedException(PausedMessage(pause.Reason, pause.Until));
    }

    private static string PausedMessage(string reason, DateTimeOffset until) =>
        $"Amazon refused this kind of request ({reason}). Amazon requests are paused until {until:HH:mm} UTC.";

    private static void RequireAmazonHttps(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
            throw new SiteBlockedException("Only https://www.amazon.com pages are read.");
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        return await _http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
    }

    // ---- robots.txt --------------------------------------------------------------------------

    /// <summary>
    /// Makes sure this host's robots.txt is in memory and under a day old, fetching it (in a turn
    /// of its own) when it is not. Callers check paths against the copy in memory afterwards, so
    /// nothing fetches while holding the gate.
    /// </summary>
    private async Task EnsureRobotsAsync(Uri url, DateTimeOffset deadline, CancellationToken ct)
    {
        var host = url.Host.ToLowerInvariant();
        if (_robots.TryGetValue(host, out var held) && _time.GetUtcNow() - held.At < RobotsLifetime) return;

        var shared = _inFlight.GetOrAdd("robots:" + host, key => new Lazy<Task<object>>(async () =>
        {
            try
            {
                return await FetchRobotsAsync(host, deadline).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }));
        await shared.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<object> FetchRobotsAsync(string host, DateTimeOffset deadline)
    {
        var url = new Uri($"https://{host}/robots.txt");
        var hadCopy = _robots.TryGetValue(host, out var held);
        try
        {
            var (status, headers, location, body) = await _gate.RunAsync(SiteRequestKind.Page, null, deadline, ThrowIfPaused,
                async token =>
                {
                    using var response = await SendAsync(url, "text/plain", token).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    return (response.StatusCode, response.Headers.Select(h => h.Key).ToList(), response.Headers.Location, text);
                }, CancellationToken.None).ConfigureAwait(false);

            var answer = AmazonPageClassifier.Classify(status, headers, url, location, body);
            // A web page where the rules should be is no robots.txt; reading it as "no rules" would allow everything.
            if (answer == AmazonAnswer.Page && LooksLikeHtml(body)) answer = AmazonAnswer.Unavailable;
            _log.LogInformation("Amazon robots.txt on {Host}: {Status} {Answer}.", host, (int)status, answer);
            switch (answer)
            {
                case AmazonAnswer.Page:
                    _robots[host] = (_time.GetUtcNow(), RobotsRules.Parse(body));
                    break;
                case AmazonAnswer.NotFound:
                    _robots[host] = (_time.GetUtcNow(), RobotsRules.AllowAll);
                    break;
                case AmazonAnswer.Throttled or AmazonAnswer.Unavailable:
                    if (!hadCopy) throw new SiteUnavailableException("Amazon's robots.txt could not be read, so nothing was requested; try again in a few minutes.");
                    break;
                default:
                    throw Pause("it refused the request for its robots.txt");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (!hadCopy) throw new SiteUnavailableException("Amazon's robots.txt could not be read, so nothing was requested; try again in a few minutes.");
        }
        return held;
    }

    private static bool LooksLikeHtml(string body) =>
        body.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);

    /// <summary>Checks <paramref name="url"/> against the robots.txt in memory; it never fetches.</summary>
    private void ThrowIfDisallowed(Uri url)
    {
        if (!_robots.TryGetValue(url.Host.ToLowerInvariant(), out var held))
            throw new SiteBusyException("Amazon's robots.txt has not been read yet; try again.");
        if (!held.Rules.IsAllowed(url.PathAndQuery, RobotsNames))
        {
            var path = url.AbsolutePath.Length > 40 ? url.AbsolutePath[..40] : url.AbsolutePath;
            throw new SiteBusyException($"Amazon's robots.txt does not allow this service to read {path} on {url.Host}.");
        }
    }

    // ---- cache -------------------------------------------------------------------------------

    /// <summary>
    /// The parsed answer for <paramref name="key"/>: from the last ten minutes if there is one,
    /// else fetched once however many callers ask at the same moment.
    /// </summary>
    private async Task<(DateTimeOffset At, T Value)> CachedAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : notnull
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && _time.GetUtcNow() - hit.At < CacheLifetime) return (hit.At, (T)hit.Value);
        }
        var shared = _inFlight.GetOrAdd(key, k => new Lazy<Task<object>>(async () =>
        {
            try
            {
                // Not the first caller's token: the callers sharing this fetch must not lose it when that one leaves.
                var value = await fetch(CancellationToken.None).ConfigureAwait(false);
                var entry = (At: _time.GetUtcNow(), Value: (object)value);
                lock (_cacheLock)
                {
                    _cache[k] = entry;
                    while (_cache.Count > CacheSize) _cache.Remove(_cache.MinBy(e => e.Value.At).Key);
                }
                return entry;
            }
            finally
            {
                _inFlight.TryRemove(k, out _);
            }
        }));
        var (at, boxed) = ((DateTimeOffset, object))await shared.Value.WaitAsync(ct).ConfigureAwait(false);
        return (at, (T)boxed);
    }
}
