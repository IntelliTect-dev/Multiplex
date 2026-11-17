using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;

namespace Multiplex.Web.Servers.TripIt;

/// <summary>
/// Stateless confirmation for risky tools. A call without a token changes nothing and gets a
/// preview plus a token; the token is a Data Protection time-limited payload (5 minutes)
/// binding the account, the tool name and a SHA-256 of the canonical JSON of the other
/// arguments. Only a call with a valid token for exactly the same request performs it.
/// </summary>
public sealed class ConfirmTokens(IDataProtectionProvider dataProtection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Multiplex.Servers.TripIt.ConfirmToken.v1").ToTimeLimitedDataProtector();

    /// <summary>Issues a token for this account, tool and argument set.</summary>
    public string Issue(string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
        => _protector.Protect(Payload(accountId, tool, arguments), time.GetUtcNow() + Lifetime);

    /// <summary>Throws a tool error unless <paramref name="token"/> was issued for exactly this request and has not expired.</summary>
    public void Verify(string token, string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
    {
        string payload;
        try
        {
            payload = _protector.Unprotect(token, out _);
        }
        catch (CryptographicException)
        {
            throw new McpException(
                $"The confirm_token is invalid or has expired (tokens last {Lifetime.TotalMinutes:0} minutes). Nothing was changed. " +
                $"Call {tool} again without confirm_token to get a fresh preview, show it to the person and ask them again.");
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(payload), Encoding.UTF8.GetBytes(Payload(accountId, tool, arguments))))
        {
            throw new McpException(
                "This confirm_token was issued for a different request (other arguments, another tool or another account). " +
                $"Nothing was changed. Call {tool} again without confirm_token to get a preview of this exact request, " +
                "show it to the person and ask them again.");
        }
    }

    /// <summary>The protected payload: account, tool and the argument hash.</summary>
    private static string Payload(string accountId, string tool, IReadOnlyDictionary<string, string?> arguments)
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
