using System.Text.Json;

namespace Multiplex.Web.SignIn;

/// <summary>How the navigation lock answered one paused Document request.</summary>
internal enum PausedRequestOutcome
{
    /// <summary>Let through.</summary>
    Continued,

    /// <summary>Failed: it would have taken a top-level page outside the allowed hosts.</summary>
    Blocked,

    /// <summary>The decision itself went wrong; the request was failed rather than left paused.</summary>
    FailedClosed,
}

internal readonly record struct PausedRequestDecision(PausedRequestOutcome Outcome, string Url, Exception? Error = null);

/// <summary>What the second line of the lock does about a page that has committed a URL.</summary>
internal enum CommittedPageAction
{
    /// <summary>Nothing to judge yet (a new, empty page).</summary>
    Ignore,

    /// <summary>Chrome's own error page, usually our refusal of a request.</summary>
    ErrorPage,

    /// <summary>A page on an allowed host.</summary>
    Allowed,

    /// <summary>Anything else: send the page back (or close the tab).</summary>
    Blocked,
}

/// <summary>
/// The decisions behind a live sign-in's navigation lock, apart from the browser so they can be
/// tested: only http(s) pages on <see cref="ISiteSignInDefinition.AllowedNavigationHosts"/> may
/// be shown at the top level; frames and sub-resources may load from anywhere.
/// </summary>
internal static class NavigationLock
{
    public static bool IsAllowed(string url, IReadOnlyCollection<string> allowedHosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Answers one CDP <c>Fetch.requestPaused</c> event through <paramref name="send"/>:
    /// <c>Fetch.continueRequest</c>, or <c>Fetch.failRequest</c> for a top-level navigation
    /// outside the allowed hosts. Fails closed: a request without a frame id is judged as
    /// top-level (<paramref name="isTopLevelFrame"/> gets null), and one the lock cannot judge
    /// is failed rather than left paused or let through. Never throws.
    /// </summary>
    public static async Task<PausedRequestDecision> DecideAsync(
        JsonElement paused,
        IReadOnlyCollection<string> allowedHosts,
        Func<string?, Task<bool>> isTopLevelFrame,
        Func<string, Dictionary<string, object>, Task> send)
    {
        string? requestId = null;
        var url = "";
        try
        {
            requestId = paused.GetProperty("requestId").GetString();
            if (string.IsNullOrEmpty(requestId)) throw new InvalidOperationException("Paused request without a requestId.");
            url = paused.GetProperty("request").GetProperty("url").GetString() ?? "";
            var frameId = paused.TryGetProperty("frameId", out var frame) && frame.ValueKind == JsonValueKind.String ? frame.GetString() : null;
            if (!IsAllowed(url, allowedHosts) && (string.IsNullOrEmpty(frameId) || await isTopLevelFrame(frameId)))
            {
                await send("Fetch.failRequest", Fail(requestId));
                return new(PausedRequestOutcome.Blocked, url);
            }
            await send("Fetch.continueRequest", new Dictionary<string, object> { ["requestId"] = requestId });
            return new(PausedRequestOutcome.Continued, url);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(requestId))
            {
                // The request may be gone already (tab closed, browser stopping); then there is nothing left to refuse.
                try { await send("Fetch.failRequest", Fail(requestId)); }
                catch (Exception) { }
            }
            return new(PausedRequestOutcome.FailedClosed, url, ex);
        }
    }

    /// <summary>
    /// What to do about a page whose main frame has just committed <paramref name="url"/>.
    /// Only an empty page and Chrome's error page are exempt; every other scheme (file:,
    /// chrome:, view-source:, data:, javascript:, blob: …) is judged like a foreign host.
    /// </summary>
    public static CommittedPageAction AfterNavigation(string url, IReadOnlyCollection<string> allowedHosts)
    {
        if (url is "" or "about:blank") return CommittedPageAction.Ignore;
        if (url.StartsWith("chrome-error:", StringComparison.Ordinal)) return CommittedPageAction.ErrorPage;
        return IsAllowed(url, allowedHosts) ? CommittedPageAction.Allowed : CommittedPageAction.Blocked;
    }

    private static Dictionary<string, object> Fail(string requestId) => new()
    {
        ["requestId"] = requestId,
        ["errorReason"] = "BlockedByClient",
    };
}
