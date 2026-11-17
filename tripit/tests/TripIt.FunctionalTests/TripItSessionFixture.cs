using Xunit;

namespace TripIt.FunctionalTests;

/// <summary>
/// The live TripIt client for functional tests, guarded so it can only ever act on the
/// dedicated test account.
/// <list type="bullet">
///   <item>Loads ONLY the session snapshot file named by <c>TRIPIT_TEST_SESSION_FILE</c>: no user
///   secrets, no <c>TRIPIT_COOKIE</c>, no <c>~/.tripit-mcp/session.json</c>, and no refresher, so a
///   rejected session can never be replaced by another one (such as the owner's real account).</item>
///   <item>Unset → <see cref="HasSession"/> is false and the tests skip.</item>
///   <item>Set → <c>TRIPIT_TEST_ACCOUNT</c> is required, and one of the signed-in profile's email
///   addresses must equal it. Anything else throws, so every test in the class FAILS loudly
///   instead of running against the wrong account.</item>
/// </list>
/// Write tests additionally need <c>TRIPIT_ALLOW_WRITES=1</c>.
/// </summary>
public sealed class TripItSessionFixture : IAsyncLifetime, IDisposable
{
    public const string SessionFileVariable = "TRIPIT_TEST_SESSION_FILE";
    public const string AccountVariable = "TRIPIT_TEST_ACCOUNT";

    private TripItClient? _client;

    public bool HasSession { get; private set; }

    public TripItClient Client => _client ?? throw new InvalidOperationException("No TripIt test session is loaded.");

    public async Task InitializeAsync()
    {
        var path = Environment.GetEnvironmentVariable(SessionFileVariable);
        if (string.IsNullOrWhiteSpace(path)) return;

        var expected = Environment.GetEnvironmentVariable(AccountVariable);
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException(
                $"{SessionFileVariable} is set but {AccountVariable} is not. Name the test account the session must belong to.");
        }
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{SessionFileVariable} names a file that does not exist.");
        }

        var session = TripItSession.FromSnapshotJson(await File.ReadAllTextAsync(path), source: SessionFileVariable);
        _client = new TripItClient(new SnapshotSessionProvider(session));

        var emails = await _client.GetMyEmailAddressesAsync();
        if (!emails.Any(e => string.Equals(e.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            _client.Dispose();
            _client = null;
            // Do not echo the addresses found: they may belong to a real account.
            throw new InvalidOperationException(
                $"The session in {SessionFileVariable} is not signed in as {AccountVariable} " +
                $"({(emails.Count == 0 ? "the profile lists no email addresses" : "none of its email addresses match")}). " +
                "Refusing to run live tests against it.");
        }
        HasSession = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _client?.Dispose();
}
