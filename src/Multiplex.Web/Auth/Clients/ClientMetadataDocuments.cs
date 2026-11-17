using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Multiplex.Web.Auth.Clients;

/// <summary>
/// Fetches OAuth client ID metadata documents (an HTTPS URL used as <c>client_id</c>, e.g. ChatGPT's
/// <c>https://chatgpt.com/oauth/client.json</c>). Bounded: 5 s for the whole fetch including redirects,
/// 64 KB, HTTPS only (redirects too), cached 10 minutes (failures 1 minute).
/// </summary>
public sealed partial class ClientMetadataDocuments(
    IHttpClientFactory httpClients,
    TimeProvider time,
    ILogger<ClientMetadataDocuments> logger)
{
    public const string HttpClientName = "Multiplex.Auth.ClientMetadata";

    internal const int MaxBytes = 64 * 1024;
    private const int MaxRedirects = 3;
    private const int MaxCacheEntries = 1000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (ClientLookup Result, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    /// <summary>A URL that may be used as a client id: HTTPS with a path, no fragment, user info or dot segments.</summary>
    public static bool IsDocumentUrl(string clientId) =>
        clientId.StartsWith("https://", StringComparison.Ordinal)
        && Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.Length > 1
        && uri.UserInfo.Length == 0
        && !clientId.Contains('#')
        && !uri.OriginalString.Split('?')[0].Split('/').Skip(3).Any(s => s is "." or "..");

    public async Task<ClientLookup> GetAsync(string clientId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(clientId, out var cached) && cached.Expires > now) return cached.Result;

        var result = await FetchAsync(clientId, ct);
        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (var stale in _cache.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _cache.TryRemove(stale, out _);
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        }
        _cache[clientId] = (result, now + (result.Client is null ? FailureLifetime : SuccessLifetime));
        return result;
    }

    private async Task<ClientLookup> FetchAsync(string clientId, CancellationToken ct)
    {
        if (!IsDocumentUrl(clientId)) return ClientLookup.Fail("The client id is not a valid HTTPS client metadata document URL.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var http = httpClients.CreateClient(HttpClientName);

        try
        {
            var url = new Uri(clientId);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null) return Failed(clientId, "redirected without a Location");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (url.Scheme != Uri.UriSchemeHttps) return Failed(clientId, "redirected to a non-HTTPS URL");
                    continue;
                }

                if (!response.IsSuccessStatusCode) return Failed(clientId, $"returned HTTP {(int)response.StatusCode}");
                if (response.Content.Headers.ContentLength > MaxBytes) return Failed(clientId, "is larger than 64 KB");

                var body = await ReadBoundedAsync(response.Content, timeout.Token);
                return body is null ? Failed(clientId, "is larger than 64 KB") : Parse(clientId, body);
            }
            return Failed(clientId, "redirected too many times");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(clientId, "timed out");
        }
        catch (HttpRequestException ex)
        {
            return Failed(clientId, $"could not be fetched ({ex.HttpRequestError})");
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private ClientLookup Parse(string clientId, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failed(clientId, "is not a JSON object");

            if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), clientId, StringComparison.Ordinal))
            {
                return Failed(clientId, "has a client_id that differs from its URL");
            }

            if (!root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
            {
                return Failed(clientId, "has no redirect_uris");
            }
            var redirectUris = uris.EnumerateArray()
                .Where(u => u.ValueKind == JsonValueKind.String)
                .Select(u => u.GetString()!)
                .Where(RedirectUris.IsAllowed)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (redirectUris.Count == 0) return Failed(clientId, "has no HTTPS or loopback redirect_uris");

            var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return ClientLookup.Found(new OAuthClient(
                clientId, OAuthClient.DisplayNameOr(name, new Uri(clientId).Host), redirectUris, IsMetadataDocument: true));
        }
        catch (JsonException)
        {
            return Failed(clientId, "is not valid JSON");
        }
    }

    private ClientLookup Failed(string clientId, string reason)
    {
        LogFetchFailed(logger, clientId, reason);
        return ClientLookup.Fail($"The client metadata document {reason}.");
    }

    /// <summary>
    /// Production connect callback: the document's host must resolve to a public address, so a client id
    /// cannot make this server probe its own network (loopback, private, link-local, CGNAT/tailnet).
    /// </summary>
    internal static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var address = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException(HttpRequestError.ConnectionError, "The host does not resolve to a public address.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or >= 224
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client metadata document {ClientId} {Reason}.")]
    private static partial void LogFetchFailed(ILogger logger, string clientId, string reason);
}
