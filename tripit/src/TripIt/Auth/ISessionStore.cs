namespace TripIt.Auth;

/// <summary>
/// Cookie + CSRF pair representing an authenticated TripIt session.
/// </summary>
public sealed record SessionCredentials(string Cookie, string CsrfToken);

/// <summary>
/// Persists and retrieves <see cref="SessionCredentials"/> on the local machine.
/// Implementations: <c>UserSecretsSessionStore</c>, <c>DpapiSessionStore</c>.
/// </summary>
public interface ISessionStore
{
    /// <summary>Human-readable description of where credentials are stored.</summary>
    string Description { get; }

    /// <summary>Attempts to load saved credentials. Returns false if none are present.</summary>
    bool TryLoad(out SessionCredentials credentials);

    /// <summary>Persists credentials, overwriting any existing entry.</summary>
    void Save(SessionCredentials credentials);

    /// <summary>Removes any stored credentials.</summary>
    void Clear();
}
