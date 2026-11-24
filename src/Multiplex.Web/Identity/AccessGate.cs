using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Multiplex.Web.Auth;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Web.Identity;

/// <summary>
/// The one access rule every request passes (decision D3 of #80):
/// <list type="bullet">
/// <item>Open to anyone: <c>/healthz</c>, <c>/.well-known/*</c>, <c>/oauth/register</c>, <c>/oauth/token</c>,
/// <c>/oauth/revoke</c>, the Microsoft callback and sign-out, and the services' icons under <c>/icons</c>
/// (public brand images, which a client fetches without credentials; see <see cref="Mcp.ServiceIdentity"/>).</item>
/// <item>Every <c>/{slug}/mcp</c> needs a bearer token whose audience is that endpoint, issued to a person
/// still on the allowlist. A web sign-in never counts there. A path shaped like an MCP endpoint that is not
/// registered is 404, so no service can be served without sign-in.</item>
/// <item>Every web page needs a person signed in to Multiplex.</item>
/// <item>The host exemption: a request from this machine, judged only by
/// <see cref="RequestOrigin.IsFromOutsideThisPc"/>, needs no sign-in for a web page, nor a token for a
/// service that uses no person's site account. Connecting a service (<c>/oauth/authorize</c>,
/// <c>/connect/*</c>) always needs a signed-in person, because every grant belongs to one; and a service
/// that uses a person's site account always needs a token, because the token says whose account.</item>
/// </list>
/// </summary>
public sealed partial class AccessGate(
    RequestDelegate next,
    AuthenticatedEndpoints endpoints,
    TokenService tokens,
    MicrosoftSignInSettings microsoft,
    ILogger<AccessGate> logger)
{
    private static readonly string[] OpenPaths =
        ["/healthz", "/oauth/register", "/oauth/token", "/oauth/revoke", MultiplexIdentity.CallbackPath, MultiplexIdentity.SignOutPath];

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsOpen(context))
        {
            await next(context);
            return;
        }

        if (McpSlug(context.Request.Path) is { } slug)
        {
            // A browser's web sign-in must never authorize an MCP call.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            if (endpoints.FindBySlug(slug) is not { } endpoint)
            {
                LogUnregisteredEndpoint(logger, slug);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await McpAsync(context, endpoint);
            return;
        }

        if (MultiplexIdentity.PersonOf(context.User) is not null
            || (!NeedsPerson(context.Request.Path) && !RequestOrigin.IsFromOutsideThisPc(context)))
        {
            await next(context);
            return;
        }

        await ChallengeAsync(context);
    }

    private async Task McpAsync(HttpContext context, AuthenticatedEndpoint endpoint)
    {
        var authorization = context.Request.Headers.Authorization;
        var presented = authorization.Count > 0;
        if (!presented && !endpoint.NeedsSiteAccount && !RequestOrigin.IsFromOutsideThisPc(context))
        {
            await next(context);
            return;
        }

        var token = authorization is [{ } header] && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        var principal = string.IsNullOrEmpty(token) ? null : await tokens.ValidateAsync(token, endpoint, context.RequestAborted);
        if (principal is null)
        {
            if (presented) LogInvalidToken(logger, endpoint.Slug);
            Challenge(context.Response, endpoint, presented);
            return;
        }

        context.User = principal;
        await next(context);
    }

    private async Task ChallengeAsync(HttpContext context)
    {
        if (!microsoft.IsConfigured)
        {
            LogNotConfigured(logger);
            await ConnectEndpoints.WriteHtmlAsync(context, StatusCodes.Status503ServiceUnavailable, nonce => ConnectHtml.Message(nonce,
                "Sign-in is not set up",
                "Signing in to this Multiplex server is not set up yet: its owner has not configured the Microsoft sign-in."));
            return;
        }

        var request = context.Request;
        // Only a page the browser is loading goes to Microsoft; an image, script or fetch just gets 401.
        var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        var pageLoad = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                       && (destination.Length == 0 || destination.Equals("document", StringComparison.OrdinalIgnoreCase));
        if (!pageLoad)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        // A path on this server only. "//host/..." (or "/\host", which browsers read the same way) would
        // send the browser to another site once it is signed in, so such a request returns home instead.
        var path = request.PathBase.Value + request.Path.Value;
        var back = path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal)
            ? "/"
            : $"{request.PathBase}{request.Path}{request.QueryString}";
        await context.ChallengeAsync(MultiplexIdentity.MicrosoftScheme, new AuthenticationProperties { RedirectUri = back });
    }

    /// <summary>The transport-level 401 that makes Claude and ChatGPT start connecting.</summary>
    internal static void Challenge(HttpResponse response, AuthenticatedEndpoint endpoint, bool tokenPresented)
    {
        var challenge = $"Bearer resource_metadata=\"{endpoint.ResourceMetadataUrl}\", scope=\"{endpoint.Slug}\"";
        if (tokenPresented)
        {
            challenge += ", error=\"invalid_token\", error_description=\"The access token is invalid, expired, revoked, or for another resource.\"";
        }

        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = challenge;
        response.Headers.CacheControl = "no-store";
    }

    private static bool IsOpen(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)
               || path.StartsWithSegments(Mcp.ServiceIdentity.IconsPath, StringComparison.OrdinalIgnoreCase)
               || OpenPaths.Any(open => IsExactly(path, open))
               // The error page re-run after an exception: the request it serves already passed (or was open).
               || context.Features.Get<IExceptionHandlerFeature>() is not null;
    }

    /// <summary>Connecting a service creates a grant, which always belongs to a signed-in person.</summary>
    private static bool NeedsPerson(PathString path) =>
        IsExactly(path, "/oauth/authorize") || path.StartsWithSegments("/connect", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactly(PathString path, string expected) =>
        path.StartsWithSegments(expected, StringComparison.OrdinalIgnoreCase, out var rest) && (!rest.HasValue || rest == "/");

    /// <summary>The slug of a <c>/{slug}/mcp</c> path (or below), ignoring case and empty segments as routing does.</summary>
    internal static string? McpSlug(PathString path) =>
        path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var slug, var mcp, ..]
        && mcp.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            ? slug
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected a bearer token for site {Site}.")]
    private static partial void LogInvalidToken(ILogger logger, string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused /{Slug}/mcp: it is not registered as a service that requires sign-in.")]
    private static partial void LogUnregisteredEndpoint(ILogger logger, string slug);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refused a request that needs sign-in: the Microsoft sign-in is not configured (Multiplex:Identity:Microsoft:ClientId and ClientSecret).")]
    private static partial void LogNotConfigured(ILogger logger);
}
