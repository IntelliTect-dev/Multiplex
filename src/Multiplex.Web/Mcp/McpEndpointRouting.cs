using System.Reflection;
using ModelContextProtocol.Server;

namespace Multiplex.Web.Mcp;

public static class McpEndpointRouting
{
    /// <summary>Maps one <c>/{slug}/mcp</c> endpoint per slug used by an <see cref="McpEndpointAttribute"/>.</summary>
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var slug in Assembly.GetExecutingAssembly().GetTypes()
                     .Select(t => t.GetCustomAttribute<McpEndpointAttribute>()?.Slug)
                     .OfType<string>()
                     .Distinct())
        {
            app.MapMcp($"/{slug}/mcp");
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
