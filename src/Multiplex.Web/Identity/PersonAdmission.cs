using System.Security.Cryptography;
using System.Text;
using Multiplex.Web.Auth.Store;

namespace Multiplex.Web.Identity;

/// <summary>Who a sign-in provider says the person is, in provider-neutral terms.</summary>
/// <param name="Provider">The provider, e.g. <c>microsoft</c>.</param>
/// <param name="Subject">The provider's immutable key for the account (for Microsoft <c>{tid}/{oid}</c>).</param>
/// <param name="Email">The email address the provider reports, if any.</param>
/// <param name="EmailVerified">Whether the provider vouches that the address is verified.</param>
public sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified);

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
/// The email allowlist: <c>Multiplex:Identity:AllowedEmails</c>, set by the owner in the host's
/// configuration. Either a list (JSON array) or one string of addresses separated by semicolons,
/// commas or spaces (the easy form for an environment variable). Read on every use, so an edit
/// applies as soon as the configuration reloads. An empty or missing list lets no one in.
/// </summary>
public sealed class AllowedPeople(IConfiguration configuration)
{
    public const string Key = "Multiplex:Identity:AllowedEmails";

    private static readonly char[] Separators = [';', ',', ' ', '\t', '\r', '\n'];

    public IReadOnlySet<string> Current()
    {
        var section = configuration.GetSection(Key);
        IEnumerable<string?> values = section.Value is { } single ? [single] : section.GetChildren().Select(c => c.Value);
        return values
            .SelectMany(v => (v ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Normalize)
            .Where(e => e.Contains('@'))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
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
            if (!list.Contains(email)) return Refuse(NotAllowedMessage, "not on the allowlist");
            if (s.People.ContainsKey(email)) return Refuse(NotAllowedMessage, "the address is bound to a different account");

            var binding = new PersonBinding(email, identity.Provider, identity.Subject, OwnerFor(identity.Provider, identity.Subject), now);
            s.People[email] = binding;
            return StoreWrite.Changed<(Admission, string)>(
                (new Admission.Admitted(new MultiplexPerson(binding.Owner, email)), "bound on first sign-in"));
        }, ct);

        if (admission is Admission.Admitted admitted) LogAdmitted(logger, admitted.Person.Email, identity.Provider, outcome);
        else LogRefused(logger, email ?? "(none)", identity.Provider, outcome);
        return admission;
    }

    /// <summary>Whether <paramref name="owner"/> may still use the server: their address is bound to them and still listed.</summary>
    public async Task<bool> IsAllowedAsync(string owner, CancellationToken ct)
    {
        var list = allowed.Current();
        return await store.ReadAsync(s => Allows(s, owner, list), ct);
    }

    /// <summary>The same check inside a store read or write, for callers that already hold the state.</summary>
    public static bool Allows(AuthState state, string owner, IReadOnlySet<string> allowedEmails) =>
        state.People.Values.Any(b => b.Owner == owner && allowedEmails.Contains(b.Email));

    /// <summary>
    /// The Multiplex user id of a provider account: a hash of provider and subject, so it is stable even if
    /// the store is lost, and safe as a folder name (<c>u</c> and 32 lower-case hex digits).
    /// </summary>
    public static string OwnerFor(string provider, string subject) =>
        "u" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + subject)))[..32];

    private static int DropUnlisted(AuthState state, IReadOnlySet<string> list)
    {
        var unlisted = state.People.Keys.Where(email => !list.Contains(email)).ToList();
        foreach (var email in unlisted) state.People.Remove(email);
        return unlisted.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in {Email} with {Provider} ({Outcome}).")]
    private static partial void LogAdmitted(ILogger logger, string email, string provider, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a {Provider} sign-in for {Email}: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string email, string provider, string reason);
}
