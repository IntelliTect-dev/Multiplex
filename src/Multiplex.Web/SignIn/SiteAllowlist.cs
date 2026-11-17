using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// Which site accounts may connect: <c>Multiplex:Sites:{slug}:AllowedAccounts</c>, a list of
/// email addresses compared case-insensitively. Missing or empty means nobody (fail closed).
/// </summary>
public sealed class SiteAllowlist(IConfiguration config)
{
    public IReadOnlySet<string> AllowedEmails(string slug)
    {
        var section = config.GetSection($"Multiplex:Sites:{slug}:AllowedAccounts");
        // An array in JSON, and/or one comma/semicolon separated string (handy in an environment variable).
        var values = (section.Value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Concat(section.GetChildren().Select(c => c.Value ?? ""));
        return values.Select(v => v.Trim()).Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(SiteAccount account)
    {
        var allowed = AllowedEmails(account.Site);
        return allowed.Count > 0 && account.Emails.Any(e => allowed.Contains(e.Trim()));
    }
}
