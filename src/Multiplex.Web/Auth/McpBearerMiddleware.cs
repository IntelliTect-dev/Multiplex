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
    ILogger<McpBearerMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (endpoints.FindByPath(context.Request.Path) is not { } endpoint)
        {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejected a bearer token for site {Site}.")]
    private static partial void LogInvalidToken(ILogger logger, string site);
}
