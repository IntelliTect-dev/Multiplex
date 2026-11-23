using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Validators;
using Multiplex.Web.Auth;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Identity;

/// <summary>
/// Signing in to Multiplex itself: OpenID Connect against the Microsoft identity platform (personal and
/// work or school accounts), the email allowlist (<see cref="PersonAdmission"/>), and a cookie that
/// remembers the person for <see cref="SessionLifetime"/>. Microsoft only says who the person is;
/// Multiplex stays the OAuth server Claude and ChatGPT talk to. Microsoft never calls this host: the
/// person's browser brings the answer back to <see cref="CallbackPath"/>, and the host calls out to
/// Microsoft to redeem it. Another provider (Google) is one more scheme mapped to an
/// <see cref="ExternalIdentity"/>; nothing after admission depends on the provider.
/// </summary>
public static class MultiplexIdentity
{
    public const string CookieScheme = "Multiplex";
    public const string MicrosoftScheme = "microsoft";

    /// <summary>Where Microsoft sends the browser back; register <c>&lt;PublicBaseUrl&gt;/signin-microsoft</c> as the redirect URI.</summary>
    public const string CallbackPath = "/signin-microsoft";

    public const string SignOutPath = "/signout";

    public const string ClientIdKey = "Multiplex:Identity:Microsoft:ClientId";
    public const string ClientSecretKey = "Multiplex:Identity:Microsoft:ClientSecret";

    /// <summary>How long one sign-in lasts before Microsoft is asked again. Not sliding.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    // Lets the handler start when the owner has not configured Microsoft yet; it is never sent anywhere,
    // because nothing challenges until MicrosoftSignInSettings.IsConfigured.
    private const string NotConfiguredClientId = "not-configured";

