using System.Security.Cryptography;
using System.Text;
using Multiplex.Web.Auth.Store;

namespace Multiplex.Web.Identity;

/// <summary>Who a sign-in provider says the person is, in provider-neutral terms.</summary>
/// <param name="Provider">The provider, e.g. <c>microsoft</c>.</param>
/// <param name="Subject">The provider's immutable key for the account (for Microsoft <c>{tid}/{oid}</c>).</param>
/// <param name="Email">The email address the provider reports, if any.</param>
/// <param name="EmailVerified">Whether the provider vouches that the address is verified.</param>
/// <param name="PersonalAccount">An account the provider keeps for an individual (a personal Microsoft account),
/// rather than one in an organization's directory whose owner verified its domain. A domain entry on the
/// allowlist never admits one (#80, N3).</param>
public sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified, bool PersonalAccount);

/// <summary>A person signed in to Multiplex.</summary>
/// <param name="Owner">Their Multiplex user id; their grants and site sessions are kept under it.</param>
/// <param name="Email">The allowlisted address they were admitted with.</param>
public sealed record MultiplexPerson(string Owner, string Email);

public abstract record Admission
{
    public sealed record Admitted(MultiplexPerson Person) : Admission;

    /// <param name="Message">Shown to the person. It never says who is on the list.</param>
    public sealed record Refused(string Message) : Admission;
}

/// <summary>
/// The allowlist as read: exact addresses, and domains whose addresses are allowed. Lower case throughout.
/// </summary>
public sealed record Allowlist(IReadOnlySet<string> Addresses, IReadOnlySet<string> Domains)
{
    public bool IsEmpty => Addresses.Count == 0 && Domains.Count == 0;

    /// <summary>
    /// Whether <paramref name="email"/> may get in: an exact entry admits any account with that address;
    /// a domain entry admits only an organization's account (#80, N3). A personal account was proved to own
    /// its address once, at sign-up, so a domain entry could otherwise let in, say, a former employee's
    /// personal account registered under the company's domain. A domain matches exactly, not its subdomains.
    /// </summary>
    public bool Admits(string email, bool personalAccount)
    {
        var address = AllowedPeople.Normalize(email);
        if (Addresses.Contains(address)) return true;
        var at = address.LastIndexOf('@');
        return !personalAccount && at > 0 && Domains.Contains(address[(at + 1)..]);
    }
}

/// <summary>
/// The allowlist: <c>Multiplex:Identity:AllowedEmails</c>, set by the owner in the host's configuration.
/// Either a list (JSON array) or one string of entries separated by semicolons, commas or spaces (the
/// easy form for an environment variable). An entry is an address (<c>pat@example.com</c>) or a domain
/// (<c>example.com</c>, <c>@example.com</c> or <c>*@example.com</c>), ignoring case. Read on every use, so
/// an edit applies as soon as the configuration reloads. An empty or missing list lets no one in.
/// </summary>
public sealed class AllowedPeople(IConfiguration configuration)
{
    public const string Key = "Multiplex:Identity:AllowedEmails";

    private static readonly char[] Separators = [';', ',', ' ', '\t', '\r', '\n'];

