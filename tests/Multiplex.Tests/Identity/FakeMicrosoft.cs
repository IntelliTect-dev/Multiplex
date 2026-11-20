using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Multiplex.Tests.Auth;
using Multiplex.Web.Identity;

namespace Multiplex.Tests.Identity;

/// <summary>
/// Microsoft's identity platform, as far as the app's real OpenID Connect handler can tell: its metadata
/// (authorization and token endpoints, one signing key), and a token endpoint that answers a code
/// redemption with an ID token signed by that key. The test decides what the next ID token says.
/// </summary>
public sealed class FakeMicrosoft : HttpMessageHandler
{
    public const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";
    public const string ClientId = "0b5f6c9e-1a2b-4c3d-9e8f-123456789abc";
    public const string AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    public const string OtherTenant = "11111111-2222-4333-8444-555555555555";

    private readonly SigningCredentials _signing;

    public FakeMicrosoft()
    {
        Configuration = new OpenIdConnectConfiguration
        {
            Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0",
            AuthorizationEndpoint = AuthorizationEndpoint,
            TokenEndpoint = TokenEndpoint,
            JsonWebKeySet = new JsonWebKeySet(),
        };
        // As in Microsoft's common keys: valid for any tenant, the tenant put in by the token.
        _signing = AddKey("fake-microsoft-key", "https://login.microsoftonline.com/{tenantid}/v2.0");
        // A key published for one tenant only (as for an app with its own signing key there).
        OtherTenantKey = AddKey("other-tenant-key", $"https://login.microsoftonline.com/{OtherTenant}/v2.0");
        // A key Microsoft never published: signatures made with it must not verify.
        var unknown = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "fake-microsoft-key" };
        UnknownKey = new SigningCredentials(unknown, SecurityAlgorithms.RsaSha256);
    }

    private SigningCredentials AddKey(string keyId, string issuer)
    {
        var rsa = new RsaSecurityKey(RSA.Create(2048)) { KeyId = keyId };
        var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.Rsa.ExportParameters(false)) { KeyId = keyId });
        key.AdditionalData["issuer"] = issuer;
        Configuration.JsonWebKeySet.Keys.Add(key);
        Configuration.SigningKeys.Add(key);
        return new SigningCredentials(rsa, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>Signs as the key published for <see cref="OtherTenant"/> only.</summary>
    public SigningCredentials OtherTenantKey { get; }

    /// <summary>Signs with a key that is not in Microsoft's published keys, under the real key's id.</summary>
    public SigningCredentials UnknownKey { get; }

    public OpenIdConnectConfiguration Configuration { get; }

    /// <summary>The ID token the next code redemption returns.</summary>
    public string? NextIdToken { get; set; }

    /// <summary>Every code redemption the host made, as form fields.</summary>
    public ConcurrentQueue<Dictionary<string, string>> Redemptions { get; } = new();

    /// <summary>An ID token as Microsoft issues one for the <c>common</c> endpoint.</summary>
    /// <param name="verified">The <c>xms_edov</c> claim; null leaves it out.</param>
    /// <param name="issuerTenant">The tenant in <c>iss</c>; defaults to <paramref name="tenant"/>.</param>
    public string IdToken(string nonce, string? email, bool? verified, string tenant = Tenant, string? objectId = null, string? issuerTenant = null,
        string audience = ClientId, SigningCredentials? signWith = null)
    {
        var oid = objectId ?? ObjectIdOf(email ?? "nobody");
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenant,
            ["oid"] = oid,
            // Pairwise per app in real tokens; never used by Multiplex, but OpenID Connect requires it.
            ["sub"] = "pairwise-" + oid,
            ["nonce"] = nonce,
            ["ver"] = "2.0",
        };
        if (email is not null) claims["email"] = email;
        if (verified is { } v) claims["xms_edov"] = v;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{issuerTenant ?? tenant}/v2.0",
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            SigningCredentials = signWith ?? _signing,
        });
    }

    /// <summary>The object id a test person has (the same one <see cref="TestPeople.SubjectOf"/> uses).</summary>
    public static string ObjectIdOf(string email) => TestPeople.SubjectOf(email).Split('/')[1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsoluteUri != TokenEndpoint)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken))
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        Redemptions.Enqueue(form);
        var body = JsonSerializer.Serialize(new { id_token = NextIdToken, access_token = "microsoft-access-token", token_type = "Bearer", expires_in = 3600 });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>The app with Microsoft sign-in configured against <see cref="FakeMicrosoft"/>.</summary>
public sealed class MicrosoftTestApp : AuthTestApp
{
    public FakeMicrosoft Microsoft { get; } = new();

    /// <summary>Every log entry the host writes (shown when a sign-in test fails, to say why).</summary>
    public SignIn.LogCapture Logs { get; } = new();

    protected override IDictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings[MultiplexIdentity.ClientIdKey] = FakeMicrosoft.ClientId;
            settings[MultiplexIdentity.ClientSecretKey] = "test-client-secret";
            return settings;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(Logs);
            services.Configure<OpenIdConnectOptions>(MultiplexIdentity.MicrosoftScheme, o =>
            {
                o.Configuration = Microsoft.Configuration;
                o.BackchannelHttpHandler = Microsoft;
            });
        });
    }

    /// <summary>A browser: keeps cookies, does not follow redirects, talks HTTPS like the public address.</summary>
    public HttpClient Browser() =>
        CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(new CookieContainer()));
}
