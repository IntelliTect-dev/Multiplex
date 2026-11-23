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
internal static class AmazonPageClassifier
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

    public static AmazonAnswer Classify(HttpStatusCode status, IEnumerable<string> headerNames, Uri requestUrl,
        Uri? location, string body)
    {
        var code = (int)status;
        if (code is >= 300 and < 400 && location is not null)
        {
            return ClassifyRedirect(Resolve(requestUrl, location));
        }

        // A check a person can answer is a form: "type the characters", or "click to continue shopping".
        if (body.Contains(CheckFormMarker, StringComparison.OrdinalIgnoreCase)) return AmazonAnswer.RobotCheck;
        if (code == 404) return AmazonAnswer.NotFound;
        if (body.Contains(AutomatedAccessMarker, StringComparison.OrdinalIgnoreCase)) return AmazonAnswer.AutomatedAccessRefused;
        // Check wording with no form to answer, or the firewall's header: a check only a browser's script can pass.
        if (CheckWords.Any(w => body.Contains(w, StringComparison.OrdinalIgnoreCase))) return AmazonAnswer.Challenge;
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
            if (target.Scheme == Uri.UriSchemeHttps && IsProductPath(target.AbsolutePath)) return AmazonAnswer.Redirect;
        }
        return AmazonAnswer.Blocked;
    }

    /// <summary>Whether <paramref name="path"/> is a product page: <c>/dp/…</c>, <c>/gp/product/…</c> or <c>/{slug}/dp/…</c>.</summary>
    public static bool IsProductPath(string path) =>
        path.StartsWith("/dp/", StringComparison.Ordinal)
        || path.StartsWith("/gp/product/", StringComparison.Ordinal)
        || (path.IndexOf("/dp/", StringComparison.Ordinal) is > 0 and var i && path.IndexOf('/', 1) == i);
}