    public Allowlist Current()
    {
        var section = configuration.GetSection(Key);
        IEnumerable<string?> values = section.Value is { } single ? [single] : section.GetChildren().Select(c => c.Value);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var domains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values.SelectMany(v => (v ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var entry = Normalize(raw);
            var domain = entry.StartsWith("*@", StringComparison.Ordinal) ? entry[2..]
                : entry.StartsWith('@') ? entry[1..]
                : entry.Contains('@') ? null
                : entry;
            if (domain is null)
            {
                if (entry.IndexOf('@') > 0 && IsDomain(entry[(entry.LastIndexOf('@') + 1)..])) addresses.Add(entry);
            }
            else if (IsDomain(domain))
            {
                domains.Add(domain);
            }
        }
        return new Allowlist(addresses, domains);
    }

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>At least two labels of letters, digits and hyphens, e.g. <c>example.com</c>; anything else is ignored.</summary>
    private static bool IsDomain(string value) =>
        value.Split('.') is { Length: >= 2 } labels
        && labels.All(l => l.Length > 0 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
}

/// <summary>
/// Decides who gets in. Only an address on the allowlist, and only one the provider vouches is
/// verified, admits a person, and only on its first sign-in: that sign-in binds the address to the
/// provider's immutable account key. From then on the address admits only that account, so an
/// address that changes hands admits no one new. Taking an address off the list ends its binding
/// at the next sign-in; adding it back lets its next sign-in bind afresh.
/// </summary>
public sealed partial class PersonAdmission(IAuthStore store, AllowedPeople allowed, TimeProvider time, ILogger<PersonAdmission> logger)
{
    internal const string NotVerifiedMessage =
        "Microsoft did not confirm that this account's email address is verified, so it cannot be used to sign in " +
        "to this Multiplex server. Sign in with a different Microsoft account.";

    internal const string NotAllowedMessage =
        "This account is not allowed to use this Multiplex server. If it should be, ask the server's owner to add your email address.";

    public async Task<Admission> AdmitAsync(ExternalIdentity identity, CancellationToken ct)
    {
        var list = allowed.Current();
        var email = identity.Email is { Length: > 0 } given ? AllowedPeople.Normalize(given) : null;
        var now = time.GetUtcNow();

        var (admission, outcome) = await store.WriteAsync(s =>
        {
            var dropped = DropUnlisted(s, list);
            StoreWrite<(Admission, string)> Refuse(string message, string why) => new((new Admission.Refused(message), why), dropped > 0);

            var bound = s.People.Values.FirstOrDefault(b => b.Provider == identity.Provider && b.Subject == identity.Subject);
            if (bound is not null)
            {
                return new StoreWrite<(Admission, string)>(
                    (new Admission.Admitted(new MultiplexPerson(bound.Owner, bound.Email)), "already bound"), dropped > 0);
            }
            if (email is null || !identity.EmailVerified) return Refuse(NotVerifiedMessage, "no verified email address");
            if (!list.Admits(email, identity.PersonalAccount)) return Refuse(NotAllowedMessage, "not on the allowlist");
            if (s.People.ContainsKey(email)) return Refuse(NotAllowedMessage, "the address is bound to a different account");

            var binding = new PersonBinding(email, identity.Provider, identity.Subject, OwnerFor(identity.Provider, identity.Subject), now,
                identity.PersonalAccount);
            s.People[email] = binding;
            return StoreWrite.Changed<(Admission, string)>(
                (new Admission.Admitted(new MultiplexPerson(binding.Owner, email)), "bound on first sign-in"));
        }, ct);

        var kind = identity.PersonalAccount ? "personal account" : "work or school account";
        if (admission is Admission.Admitted admitted) LogAdmitted(logger, admitted.Person.Email, identity.Provider, kind, outcome);
        else LogRefused(logger, email ?? "(none)", identity.Provider, kind, outcome);
        return admission;
    }

    /// <summary>Whether <paramref name="owner"/> may still use the server: their address is bound to them and still listed.</summary>
    public async Task<bool> IsAllowedAsync(string owner, CancellationToken ct)
    {
        var list = allowed.Current();
        return await store.ReadAsync(s => Allows(s, owner, list), ct);
    }

    /// <summary>The same check inside a store read or write, for callers that already hold the state.</summary>
    public static bool Allows(AuthState state, string owner, Allowlist list) =>
        state.People.Values.Any(b => b.Owner == owner && list.Admits(b.Email, b.PersonalAccount));

    /// <summary>
    /// The Multiplex user id of a provider account: a hash of provider and subject, so it is stable even if
    /// the store is lost, and safe as a folder name (<c>u</c> and 32 lower-case hex digits).
    /// </summary>
    public static string OwnerFor(string provider, string subject) =>
        "u" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + subject)))[..32];

    /// <summary>
    /// Ends the bindings of addresses no longer on the list. An empty list (missing, mistyped, or read
    /// half-way through an edit) ends none: it already lets no one in, and dropping every binding on it
    /// would let the next holder of each address bind afresh.
    /// </summary>
    private static int DropUnlisted(AuthState state, Allowlist list)
    {
        if (list.IsEmpty) return 0;
        var unlisted = state.People.Values.Where(b => !list.Admits(b.Email, b.PersonalAccount)).Select(b => b.Email).ToList();
        foreach (var email in unlisted) state.People.Remove(email);
        return unlisted.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in {Email} with {Provider}, a {Kind} ({Outcome}).")]
    private static partial void LogAdmitted(ILogger logger, string email, string provider, string kind, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a {Provider} sign-in for {Email}, a {Kind}: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string email, string provider, string kind, string reason);
}
