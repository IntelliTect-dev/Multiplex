using ModelContextProtocol.Protocol;
using TripIt;

namespace TripItMcp;

/// <summary>
/// Turns expected failures into tool results whose text the model can act on. The text is
/// <see cref="TripItErrorText.ForTool"/>: a short message (TripIt's validation message only
/// when it is short plain text), never TripIt's response body or session values.
/// </summary>
internal static class ToolErrors
{
    public static bool IsReportable(Exception ex) => TripItErrorText.ForTool(ex) is not null;

    public static CallToolResult ToResult(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = TripItErrorText.ForTool(ex) ?? "The TripIt tool failed." }],
    };
}
