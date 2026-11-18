using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;

namespace Multiplex.Web.Services.TripIt;

/// <summary>
/// Single-use confirmation for risky tools. A call without a token changes nothing and gets a
/// preview plus a token; the token is a Data Protection time-limited payload (5 minutes)
/// carrying a random id and binding the account, the tool name and a SHA-256 of the canonical
/// JSON of the other arguments. Only a call with a valid token for exactly the same request
/// performs it, and only once: the ids of used tokens are remembered in memory, per account,
/// until the tokens expire.
/// </summary>
/// <remarks>
/// The record of used ids lives in this process only: after a server restart a token issued
/// before it could be used once more within its 5 minutes.
/// </remarks>
public sealed class ConfirmTokens(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Multiplex.Services.TripIt.ConfirmToken.v2").ToTimeLimitedDataProtector();

    /// <summary>Used token ids per account, each with its token's expiry.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTimeOffset>> _used =
        new(StringComparer.Ordinal);

    /// <summary>Issues a token for this account, tool and argument set.</summary>
    public string Issue(string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return _protector.Protect($"{id}.{Binding(accountId, tool, arguments)}", time.GetUtcNow() + Lifetime);
    }

    /// <summary>
    /// Uses up <paramref name="token"/>: throws a tool error unless it was issued for exactly
    /// this request, has not expired and has not been used before. A token refused for another
    /// request is not used up.
    /// </summary>
    public void Redeem(string token, string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
    {
        string payload;
        DateTimeOffset expires;
        try
        {
            payload = _protector.Unprotect(token, out expires);
        }
        catch (CryptographicException)
        {
            throw new McpException(
                $"The confirm_token is invalid or has expired (tokens last {Lifetime.TotalMinutes:0} minutes). Nothing was changed. " +
                $"Call {tool} again without confirm_token to get a fresh preview, show it to the person and ask them again.");
        }
        var dot = payload.IndexOf('.', StringComparison.Ordinal);
        var id = dot > 0 ? payload[..dot] : "";
        var binding = dot > 0 ? payload[(dot + 1)..] : payload;
        if (id.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(binding), Encoding.UTF8.GetBytes(Binding(accountId, tool, arguments))))
        {
            throw new McpException(
                "This confirm_token was issued for a different request (other arguments, another tool or another account). " +
                $"Nothing was changed. Call {tool} again without confirm_token to get a preview of this exact request, " +
                "show it to the person and ask them again.");
        }

        var used = _used.GetOrAdd(accountId, static _ => new(StringComparer.Ordinal));
        var now = time.GetUtcNow();
        foreach (var (usedId, usedExpiry) in used)
        {
            if (usedExpiry <= now) used.TryRemove(usedId, out _);
        }
        if (!used.TryAdd(id, expires))
        {
            throw new McpException(
                "This confirm_token has already been used; each one allows the change once. Nothing was changed. " +
                $"If the person wants it done again, call {tool} again without confirm_token for a fresh preview, " +
                "show it to them and ask them again.");
        }
    }

    /// <summary>What a token binds: account, tool and the argument hash.</summary>
    private static string Binding(string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("account", accountId);
            w.WriteString("tool", tool);
            w.WriteString("arguments", HashArguments(arguments));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// SHA-256 (hex) of the arguments as canonical JSON: keys in ordinal order, values after the
    /// tool's defaults were applied (so omitting a default and passing it hash the same), null as JSON null.
    /// </summary>
    public static string HashArguments(IReadOnlyDictionary<string, string?> arguments)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var (key, value) in arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (value is null) w.WriteNull(key);
                else w.WriteString(key, value);
            }
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
