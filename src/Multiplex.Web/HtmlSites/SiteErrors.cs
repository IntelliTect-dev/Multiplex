namespace Multiplex.Web.HtmlSites;

/// <summary>
/// A failure reading a website's pages whose message is written for the person: a tool shows it
/// as its error text.
/// </summary>
public class SiteException(string message) : Exception(message);

/// <summary>The site refused the request as automated, or the service is paused after such a refusal.</summary>
public sealed class SiteBlockedException(string message) : SiteException(message);

/// <summary>The site wants a person to answer a robot check; <see cref="Url"/> is where they do it.</summary>
public sealed class SiteHandOverException(string message, Uri url, DateTimeOffset expiresAt) : SiteException(message)
{
    public Uri Url { get; } = url;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

/// <summary>The page no longer has the element the service reads; <see cref="Missing"/> names it.</summary>
public sealed class SiteLayoutChangedException(string site, string missing)
    : SiteException($"{site}'s page layout has changed: {missing} was not found. The {site} service needs updating.")
{
    public string Missing { get; } = missing;
}

/// <summary>The request was not sent: the queue, a volume cap, the call's deadline or robots.txt stopped it.</summary>
public sealed class SiteBusyException(string message) : SiteException(message);

/// <summary>The site has no such page.</summary>
public sealed class SiteNotFoundException(string message) : SiteException(message);

/// <summary>The site answered with an error that is not a block (throttling, a server error).</summary>
public sealed class SiteUnavailableException(string message) : SiteException(message);
