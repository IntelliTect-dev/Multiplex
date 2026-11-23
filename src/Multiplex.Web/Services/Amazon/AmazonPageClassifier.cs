using System.Net;

namespace Multiplex.Web.Services.Amazon;

/// <summary>What an answer from Amazon is, before anything is read from it.</summary>
internal enum AmazonAnswer
{
    /// <summary>A normal page to parse.</summary>
    Page,
    NotFound,
    /// <summary>A check a person answers through a form: "type the characters", or "click to continue shopping".</summary>
    RobotCheck,
    /// <summary>The page that says automated access needs Amazon's permission.</summary>
    AutomatedAccessRefused,
    /// <summary>A check that needs a script to run in a browser: the firewall's, or check wording with no form.</summary>
    Challenge,
    SignInWall,
    /// <summary>A redirect to another product page on www.amazon.com.</summary>
    Redirect,
    /// <summary>A redirect anywhere else, or a refusal with no better name.</summary>
    Blocked,
    Throttled,
    Unavailable,
}

/// <summary>
/// Sorts Amazon's answers. The status alone is not enough: a robot check can arrive with a 200,
/// so the markers are looked for on every answer.
/// </summary>
internal static partial class AmazonPageClassifier
{
    public const string Host = "www.amazon.com";
    /// <summary>Amazon's check forms post to <c>/errors/validateCaptcha</c> or <c>/errors_page/validateCaptcha</c>.</summary>
    internal const string CheckFormMarker = "validateCaptcha";
    /// <summary>
    /// Amazon's refusal names this address. It is also in a comment on its check pages and its
    /// 404 page, so it only means a refusal when the answer is neither of those.
    /// </summary>
    internal const string AutomatedAccessMarker = "api-services-support@amazon.com";
    internal static readonly string[] CheckWords =
        ["Enter the characters you see below", "make sure you're not a robot"];
    internal const string WafHeader = "x-amzn-waf-action";
    /// <summary>The longest a 200 page may be and still be judged a refusal or a check by its wording. The real check page is 4 KB.</summary>
    internal const int InterstitialMaxLength = 32 * 1024;

    [System.Text.RegularExpressions.GeneratedRegex("""<form\b[^>]{0,500}\baction\s*=\s*["'][^"']{0,300}validateCaptcha""", System.Text.RegularExpressions.RegexOptions.IgnoreCase, 1000)]
    private static partial System.Text.RegularExpressions.Regex CheckForm();

    public static AmazonAnswer Classify(HttpStatusCode status, IEnumerable<string> headerNames, Uri requestUrl,
        Uri? location, string body)
    {
        var code = (int)status;
        if (code is >= 300 and < 400 && location is not null)
        {
            return ClassifyRedirect(Resolve(requestUrl, location));
        }

        // A check a person can answer is a form: "type the characters", or "click to continue shopping".
        // Only a real form tag counts: the words alone can turn up in a review or a description.
        if (CheckForm().IsMatch(body)) return AmazonAnswer.RobotCheck;
        if (code == 404) return AmazonAnswer.NotFound;
        // A real page is a megabyte or more and may quote any of these phrases in a review. Amazon's
        // refusal and check pages are a few kilobytes, so on a 200 only a short page is judged by them.
        if (code != 200 || body.Length <= InterstitialMaxLength)
        {
            if (body.Contains(AutomatedAccessMarker, StringComparison.OrdinalIgnoreCase)) return AmazonAnswer.AutomatedAccessRefused;
            // Check wording with no form to answer: a check only a browser's script can pass.
            if (CheckWords.Any(w => body.Contains(w, StringComparison.OrdinalIgnoreCase))) return AmazonAnswer.Challenge;
        }
        if (headerNames.Any(h => h.Equals(WafHeader, StringComparison.OrdinalIgnoreCase))) return AmazonAnswer.Challenge;

        return code switch
        {
            200 => AmazonAnswer.Page,
            429 => AmazonAnswer.Throttled,
            >= 500 => AmazonAnswer.Unavailable,
            // A redirect without a Location, or a status Amazon has no reason to send a reader.
            _ => AmazonAnswer.Blocked,
        };
    }

    public static Uri Resolve(Uri requestUrl, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(requestUrl, location);

    private static AmazonAnswer ClassifyRedirect(Uri target)
    {
        if (target.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            if (target.AbsolutePath.StartsWith("/ap/signin", StringComparison.OrdinalIgnoreCase)) return AmazonAnswer.SignInWall;
            if (target.AbsolutePath.Contains(CheckFormMarker, StringComparison.OrdinalIgnoreCase)) return AmazonAnswer.RobotCheck;
            if (target.Scheme == Uri.UriSchemeHttps && target.IsDefaultPort && target.UserInfo.Length == 0
                && IsProductPath(target.AbsolutePath))
            {
                return AmazonAnswer.Redirect;
            }
        }
        return AmazonAnswer.Blocked;
    }

    /// <summary>Whether <paramref name="path"/> is a product page: <c>/dp/…</c>, <c>/gp/product/…</c> or <c>/{slug}/dp/…</c>.</summary>
    public static bool IsProductPath(string path) =>
        path.StartsWith("/dp/", StringComparison.Ordinal)
        || path.StartsWith("/gp/product/", StringComparison.Ordinal)
        || (path.IndexOf("/dp/", StringComparison.Ordinal) is > 0 and var i && path.IndexOf('/', 1) == i);
}
