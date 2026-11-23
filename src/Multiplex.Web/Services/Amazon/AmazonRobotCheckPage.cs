namespace Multiplex.Web.Services.Amazon;

/// <summary>
/// One of Amazon's robot checks a person can answer through a form: where the answer goes, the
/// fields that go with it, and, for the "type the characters" kind, the picture and the field
/// the typed characters go in.
/// </summary>
internal sealed record AmazonRobotCheck(
    Uri Action,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string? AnswerField,
    Uri? Picture)
{
    /// <summary>Whether the person has characters to type, or only a button to press.</summary>
    public bool NeedsTyping => AnswerField is not null;

    /// <summary>The address that submits the answer: Amazon's own form fields, plus what the person typed.</summary>
    public Uri AnswerUrl(string? typed)
    {
        var fields = AnswerField is null ? Fields : [.. Fields, new KeyValuePair<string, string>(AnswerField, typed ?? "")];
        var query = string.Join("&", fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
        return new Uri($"{Action.GetLeftPart(UriPartial.Path)}?{query}");
    }
}

/// <summary>
/// What the service knows about Amazon's robot-check pages. Two kinds have a form a person can
/// answer: "Click the button below to continue shopping" (seen on 2026-11-23: every field is
/// already filled in, and there is one button), and "Enter the characters you see below" (a
/// picture and a text box; not yet seen by this service).
/// </summary>
internal static class AmazonRobotCheckPage
{
    internal const string Form = "form[action*=\"validateCaptcha\"]";
    internal const string HiddenField = "input[type=\"hidden\"][name]";
    internal const string TextField = "input[name]:not([type=\"hidden\"]):not([type=\"submit\"]):not([type=\"button\"])";
    internal const string PictureImage = "img[src*=\"captcha\" i]";
    /// <summary>The only places an answer is ever sent.</summary>
    internal static readonly string[] AnswerPaths = ["/errors/validateCaptcha", "/errors_page/validateCaptcha"];
    public const int MaxFields = 12;

    /// <summary>
    /// Reads the check on <paramref name="html"/>, or returns null when it is not one this service
    /// can hand to a person. Every address on the page is checked: the form must send a GET to
    /// one of Amazon's two check addresses on https://www.amazon.com, and the picture must be on
    /// one of Amazon's image hosts. A page that fails any of that is not handed over.
    /// </summary>
    public static AmazonRobotCheck? TryParse(string html, Uri pageUrl)
    {
        var document = AmazonText.ParseWithoutScripts(html);
        var form = document.QuerySelector(Form);
        if (form is null) return null;
        if (!string.Equals(form.GetAttribute("method") ?? "get", "get", StringComparison.OrdinalIgnoreCase)) return null;

        if (!Uri.TryCreate(pageUrl, form.GetAttribute("action"), out var action)
            || action.Scheme != Uri.UriSchemeHttps || !action.IsDefaultPort || action.UserInfo.Length > 0
            || !action.Host.Equals(AmazonPageClassifier.Host, StringComparison.OrdinalIgnoreCase)
            || !AnswerPaths.Contains(action.AbsolutePath, StringComparer.Ordinal))
        {
            return null;
        }

        var fields = form.QuerySelectorAll(HiddenField)
            .Select(f => new KeyValuePair<string, string>(f.GetAttribute("name")!, f.GetAttribute("value") ?? ""))
            .ToList();
        if (fields.Count > MaxFields || fields.Any(f => f.Key.Length > 64 || f.Value.Length > 2048)) return null;

        var text = form.QuerySelectorAll(TextField).ToList();
        if (text.Count == 0) return new AmazonRobotCheck(action, fields, AnswerField: null, Picture: null);
        if (text.Count > 1) return null;

        // Characters to type need a picture to read them from, and it must be Amazon's.
        var source = (form.QuerySelector(PictureImage) ?? document.QuerySelector(PictureImage))?.GetAttribute("src");
        if (!Uri.TryCreate(pageUrl, source, out var picture)
            || picture.Scheme != Uri.UriSchemeHttps || !picture.IsDefaultPort || picture.UserInfo.Length > 0
            || !AmazonClient.ImageHosts.Contains(picture.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }
        return new AmazonRobotCheck(action, fields, text[0].GetAttribute("name")!, picture);
    }
}
