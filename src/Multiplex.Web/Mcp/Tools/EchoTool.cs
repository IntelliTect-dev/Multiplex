using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Multiplex.Web.Mcp.Tools;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool(Name = "echo"), Description("Echoes the message back to the caller.")]
    public static string Echo([Description("The message to echo.")] string message) => $"Echo: {message}";
}
