using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Multiplex.Sites.GoogleFlights.Wire;
using Polly;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Multiplex.Sites.GoogleFlights.Http;

/// <summary>
/// Fetches Google Flights search pages, and sends the airport lookup RPC, over direct HTTPS, and turns each answer
/// into frames or the right exception.
/// Retries, back-off, pacing and timeouts come from the shared resilience package
/// (Microsoft.Extensions.Http.Resilience); refusals go to the <see cref="RefusalGate"/> and are never retried.
/// </summary>
internal sealed class GoogleFlightsTransport : IDisposable
{

    private const string BatchUrl =
        "https://www.google.com/_/FlightsFrontendUi/data/batchexecute?rpcids={0}&source-path=%2Ftravel%2Fflights&hl=en-US&soc-app=162&soc-platform=1&soc-device=1&rt=c";

    /// <summary>Where a relative redirect is resolved against.</summary>
    private static readonly Uri Google = new("https://www.google.com/");

    private readonly HttpClient _http;
    private readonly RefusalGate _gate;
    private readonly TimeProvider _time;
    private readonly RateLimiter? _ownedLimiter;
    private readonly TimeSpan _interval;

    public GoogleFlightsTransport(GoogleFlightsClientOptions options, TimeProvider time, HttpMessageHandler? transport)
    {
        _time = time;
        _interval = options.RequestInterval;
        _gate = new RefusalGate(options.RefusalCooldown, options.MaxRefusalCooldown, time);

        var limiter = options.RateLimiter;
        if (limiter is null)
        {
            limiter = _ownedLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, options.BurstRequests),
                TokensPerPeriod = 1,
                ReplenishmentPeriod = options.RequestInterval,
                QueueLimit = Math.Max(0, options.MaxWaitingRequests),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
        }

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = time }
            .AddTimeout(options.TotalTimeout);
        if (options.MaxRetries > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.RetryDelay,
                // 429 is Google throttling: handled as a refusal, never retried.
                ShouldRetryAfterHeader = false,
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
            });
        }
        var pipeline = builder
            .AddRateLimiter(limiter)
            .AddTimeout(options.AttemptTimeout)
            .Build();

        // Pacing waits happen inside the pipeline, so the cool-down is checked again per attempt, after the wait.
        var gated = new RefusalGateHandler(_gate) { InnerHandler = transport ?? CreateNetworkHandler() };
        _http = new HttpClient(new ResilienceHandler(pipeline) { InnerHandler = gated })
        {
            Timeout = Timeout.InfiniteTimeSpan, // the pipeline's timeouts apply
        };
    }

    /// <summary>The handler that reaches Google over the network.</summary>
    internal static SocketsHttpHandler CreateNetworkHandler() => new()
    {
        // A redirect is how Google says "unusual traffic": the client must see it, never follow it.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        // NID, which Google sets on its first answer, is sent back as any HTTP client would. Nothing else.
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Fetches a Google Flights search page and returns the first answer its HTML carries (data block <c>ds:1</c>), as
    /// one payload laid out like a <c>GetShoppingResults</c> answer (protocol.md section 5).
    /// </summary>
    public Task<RpcFrames> SearchPageAsync(Uri pageUrl, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", GoogleFlightsClientOptions.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        return SendAsync(request, ClassifyPage, ct);
    }

    /// <summary>A batchexecute RPC (the airport lookup): answered without the page's token (protocol.md section 2).</summary>
    public Task<RpcFrames> BatchAsync(string rpcId, string inner, string jspb, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, string.Format(System.Globalization.CultureInfo.InvariantCulture, BatchUrl, rpcId));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(RequestEncoder.BatchBody(rpcId, inner)));
        // Exactly the header Google answered; ByteArrayContent sets none of its own.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("User-Agent", GoogleFlightsClientOptions.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("x-same-domain", "1");
        request.Headers.TryAddWithoutValidation("x-goog-ext-259736195-jspb", jspb);
        return SendAsync(request, ClassifyRpc, ct);
    }

    private async Task<RpcFrames> SendAsync(HttpRequestMessage request, Func<HttpResponseMessage, string, RpcFrames> classify, CancellationToken ct)
    {
        using (request)
        {
            _gate.ThrowIfCoolingDown();

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (RateLimiterRejectedException e)
            {
                throw new GoogleFlightsThrottledException(
                    "Too many Google Flights requests are already waiting their turn; the client paces itself to avoid a block. Try again shortly.",
                    ThrottleReason.Paced, _time.GetUtcNow() + (e.RetryAfter ?? _interval));
            }
            catch (TimeoutRejectedException e)
            {
                throw new GoogleFlightsTransientException("Google Flights did not answer in time.", e);
            }
            catch (HttpRequestException e)
            {
                throw new GoogleFlightsTransientException($"Could not reach Google Flights: {e.Message}", e);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return classify(response, text);
            }
        }
    }

    private RpcFrames ClassifyRpc(HttpResponseMessage response, string text)
    {
        CheckStatus(response);

        if (!ResponseFrames.TryParse(text, out var frames))
        {
            throw text.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase)
                ? Challenge()
                : new GoogleFlightsFormatException("Google Flights' answer was not in its usual framed format.");
        }

        CheckRpcErrors(frames);

        _gate.Answered();
        return frames;
    }

    private RpcFrames ClassifyPage(HttpResponseMessage response, string text)
    {
        // How Google turns away a page request from a client it does not serve pages to (P5, P9): a refusal, with a
        // cool-down like any other. Read before the status, which would call it an unexpected redirect.
        // Only a redirect TO that page: a /sorry/ check whose own link mentions it is still a check.
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location
            && new Uri(Google, location).AbsolutePath
                .StartsWith("/travel/flights/unsupported", StringComparison.OrdinalIgnoreCase))
        {
            var until = _gate.Refused();
            throw new GoogleFlightsThrottledException(
                $"Google Flights redirected the client to its \"unsupported browser\" page: it is not serving its search pages to this client. The client will not ask again until {until:u}.",
                ThrottleReason.Refused, until);
        }

        CheckStatus(response);

        // The flights decide first: a page that carries them is an answer, whatever its scripts or help texts say.
        if (SearchPageData.Read(text) is { } payload)
        {
            _gate.Answered();
            return new RpcFrames([payload], []);
        }

        if (text.Contains("/sorry/", StringComparison.OrdinalIgnoreCase) || text.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))
            throw Challenge();

        throw new GoogleFlightsFormatException("Google Flights' search page did not carry its flight data (no ds:1 block).");
    }

    private void CheckStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var location = response.Headers.Location?.ToString() ?? "";

        // Every block signal in the status or headers is read before the body is parsed, so a body that fails
        // to parse can never hide one.
        if (location.Contains("/sorry/", StringComparison.OrdinalIgnoreCase))
            throw Challenge();

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var until = _gate.Refused(RetryAfter(response));
            throw new GoogleFlightsThrottledException(
                $"Google Flights is limiting requests from this network (HTTP 429). The client will not ask again until {until:u}.",
                ThrottleReason.TooManyRequests, until);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // A common block page. Treated like any refusal: stop and back off, never retry straight away.
            var until = _gate.Refused();
            throw new GoogleFlightsThrottledException(
                $"Google Flights refused the request (HTTP 403). The client will not ask again until {until:u}, to avoid a block on this network.",
                ThrottleReason.Refused, until);
        }

        if (status is >= 500 and < 600)
            throw new GoogleFlightsTransientException($"Google Flights had a server error (HTTP {status}).");

        if (status != 200)
        {
            throw new GoogleFlightsFormatException(location.Length > 0
                ? $"Google Flights answered HTTP {status}, redirecting to {location}, where an answer was expected."
                : $"Google Flights answered HTTP {status} where an answer was expected.");
        }
    }

    private void CheckRpcErrors(RpcFrames frames)
    {
        if (frames.Errors.Count > 0 && frames.Payloads.Count == 0)
        {
            var code = frames.Errors[0].Code;
            switch (code)
            {
                case 13:
                    var until = _gate.Refused();
                    throw new GoogleFlightsThrottledException(
                        $"Google Flights refused the request (gRPC 13). It does not say why; the client will not ask again until {until:u}, to avoid a block on this network.",
                        ThrottleReason.Refused, until);
                case 3:
                    _gate.Answered();
                    throw new GoogleFlightsRequestException(
                        "Google Flights rejected the request as invalid (gRPC 3): check the airports, dates and filters.");
                case 14:
                    throw new GoogleFlightsTransientException("Google Flights is unavailable just now (gRPC 14).");
                case 9:
                    // Seen from a network Google was refusing and from one it was answering, whose next search was
                    // answered: it says nothing about a block. Not retried here: requests close together came before refusals.
                    throw new GoogleFlightsTransientException("Google Flights could not answer just now (gRPC 9). Try again in a few minutes.");
                default:
                    // A code the client does not know: stop and back off, as for 13. Asking again at once could only
                    // deepen a block.
                    var stopUntil = _gate.Refused();
                    throw new GoogleFlightsThrottledException(
                        $"Google Flights refused the request with an error the client does not know (gRPC {code}); it will not ask again until {stopUntil:u}.",
                        ThrottleReason.Refused, stopUntil);
            }
        }
    }


    private GoogleFlightsThrottledException Challenge() => new(
        "Google Flights is asking for a check that a person must clear in a browser (\"unusual traffic\"). The client never clears it; it has stopped sending requests.",
        ThrottleReason.Challenge, _gate.Refused());

    private DateTimeOffset? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date)
            return date;
        if (retry?.Delta is { } delta)
            return _time.GetUtcNow() + delta;
        return null;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException or TimeoutRejectedException } => true,
        { Result: { } r } => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout,
        _ => false,
    };

    public void Dispose()
    {
        _http.Dispose();
        _ownedLimiter?.Dispose();
    }
}
