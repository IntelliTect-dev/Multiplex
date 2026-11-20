using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Multiplex.Web.Identity;

/// <summary>
/// What a Microsoft ID token (v2.0, the <c>common</c> endpoint: personal and work or school accounts)
/// says about the person, and the checks Microsoft documents for a multi-tenant app.
/// </summary>
public static class MicrosoftAccounts
{
    public const string Provider = "microsoft";

    /// <summary>Personal and work or school accounts from any tenant.</summary>
    public const string Authority = "https://login.microsoftonline.com/common/v2.0";

    private const string IssuerPrefix = "https://login.microsoftonline.com/";

    /// <summary>
    /// The person in provider-neutral terms, or null when the token lacks the tenant or object id.
    /// The account key is tenant ID plus object ID, the pair Microsoft names as immutable. The email
    /// counts as verified only when the token's <c>xms_edov</c> claim (email domain owner verified,
    /// an optional claim the app registration must request) is true; otherwise it is not trusted.
    /// </summary>
    public static ExternalIdentity? From(ClaimsPrincipal principal)
    {
        if (!Guid.TryParse(principal.FindFirst("tid")?.Value, out var tenant)
            || !Guid.TryParse(principal.FindFirst("oid")?.Value, out var objectId))
        {
            return null;
        }

        var verified = principal.FindFirst("xms_edov")?.Value is { } flag
            && (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1");
        return new ExternalIdentity(Provider, $"{tenant:D}/{objectId:D}", principal.FindFirst("email")?.Value, verified);
    }

    /// <summary>
    /// Microsoft's rule for tokens from the <c>common</c> endpoint: the <c>tid</c> claim is a GUID and the
    /// issuer is exactly <c>https://login.microsoftonline.com/{tid}/v2.0</c>.
    /// </summary>
    public static string ValidateIssuer(string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        var tenant = token switch
        {
            JsonWebToken jwt when jwt.TryGetPayloadValue<string>("tid", out var tid) => tid,
            System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwt => jwt.Payload.TryGetValue("tid", out var tid) ? tid as string : null,
            _ => null,
        };
        if (Guid.TryParse(tenant, out _) && string.Equals(issuer, IssuerPrefix + tenant + "/v2.0", StringComparison.Ordinal))
        {
            return issuer;
        }
        throw new SecurityTokenInvalidIssuerException("The token's issuer does not match its tenant.") { InvalidIssuer = issuer };
    }
}
