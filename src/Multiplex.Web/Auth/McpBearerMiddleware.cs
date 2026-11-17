namespace Multiplex.Web.Auth;

/// <summary>
/// Whole-server protection for authenticated MCP endpoints: every request to <c>/{slug}/mcp</c> (or
/// below), <c>initialize</c> included, needs a valid bearer token whose audience is that endpoint.
/// Otherwise the answer is a transport-level 401 whose <c>WWW-Authenticate</c> header points at the
/// protected resource metadata; that is what makes Claude and ChatGPT start sign-in. Endpoints not
/// registered as authenticated (e.g. <c>/time/mcp</c>) pass through untouched.
/// </summary>
public sealed partial class McpBearerMiddleware(
    RequestDelegate next,
    AuthenticatedEndpoints endpoints,
    TokenService tokens,
    IConfiguration configuration,
    ILogger<McpBearerMiddleware> logger)
{
    // MCP endpoints that need no sign-in may be called from the internet only when listed here.
    private readonly HashSet<string> _publicOpenEndpoints = new(
        configuration.GetSection("Multiplex:PublicOpenEndpoints").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (endpoints.FindByPath(context.Request.Path) is not { } endpoint)
        {
            // An endpoint without sign-in (e.g. /time/mcp, which drives a browser on this PC) stays
            // local: once a tunnel publishes the server, anyone on the internet could call it.
            if (OpenMcpSlug(context.Request.Path) is { } slug && !_publicOpenEndpoints.Contains(slug)
                && Sites.RequestOrigin.IsFromOutsideThisPc(context))
            {
                LogOpenEndpointRefused(logger, slug);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization;
        var presented = authorization.Count > 0;
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

    /// <summary>The slug of a <c>/{slug}/mcp</c> path (or below), or null for any other path.</summary>
    internal static string? OpenMcpSlug(PathString path) =>
        path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var slug, var mcp, ..]
            && mcp.Equals("mcp", StringComparison.OrdinalIgnoreCase)
            ? slug
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected a bearer token for site {Site}.")]
    private static partial void LogInvalidToken(ILogger logger, string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused an internet request to the open endpoint /{Slug}/mcp; list it in Multiplex:PublicOpenEndpoints to allow that.")]
    private static partial void LogOpenEndpointRefused(ILogger logger, string slug);
}
