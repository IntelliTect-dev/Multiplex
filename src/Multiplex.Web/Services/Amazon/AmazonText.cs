using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Multiplex.Web.Services.Amazon;

/// <summary>Reading numbers and tidy text out of Amazon's markup. A value that can't be read exactly is null, never a guess.</summary>
internal static partial class AmazonText
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\$\s*([\d,]+(?:\.\d{1,2})?)$")]
    private static partial Regex Dollars();

    [GeneratedRegex(@"^(\d(?:\.\d)?) out of 5")]
    private static partial Regex Stars();

    [GeneratedRegex(@"([\d,]+)")]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"^([\d.]+)([KM]?)\+ bought in past month", RegexOptions.IgnoreCase)]
    private static partial Regex Bought();

    [GeneratedRegex(@"\(\s*(\$[\d.,]+)\s*(?:\$[\d.,]+)?\s*/\s*([^)]+?)\s*\)")]
    private static partial Regex PerUnit();

    /// <summary>The page as a document, without the scripts and styles whose text would otherwise read as content.</summary>
    public static AngleSharp.Html.Dom.IHtmlDocument ParseWithoutScripts(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script, style, noscript, template").ToList()) element.Remove();
        return document;
    }

    /// <summary>The element's text with runs of whitespace (and Amazon's direction marks) collapsed; null when empty.</summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = Whitespace().Replace(text.Replace("‎", "").Replace("‏", "").Replace("​", ""), " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public static string? Text(this IElement? element) => Clean(element?.TextContent);

    public static string? TextOf(this IParentNode parent, string selector) => parent.QuerySelector(selector).Text();

    /// <summary>"$7.99" or "$1,299.00" as dollars.</summary>
    public static decimal? Price(string? text)
    {
        if (Clean(text) is not { } t || !Dollars().IsMatch(t)) return null;
        return decimal.Parse(Dollars().Match(t).Groups[1].Value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    /// <summary>"4.7 out of 5 stars…" as 4.7.</summary>
    public static double? Rating(string? text) =>
        Clean(text) is { } t && Stars().Match(t) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>The first whole number in the text: "91,951 ratings" and "(91,951)" are 91951. A shortened "(91.9K)" is not exact, so it is null.</summary>
    public static int? Count(string? text)
    {
        if (Clean(text) is not { } t || t.Contains('K') || t.Contains('M') || t.Contains('.')) return null;
        return FirstNumber().Match(t) is { Success: true } m
               && int.TryParse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>"10K+ bought in past month" as (10000, "10K+").</summary>
    public static (int? Number, string? Label) BoughtInPastMonth(string? text)
    {
        if (Clean(text) is not { } t || Bought().Match(t) is not { Success: true } m) return (null, null);
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var scale = m.Groups[2].Value.ToUpperInvariant() switch { "K" => 1_000, "M" => 1_000_000, _ => 1 };
        return ((int)Math.Round(value * scale), $"{m.Groups[1].Value}{m.Groups[2].Value.ToUpperInvariant()}+");
    }

    /// <summary>"($4.00$4.00/count)" as "$4.00/count".</summary>
    public static string? UnitPrice(string? text) =>
        Clean(text) is { } t && PerUnit().Match(t) is { Success: true } m ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
}
