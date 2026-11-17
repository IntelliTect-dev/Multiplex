using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Web.SignIn;

/// <summary>
/// Everything the generic sign-in machinery needs to know about one site: where its sign-in
/// starts, where the browser may go, and how to tell that a person is signed in.
/// Register with <c>AddSiteSignIn(...).AddSite&lt;T&gt;()</c>.
/// </summary>
public interface ISiteSignInDefinition
{
    /// <summary>The endpoint slug, e.g. <c>tripit</c>; matches <see cref="PendingAuthorization.Site"/>.</summary>
    string Slug { get; }

    /// <summary>The site's name as people know it, e.g. "TripIt".</summary>
    string DisplayName { get; }

    /// <summary>The page the live sign-in opens on.</summary>
    Uri SignInStartUrl { get; }

    /// <summary>
    /// Hosts the mirrored browser may navigate its top-level page to: the site itself plus the
    /// identity providers its own sign-in page links to. Sub-resources (scripts, images,
    /// iframes) may come from anywhere.
    /// </summary>
    IReadOnlyCollection<string> AllowedNavigationHosts { get; }

    /// <summary>A page that, loaded in a signed-in profile, renews the session's cookies.</summary>
    Uri RefreshUrl { get; }

    /// <summary>Optional one-line hint shown under the live page, e.g. "Tick Keep me signed in".</summary>
    string? SignInHint => null;

    /// <summary>
    /// The account the page's browser is signed in to, or <c>null</c> when it is not (yet).
    /// Called every couple of seconds during a live sign-in, so it must be cheap and must not
    /// navigate the page.
    /// </summary>
    Task<SiteAccount?> TryGetSignedInAccountAsync(IPage page, CancellationToken ct);

    /// <summary>
    /// Reads the signed-in session into the opaque snapshot JSON that the site's server replays
    /// (stored by <see cref="ISiteSessionStore"/>). May reload the page to observe its requests.
    /// </summary>
    Task<string> CaptureSnapshotAsync(IPage page, CancellationToken ct);

    /// <summary>
    /// True only on a positive signed-out signal (the site's login page). A block page, an
    /// error or a timeout is <em>not</em> signed out: that would revoke the account's grants.
    /// </summary>
    Task<bool> IsSignedOutAsync(IPage page);
}
