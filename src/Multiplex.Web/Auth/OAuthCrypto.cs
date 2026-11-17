using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Multiplex.Web.Auth;

/// <summary>Random identifiers, token hashing and PKCE.</summary>
internal static class OAuthCrypto
{
    /// <summary>An opaque token or code: 32 random bytes, base64url (43 characters).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A non-secret identifier (client id, pending id, grant id): 16 random bytes, base64url.</summary>
    public static string NewId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The key a token or code is stored under: base64url SHA-256 of its UTF-8 bytes.</summary>
    public static string Hash(string token) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>RFC 7636: 43-128 characters from the unreserved set.</summary>
    public static bool IsValidPkceString(string? value) =>
        value is { Length: >= 43 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    /// <summary>True when BASE64URL(SHA256(verifier)) equals the challenge, compared in constant time.</summary>
    public static bool VerifyS256(string verifier, string challenge)
    {
        var computed = Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        return CryptographicOperations.FixedTimeEquals(computed, Encoding.ASCII.GetBytes(challenge));
    }
}
