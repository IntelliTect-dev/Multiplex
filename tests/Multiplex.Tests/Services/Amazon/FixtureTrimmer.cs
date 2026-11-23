using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Multiplex.Web.Services.Amazon;

namespace Multiplex.Tests.Services.Amazon;

/// <summary>
/// Cuts a whole recorded Amazon page down to a fixture that may be committed to a public
/// repository: only the parts the page class reads (its own <c>FixtureRoots</c>), only the
/// attributes and class names its selectors use, Amazon's prose replaced with placeholders, and
/// no scripts, styles or per-visitor ids.
/// </summary>
internal static partial class FixtureTrimmer
{
    public const int MaxTextLength = 200;

    private static readonly HashSet<string> KeptAttributes =
    [
        "id", "class", "href", "src", "title", "aria-label", "value", "type", "name", "data-asin",
        "data-component-type", "data-cy", "data-testid", "data-a-strike", "offer-display-feature-name", "role",
    ];

    [GeneratedRegex(@"\.([A-Za-z_][\w-]*)")]
    private static partial Regex ClassToken();

    public static string TrimSearch(string html) =>
        Trim(html, typeof(AmazonSearchPage), AmazonSearchPage.FixtureRoots, [], keepGallery: false);

    public static string TrimProduct(string html) =>
        Trim(html, typeof(AmazonProductPage), AmazonProductPage.FixtureRoots, AmazonProductPage.FixtureProse, keepGallery: true);

    private static string Trim(string html, Type pageClass, string[] roots, string[] prose, bool keepGallery)
    {
        var classes = pageClass.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .SelectMany(f => ClassToken().Matches((string)f.GetRawConstantValue()!).Select(m => m.Groups[1].Value))
            .ToHashSet();

        var document = new HtmlParser().ParseDocument(html);
        foreach (var junk in document.QuerySelectorAll("script, style, noscript, svg, link, meta, iframe, template").ToList())
            junk.Remove();
        foreach (var comment in document.Descendants<IComment>().ToList()) comment.Remove();

        var kept = new List<IElement>();
        foreach (var element in roots.SelectMany(r => document.QuerySelectorAll(r)).Distinct())
        {
            if (!kept.Any(k => k.Contains(element))) kept.Add(element);
        }
        kept = kept.Where(e => !kept.Any(k => k != e && k.Contains(e))).ToList();

        var placeholder = 0;
        foreach (var element in prose.SelectMany(p => document.QuerySelectorAll(p)).Distinct().ToList())
        {
            if (!string.IsNullOrWhiteSpace(element.TextContent)) element.TextContent = $"Placeholder text {++placeholder}.";
        }
        // A review quote sits beside its "Read more" link.
        foreach (var link in document.QuerySelectorAll(AmazonProductPage.SayReadMore).ToList())
        {
            if (link.ParentElement is not { } parent) continue;
            foreach (var text in parent.ChildNodes.OfType<IText>().ToList()) text.TextContent = $"\"Placeholder quote {++placeholder}.\" ";
            foreach (var sibling in parent.Children.Where(c => c != link).ToList()) sibling.TextContent = $"\"Placeholder quote {++placeholder}.\" ";
        }

        var body = new System.Text.StringBuilder("<!doctype html>\n<html><body>\n");
        foreach (var element in kept)
        {
            Clean(element, classes);
            body.Append(element.OuterHtml).Append('\n');
        }
        if (keepGallery && Gallery(html) is { } gallery) body.Append("<script>\n").Append(gallery).Append("\n</script>\n");
        body.Append("</body></html>\n");
        return Whitespace().Replace(body.ToString(), " ").Replace("> <", ">\n<");
    }

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    private static void Clean(IElement root, HashSet<string> classes)
    {
        // Forms and their hidden fields carry per-visit tokens (anti-CSRF, offer ids, query ids), and no page class reads them.
        foreach (var field in root.QuerySelectorAll("form, input, select, textarea, button").ToList()) field.Remove();
        foreach (var element in root.QuerySelectorAll("*").Prepend(root).ToList())
        {
            foreach (var attribute in element.Attributes.ToList())
            {
                if (!KeptAttributes.Contains(attribute.Name)) { element.RemoveAttribute(attribute.Name); continue; }
                switch (attribute.Name)
                {
                    case "class":
                        var tokens = attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(classes.Contains).ToList();
                        if (tokens.Count == 0) element.RemoveAttribute("class");
                        else element.SetAttribute("class", string.Join(' ', tokens));
                        break;
                    case "href":
                        // Links carry tracking and per-visitor query strings; a fixture needs only the path.
                        var path = attribute.Value.Split('?', '#')[0];
                        element.SetAttribute("href", path.Length > 60 ? path[..60] : path);
                        break;
                    case "src" when !(element.LocalName == "img" && element.ClassList.Contains("s-image")):
                        element.RemoveAttribute("src");
                        break;
                    case "id" when attribute.Value.Length > 80:
                        element.RemoveAttribute("id");
                        break;
                }
            }
        }
        foreach (var text in root.Descendants<IText>().ToList())
        {
            if (text.TextContent.Length > MaxTextLength) text.TextContent = text.TextContent[..MaxTextLength];
        }
        // Drop what is left empty: no text, no attribute a selector could match, no children.
        bool removed;
        do
        {
            removed = false;
            foreach (var element in root.QuerySelectorAll("*").ToList())
            {
                if (element.ChildElementCount == 0 && string.IsNullOrWhiteSpace(element.TextContent)
                    && element.Attributes.Length == 0 && element.LocalName is not ("br" or "td" or "th"))
                {
                    element.Remove();
                    removed = true;
                }
            }
        } while (removed);
    }

    /// <summary>The gallery script line, cut to its first two pictures.</summary>
    private static string? Gallery(string html)
    {
        var (images, main) = AmazonProductPage.Gallery(html);
        if (images.Count == 0) return null;
        var items = images.Take(2).Select((url, i) => new Dictionary<string, string?>
        {
            ["hiRes"] = url,
            ["large"] = i == 0 ? main : null,
        });
        return $"{AmazonProductPage.GalleryMarker}: {{ 'initial': {JsonSerializer.Serialize(items)} }}";
    }
}
