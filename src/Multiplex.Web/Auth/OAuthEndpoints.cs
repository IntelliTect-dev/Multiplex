using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Multiplex.Web.Auth.Clients;

namespace Multiplex.Web.Auth;

/// <summary>Maps the discovery documents and the <c>/oauth/*</c> endpoints.</summary>
public static class OAuthEndpoints
{
    internal const string RegisterPolicy = "multiplex-auth-register";
    internal const string AuthorizePolicy = "multiplex-auth-authorize";
    internal const string TokenPolicy = "multiplex-auth-token";

    private const int MaxRegistrationBytes = 16 * 1024;
    private const int MaxFormBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Maps RFC 8414 authorization server metadata (also at the OIDC discovery path), RFC 9728 protected
    /// resource metadata (per endpoint and at the root), and register, authorize, token and revoke.
    /// </summary>
    public static IEndpointRouteBuilder MapMultiplexAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", ServerMetadata);
        app.MapGet("/.well-known/openid-configuration", ServerMetadata);
        app.MapGet("/.well-known/oauth-protected-resource", (AuthenticatedEndpoints endpoints, AuthIssuer issuer) =>
            endpoints.All.Count == 0 ? Results.NotFound() : ResourceMetadata(endpoints.All[0], issuer));
        app.MapGet("/.well-known/oauth-protected-resource/{slug}/mcp", (string slug, AuthenticatedEndpoints endpoints, AuthIssuer issuer) =>
            endpoints.FindBySlug(slug) is { } endpoint ? ResourceMetadata(endpoint, issuer) : Results.NotFound());

        app.MapPost("/oauth/register", Register).RequireRateLimiting(RegisterPolicy).DisableAntiforgery();
        app.MapGet("/oauth/authorize", Authorize).RequireRateLimiting(AuthorizePolicy);
        app.MapPost("/oauth/token", Token).RequireRateLimiting(TokenPolicy).DisableAntiforgery();
        app.MapPost("/oauth/revoke", Revoke).RequireRateLimiting(TokenPolicy).DisableAntiforgery();
        return app;
    }

    private static IResult ServerMetadata(AuthIssuer issuer, AuthenticatedEndpoints endpoints) => Results.Json(new
    {
        issuer = issuer.Issuer,
        authorization_endpoint = issuer.AuthorizationEndpoint,
        token_endpoint = issuer.TokenEndpoint,
        registration_endpoint = issuer.RegistrationEndpoint,
        revocation_endpoint = issuer.RevocationEndpoint,
        response_types_supported = new[] { "code" },
        response_modes_supported = new[] { "query" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        code_challenge_methods_supported = new[] { "S256" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        revocation_endpoint_auth_methods_supported = new[] { "none" },
        client_id_metadata_document_supported = true,
        authorization_response_iss_parameter_supported = true,
        scopes_supported = endpoints.All.Select(e => e.Slug).ToArray(),
    }, Json);

    private static IResult ResourceMetadata(AuthenticatedEndpoint endpoint, AuthIssuer issuer) => Results.Json(new
    {
        resource = endpoint.Resource,
        authorization_servers = new[] { issuer.Issuer },
        scopes_supported = new[] { endpoint.Slug },
        bearer_methods_supported = new[] { "header" },
        resource_name = endpoint.DisplayName,
    }, Json);

    private static async Task<IResult> Register(HttpContext context, OAuthClients clients)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Error("invalid_client_metadata", "The registration request must be application/json.");
        }

        var body = await ReadBoundedAsync(context.Request.Body, MaxRegistrationBytes, context.RequestAborted);
        if (body is null) return Error("invalid_client_metadata", "The registration request is too large.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Error("invalid_client_metadata", "The registration request is not valid JSON.");
        }

        using (document)
        {
            var (status, response) = await clients.RegisterAsync(document.RootElement, context.RequestAborted);
            NoStore(context.Response);
            return Results.Json(response, Json, statusCode: status);
        }
    }

    private static async Task<IResult> Authorize(HttpContext context, AuthorizationServer server)
    {
        var outcome = await server.AuthorizeAsync(context.Request.Query, context.Request.PathBase, context.RequestAborted);
        NoStore(context.Response);
        return outcome switch
        {
            AuthorizeOutcome.Redirect r => Results.Redirect(r.Location),
            AuthorizeOutcome.ErrorPage e => Results.Content(ErrorPage(e.Message), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status400BadRequest),
            _ => throw new InvalidOperationException(),
        };
    }

    private static async Task<IResult> Token(HttpContext context, TokenService tokens)
    {
        var (form, problem) = await ReadFormAsync(context);
        if (problem is not null) return problem;
        return ToResult(context, await tokens.ExchangeAsync(form!, BasicClientId(context.Request), context.RequestAborted));
    }

    private static async Task<IResult> Revoke(HttpContext context, TokenService tokens)
    {
        var (form, problem) = await ReadFormAsync(context);
        if (problem is not null) return problem;
        return ToResult(context, await tokens.RevokeAsync(form!, BasicClientId(context.Request), context.RequestAborted));
    }

    private static async Task<(IFormCollection?, IResult?)> ReadFormAsync(HttpContext context)
    {
        NoStore(context.Response);
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type)
            || !type.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Error("invalid_request", "The request must be application/x-www-form-urlencoded."));
        }

        // Counted as it is read, so a chunked body (no Content-Length) is held to the same limit.
        var body = await ReadBoundedAsync(context.Request.Body, MaxFormBytes, context.RequestAborted);
        if (body is null) return (null, Error("invalid_request", "The request is too large."));
        try
        {
            return (new FormCollection(new FormReader(Encoding.UTF8.GetString(body)).ReadForm()), null);
        }
        catch (InvalidDataException)
        {
            return (null, Error("invalid_request", "The request body could not be read."));
        }
    }

    private static IResult ToResult(HttpContext context, OAuthResponse response)
    {
        NoStore(context.Response);
        return response.Body is null ? Results.Ok() : Results.Json(response.Body, Json, statusCode: response.Status);
    }

    /// <summary>Public clients may still send <c>Authorization: Basic</c> with their id and an empty secret.</summary>
    private static string? BasicClientId(HttpRequest request)
    {
        if (request.Headers.Authorization is not [{ } header] || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var id = Uri.UnescapeDataString(decoded.Split(':', 2)[0]);
            return id.Length > 0 ? id : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IResult Error(string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, Json,
            statusCode: StatusCodes.Status400BadRequest);

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string ErrorPage(string message) => $"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Cannot connect</title></head>
        <body style="font-family: system-ui, sans-serif; max-width: 36rem; margin: 2rem auto; padding: 0 1rem;">
        <h1>This connection request can't be used</h1>
        <p>{HtmlEncoder.Default.Encode(message)}</p>
        <p>Go back to the app you came from and try connecting again.</p>
        </body>
        </html>
        """;

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
