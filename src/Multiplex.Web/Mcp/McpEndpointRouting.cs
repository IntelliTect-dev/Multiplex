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

    /// <summary>Maps an MCP endpoint at each of <see cref="EndpointPaths"/>.</summary>
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var path in EndpointPaths)
        {
            app.MapMcp(path);
        }
    }

    /// <summary>Restricts a session's tools to those belonging to the endpoint the request arrived on.</summary>
    public static Task ScopeToolsToEndpoint(HttpContext context, McpServerOptions options, CancellationToken ct)
    {
        var path = context.Request.Path.Value!.TrimEnd('/');
        var slug = path[..^"/mcp".Length].Trim('/');

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
