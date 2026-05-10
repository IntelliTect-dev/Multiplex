using TripIt;
using TripIt.Auth;

namespace TripIt.FunctionalTests;

/// <summary>
/// Resolves a usable <see cref="TripItClient"/> for functional tests, or marks
/// <see cref="HasSession"/> false when no credentials are available so tests can skip.
///
/// Resolution order:
///   1. TRIPIT_COOKIE (+ optional TRIPIT_CSRF) environment variables
///   2. UserSecretsSessionStore
///   3. Default file-based session at ~/.tripit-mcp/session.json
/// </summary>
public sealed class TripItSessionFixture : IDisposable
{
    public TripItSession Session { get; }
    public TripItClient Client { get; }
    public bool HasSession => Session.IsConfigured;

    public TripItSessionFixture()
    {
        Session = new TripItSession(new UserSecretsSessionStore());
        Client = new TripItClient(Session);
    }

    public void Dispose()
    {
        Client.Dispose();
    }
}
