using ModelContextProtocol.Protocol;
using TripIt;

namespace TripItMcp;

/// <summary>Turns expected failures into tool results whose text the model can act on.</summary>
internal static class ToolErrors
{
    public static bool IsReportable(Exception ex)
        => ex is ArgumentException or TripItApiException or TripItAuthenticationException or System.Text.Json.JsonException;

    public static CallToolResult ToResult(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }],
    };
}