    public static IServiceCollection AddMultiplexIdentity(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(PersonAdmission))) return services;

        services.AddMultiplexDataProtection();
        services.AddSingleton<AllowedPeople>();
        services.AddSingleton<PersonAdmission>();
        services.AddSingleton<MicrosoftSignInSettings>();

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieScheme;
                o.DefaultChallengeScheme = MicrosoftScheme;
            })
            .AddCookie(CookieScheme)
            .AddOpenIdConnect(MicrosoftScheme, _ => { });

        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<AuthIssuer>(ConfigureCookie);
        services.AddOptions<OpenIdConnectOptions>(MicrosoftScheme).Configure<IConfiguration, AuthIssuer>(ConfigureMicrosoft);
        return services;
    }

    /// <summary>The signed-in person on a web request, or null.</summary>
    public static MultiplexPerson? PersonOf(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true, AuthenticationType: CookieScheme }
        && user.FindFirst(SiteClaims.Owner)?.Value is { } owner
            ? new MultiplexPerson(owner, user.FindFirst("email")?.Value ?? "")
            : null;

    /// <summary>Maps <c>POST /signout</c>: forgets the sign-in on this browser (Microsoft's own session is left alone).</summary>
    public static IEndpointRouteBuilder MapMultiplexSignOut(this IEndpointRouteBuilder app)
    {
        app.MapPost(SignOutPath, async context =>
        {
            await context.SignOutAsync(CookieScheme);
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = "/";
        }).DisableAntiforgery();
        return app;
    }

    /// <summary>
    /// Where a failed sign-in's page sends the person to start again: the page they were going to, if it
    /// is a path on this server, else the home page. A browser reads "\" as "/" and strips tabs and line
    /// breaks from a URL, so a path holding either could become "//other.site/".
    /// </summary>
    internal static string StartAgainPath(string? returnTo) =>
        returnTo is ['/', ..] && !returnTo.StartsWith("//", StringComparison.Ordinal)
        && !returnTo.Contains('\\') && !returnTo.Any(char.IsControl)
            ? returnTo
            : "/";

    /// <summary>
    /// Whether Microsoft says the person declined its permission request: AADSTS65004 (a work or school
    /// account, sent as consent_required or access_denied), or a personal account's access_denied, which
    /// carries no AADSTS code. Another AADSTS code (admin approval needed, say) is not the person's choice.
    /// </summary>
    private static bool Declined(Exception? failure)
    {
        if (failure is not OpenIdConnectProtocolException { Data: var data }) return false;
        var description = data["error_description"] as string ?? "";
        return description.StartsWith("AADSTS65004:", StringComparison.Ordinal)
               || (data["error"] as string == "access_denied" && !description.StartsWith("AADSTS", StringComparison.Ordinal));
    }

    private static CookieSecurePolicy SecurePolicy(AuthIssuer issuer) =>
        issuer.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    private static void ConfigureCookie(CookieAuthenticationOptions o, AuthIssuer issuer)
    {
        o.Cookie.Name = "multiplex_session";
        o.Cookie.HttpOnly = true;
        // Lax, not Strict: the cookie must come along when Claude or ChatGPT sends the browser here
        // from another site, so a second connector does not ask for Microsoft again.
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Behind an HTTPS tunnel the server itself sees http; the public address decides.
        o.Cookie.SecurePolicy = SecurePolicy(issuer);
        o.Cookie.IsEssential = true;
        o.ExpireTimeSpan = SessionLifetime;
        o.SlidingExpiration = false;
        o.Events = new CookieAuthenticationEvents
        {
            // Every request: a person taken off the list (or unbound) is signed out at once.
            OnValidatePrincipal = async context =>
            {
                var owner = context.Principal?.FindFirst(SiteClaims.Owner)?.Value;
                var admission = context.HttpContext.RequestServices.GetRequiredService<PersonAdmission>();
                if (owner is null || !await admission.IsAllowedAsync(owner, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieScheme);
                }
            },
            // The access gate decides when to challenge; the cookie handler never redirects on its own.
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            },
        };
    }

    private static void ConfigureMicrosoft(OpenIdConnectOptions o, IConfiguration configuration, AuthIssuer issuer)
    {
        o.Authority = MicrosoftAccounts.Authority;
        o.ClientId = configuration[ClientIdKey] is { Length: > 0 } id ? id.Trim() : NotConfiguredClientId;
        o.ClientSecret = configuration[ClientSecretKey];
        o.ResponseType = OpenIdConnectResponseType.Code;
        // A top-level GET back to the callback, so the correlation and nonce cookies can stay SameSite=Lax.
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.UsePkce = true;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");
        o.CallbackPath = CallbackPath;
        // Multiplex never signs out at Microsoft, so the handler serves no sign-out paths of its own.
        o.RemoteSignOutPath = PathString.Empty;
        o.SignedOutCallbackPath = PathString.Empty;
        o.SignInScheme = CookieScheme;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.DisableTelemetry = true;

        o.TokenValidationParameters.IssuerValidator = MicrosoftAccounts.ValidateIssuer;
        // Microsoft's own check that a signing key is only used for the tenant it belongs to.
        o.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();

        var secure = SecurePolicy(issuer);
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = secure;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = secure;

        o.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                // From the configured public address, never from the request: behind a tunnel the
                // request says http and may name another host. The code is redeemed with the same URI.
                context.ProtocolMessage.RedirectUri = issuer.Issuer + CallbackPath;
                // Lets the person choose between their personal and work accounts.
                context.ProtocolMessage.Prompt = "select_account";
                return Task.CompletedTask;
            },
            // Not at TokenValidated: the handler checks the nonce (and the rest of the token response) only
            // after that event, so admitting there could bind an address for a sign-in that then fails.
            // TicketReceived runs once every check has passed, before the cookie is issued.
            OnTicketReceived = async context =>
            {
                var services = context.HttpContext.RequestServices;
                var admission = MicrosoftAccounts.From(context.Principal!) is { } identity
                    ? await services.GetRequiredService<PersonAdmission>().AdmitAsync(identity, context.HttpContext.RequestAborted)
                    : new Admission.Refused(PersonAdmission.NotAllowedMessage);
                if (admission is not Admission.Admitted { Person: var person })
                {
                    context.HandleResponse();
                    await ConnectEndpoints.WriteHtmlAsync(context.HttpContext, StatusCodes.Status403Forbidden,
                        nonce => ConnectHtml.Message(nonce, "Not allowed", ((Admission.Refused)admission).Message));
                    return;
                }

                var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(SiteClaims.Owner, person.Owner), new Claim("email", person.Email)],
                    authenticationType: CookieScheme, nameType: "email", roleType: null));
                context.Properties!.IsPersistent = true;
                context.Properties.AllowRefresh = false;
                context.Properties.ExpiresUtc = now + SessionLifetime;
            },
            OnRemoteFailure = async context =>
            {
                context.HandleResponse();
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MultiplexIdentity));
                // The type only: a message can carry values from the request or from Microsoft.
                logger.LogWarning("Microsoft sign-in did not complete: {Error}.", context.Failure?.GetType().Name ?? "unknown");
                // Never "go back": Back reopens Microsoft's reply, whose one-time cookie is spent, so it
                // fails again (#112). The link starts a fresh sign-in for the page the person wanted.
                var again = StartAgainPath(context.Properties?.RedirectUri);
                await ConnectEndpoints.WriteHtmlAsync(context.HttpContext, StatusCodes.Status400BadRequest, nonce => Declined(context.Failure)
                    ? ConnectHtml.Message(nonce, "Sign-in was declined",
                        "Microsoft's permission request for Multiplex was declined, so you are not signed in. Multiplex asks only to sign you in and to see your name and email address. To sign in, start again and choose Accept.",
                        again, "Start the sign-in again")
                    : ConnectHtml.Message(nonce, "Sign-in did not finish",
                        "The sign-in with Microsoft did not finish, or this page was opened again after it had. Start the sign-in again with the link below, not the Back button.",
                        again, "Start the sign-in again"));
            },
        };
    }
}

/// <summary>Whether the owner has configured the Microsoft app registration (client id and secret).</summary>
public sealed class MicrosoftSignInSettings(IConfiguration configuration)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration[MultiplexIdentity.ClientIdKey])
        && !string.IsNullOrWhiteSpace(configuration[MultiplexIdentity.ClientSecretKey]);
}
