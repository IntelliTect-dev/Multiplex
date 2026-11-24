using System.Reflection;
using ModelContextProtocol.Server;

namespace Multiplex.Web.Mcp;

public static class McpEndpointRouting
{
    /// <summary>The MCP address of every service: one <c>/{slug}/mcp</c> per slug used by an <see cref="McpEndpointAttribute"/>.</summary>
    public static IReadOnlyList<string> EndpointPaths { get; } = Assembly.GetExecutingAssembly().GetTypes()
        .Select(t => t.GetCustomAttribute<McpEndpointAttribute>()?.Slug)
        .OfType<string>()
        .Distinct()
        .Select(slug => $"/{slug}/mcp")
        .ToList();

    /// <summary>
    /// Fails startup when an MCP endpoint is not registered as requiring sign-in. The access gate
    /// already refuses such an endpoint (404); this makes the mistake loud instead of a silent 404.
    /// </summary>
    public static void EnsureEveryEndpointRequiresSignIn(Auth.AuthenticatedEndpoints endpoints)
    {
        var missing = EndpointPaths.Select(p => p.Trim('/')[..^"/mcp".Length])
            .Where(slug => endpoints.FindBySlug(slug) is null)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Every MCP endpoint must require sign-in; register {string.Join(", ", missing)} with RequireAuthFor.");
        }
    }

    /// <summary>Maps an MCP endpoint at each of <see cref="EndpointPaths"/>.</summary>
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var path in EndpointPaths)
        {
            app.MapMcp(path);
        }
    }

    /// <summary>
    /// Makes a session the service at the endpoint the request arrived on: only that service's tools, and its
    /// own name, website and icon in <c>serverInfo</c> (<see cref="ServiceIdentity"/>).
    /// </summary>
    public static Task ConfigureForEndpoint(HttpContext context, McpServerOptions options, CancellationToken ct)
    {
        var path = context.Request.Path.Value!.TrimEnd('/');
        var slug = path[..^"/mcp".Length].Trim('/');

        if (context.RequestServices.GetRequiredService<ServiceIdentity>().ServerInfo(slug) is { } serverInfo)
        {
            options.ServerInfo = serverInfo;
        }

        var scoped = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in options.ToolCollection ?? [])
        {
            var toolSlug = tool.Metadata.OfType<MethodInfo>().FirstOrDefault()?.DeclaringType
                ?.GetCustomAttribute<McpEndpointAttribute>()?.Slug;
            if (toolSlug == slug) scoped.Add(tool);
        }
        options.ToolCollection = scoped;
        return Task.CompletedTask;
    }
}
