using ModelContextProtocol;

namespace Multiplex.Web.Services.LinkedIn;

/// <summary>Reads the public profile id a tool was given, as an id or as a profile URL.</summary>
public static class LinkedInProfileId
{
    private const int MaxLength = 100;

    /// <summary>
    /// <c>jane-doe</c>, <c>https://www.linkedin.com/in/jane-doe/</c> and <c>linkedin.com/in/jane-doe?trk=x</c>
    /// all give <c>jane-doe</c>. Anything else that names a path, a query or another site is refused,
    /// so a tool can never be pointed at a LinkedIn address other than one member's profile.
    /// </summary>
    public static string Normalize(string? value, string parameter)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) throw Invalid(parameter, "is empty");

        var id = text;
        if (LooksLikeUrl(text))
        {
            if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !IsLinkedInHost(uri.Host))
            {
                throw Invalid(parameter, "is not a LinkedIn profile URL");
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is not ["in", var segment])
                throw Invalid(parameter, "is not a profile URL (expected https://www.linkedin.com/in/<id>/)");
            id = segment;
        }

        try
        {
            id = Uri.UnescapeDataString(id);
        }
        catch (UriFormatException)
        {
            throw Invalid(parameter, "is not a valid profile id");
        }
        if (id.Length is 0 or > MaxLength || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '&' or '%' or ':' or '(' or ')' or ','))
            throw Invalid(parameter, "is not a valid profile id");
        return id;
    }

    private static bool LooksLikeUrl(string text) =>
        text.Contains('/', StringComparison.Ordinal) || text.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkedInHost(string host) =>
        host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase);

    private static McpException Invalid(string parameter, string problem) =>
        new($"{parameter} {problem}. Pass the member's public profile id (the part after /in/ in their profile URL) or the profile URL itself.");
}
