using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Multiplex.Sites.GoogleFlights.Wire;
using Polly;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Multiplex.Sites.GoogleFlights.Http;

/// <summary>
/// Sends Google Flights RPCs over direct HTTP and turns each answer into frames or the right exception.
/// Retries, back-off, pacing and timeouts come from the shared resilience package
/// (Microsoft.Extensions.Http.Resilience); refusals go to the <see cref="RefusalGate"/> and are never retried.
/// </summary>
internal sealed partial class GoogleFlightsTransport : IDisposable
{
    private const string ServiceUrl =
        "https://www.google.com/_/FlightsFrontendUi/data/travel.frontend.flights.FlightsFrontendService/{0}?hl=en-US&soc-app=162&soc-platform=1&soc-device=1&rt=c";

    private const string BatchUrl =
        "https://www.google.com/_/FlightsFrontendUi/data/batchexecute?rpcids={0}&source-path=%2Ftravel%2Fflights&hl=en-US&soc-app=162&soc-platform=1&soc-device=1&rt=c";

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

    public Task<RpcFrames> ServiceAsync(string rpc, string inner, string jspb, CancellationToken ct) =>
        SendAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, ServiceUrl, rpc), RequestEncoder.ServiceBody(inner), jspb, ct);

    public Task<RpcFrames> BatchAsync(string rpcId, string inner, string jspb, CancellationToken ct) =>
        SendAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, BatchUrl, rpcId), RequestEncoder.BatchBody(rpcId, inner), jspb, ct);

    /// <summary>Sends a GET request to a Google Flights search page URL and extracts the framed flight data from the HTML.</summary>
    public async Task<RpcFrames> SearchPageAsync(Uri pageUrl, CancellationToken ct)
    {
        _gate.ThrowIfCoolingDown();

        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", GoogleFlightsClientOptions.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

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
            return ClassifyPage(response, text);
        }
    }

    private async Task<RpcFrames> SendAsync(string url, string body, string jspb, CancellationToken ct)
    {
        _gate.ThrowIfCoolingDown();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        // Exactly the header Google answered; ByteArrayContent sets none of its own.
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("User-Agent", GoogleFlightsClientOptions.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.TryAddWithoutValidation("x-same-domain", "1");
        request.Headers.TryAddWithoutValidation("x-goog-ext-259736195-jspb", jspb);

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
            return Classify(response, text);
        }
    }

    private RpcFrames Classify(HttpResponseMessage response, string text)
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
        CheckStatus(response);

        if (ResponseFrames.TryParse(text, out var frames))
        {
            CheckRpcErrors(frames);
            _gate.Answered();
            return frames;
        }

        if (text.Contains("/sorry/", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))
        {
            throw Challenge();
        }

        var match = Ds1CallbackRegex().Match(text);
        if (!match.Success)
        {
            throw new GoogleFlightsFormatException("Google Flights' search page did not contain the expected flight data (key: 'ds:1').");
        }

        var dataIndex = text.IndexOf("data:", match.Index, StringComparison.Ordinal);
        if (dataIndex < 0)
        {
            throw new GoogleFlightsFormatException("Google Flights' search page did not contain data for key 'ds:1'.");
        }

        var arrayStart = text.IndexOf('[', dataIndex);
        if (arrayStart < 0)
        {
            throw new GoogleFlightsFormatException("Google Flights' search page did not contain a data array for key 'ds:1'.");
        }

        var scriptEnd = text.IndexOf("</script>", arrayStart, StringComparison.Ordinal);
        var searchSlice = scriptEnd >= 0 ? text[arrayStart..scriptEnd] : text[arrayStart..];

        try
        {
            var bytes = Encoding.UTF8.GetBytes(searchSlice);
            var reader = new Utf8JsonReader(bytes);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GoogleFlightsFormatException("Google Flights' search page data for key 'ds:1' was not a JSON array.");
            }

            _gate.Answered();
            return new RpcFrames([doc.RootElement.Clone()], []);
        }
        catch (JsonException e)
        {
            throw new GoogleFlightsFormatException($"Google Flights' search page data for key 'ds:1' was not valid JSON: {e.Message}", e);
        }
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

    [GeneratedRegex(@"AF_initDataCallback\s*\(\s*\{[^}]*?key\s*:\s*['""]ds:1['""]")]
    private static partial Regex Ds1CallbackRegex();

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
