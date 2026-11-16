namespace Multiplex.Web.Mcp;

/// <summary>
/// Assigns a tool class to the MCP endpoint at <c>/{slug}/mcp</c>. Tool classes without it are not served.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpEndpointAttribute(string slug) : Attribute
{
    public string Slug { get; } = slug;
}
