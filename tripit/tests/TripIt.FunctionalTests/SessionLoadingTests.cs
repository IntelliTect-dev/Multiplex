using System.Text.Json;
using TripIt;
using TripIt.Auth;
using Xunit;

namespace TripIt.FunctionalTests;

/// <summary>
/// Tests for <see cref="TripItSession"/> precedence rules. These do NOT hit the network.
/// </summary>
public sealed class SessionLoadingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _origCookie;
    private readonly string? _origCsrf;
    private readonly string? _origFile;

    public SessionLoadingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tripit-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _origCookie = Environment.GetEnvironmentVariable("TRIPIT_COOKIE");
        _origCsrf = Environment.GetEnvironmentVariable("TRIPIT_CSRF");
        _origFile = Environment.GetEnvironmentVariable("TRIPIT_SESSION_FILE");
        Environment.SetEnvironmentVariable("TRIPIT_COOKIE", null);
        Environment.SetEnvironmentVariable("TRIPIT_CSRF", null);
        Environment.SetEnvironmentVariable("TRIPIT_SESSION_FILE", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TRIPIT_COOKIE", _origCookie);
        Environment.SetEnvironmentVariable("TRIPIT_CSRF", _origCsrf);
        Environment.SetEnvironmentVariable("TRIPIT_SESSION_FILE", _origFile);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnvVars_take_precedence_over_file()
    {
        Environment.SetEnvironmentVariable("TRIPIT_COOKIE", "env-cookie");
        Environment.SetEnvironmentVariable("TRIPIT_CSRF", "env-csrf");

        var path = Path.Combine(_tempDir, "session.json");
        File.WriteAllText(path, """{"cookie":"file-cookie","csrf_token":"file-csrf"}""");
        Environment.SetEnvironmentVariable("TRIPIT_SESSION_FILE", path);

        var session = new TripItSession();
        Assert.Equal("env-cookie", session.Cookie);
        Assert.Equal("env-csrf", session.CsrfToken);
        Assert.Equal("environment", session.Source);
    }

    [Fact]
    public void File_used_when_env_unset()
    {
        var path = Path.Combine(_tempDir, "session.json");
        File.WriteAllText(path, """{"cookie":"file-cookie","csrf_token":"file-csrf"}""");
        Environment.SetEnvironmentVariable("TRIPIT_SESSION_FILE", path);

        var session = new TripItSession();
        Assert.Equal("file-cookie", session.Cookie);
        Assert.Equal("file-csrf", session.CsrfToken);
        Assert.Equal(path, session.Source);
    }

    [Fact]
    public void Store_takes_precedence_over_env()
    {
        Environment.SetEnvironmentVariable("TRIPIT_COOKIE", "env-cookie");

        var fakeStore = new InMemoryStore(new SessionCredentials("store-cookie", "store-csrf"));
        var session = new TripItSession(fakeStore);

        Assert.Equal("store-cookie", session.Cookie);
        Assert.Equal("store-csrf", session.CsrfToken);
        Assert.Equal("in-memory-test", session.Source);
    }

    [Fact]
    public void IsConfigured_false_when_no_source()
    {
        var session = new TripItSession();
        Assert.False(session.IsConfigured);
    }

    [Fact]
    public void Save_persists_to_store_and_updates_in_memory()
    {
        var store = new InMemoryStore(null);
        var session = new TripItSession(store);
        Assert.False(session.IsConfigured);

        session.Save(new SessionCredentials("new-cookie", "new-csrf"));

        Assert.Equal("new-cookie", session.Cookie);
        Assert.Equal("new-csrf", session.CsrfToken);
        Assert.True(store.SaveCalled);
    }

    private sealed class InMemoryStore : ISessionStore
    {
        private SessionCredentials? _creds;
        public bool SaveCalled { get; private set; }
        public InMemoryStore(SessionCredentials? initial) => _creds = initial;
        public string Description => "in-memory-test";
        public bool TryLoad(out SessionCredentials credentials)
        {
            credentials = _creds ?? new SessionCredentials("", "");
            return _creds is not null;
        }
        public void Save(SessionCredentials credentials)
        {
            _creds = credentials;
            SaveCalled = true;
        }
        public void Clear() => _creds = null;
    }
}

/// <summary>
/// Tests that DTO models round-trip through JSON without losing unknown fields.
/// </summary>
public sealed class DtoRoundTripTests
{
    [Fact]
    public void Profile_preserves_unknown_fields_via_extension_data()
    {
        const string json = """
        {
          "display_name": "Alice",
          "company": "Acme",
          "future_field_not_modeled": "preserved"
        }
        """;
        var profile = JsonSerializer.Deserialize<TripIt.Models.Profile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("Alice", profile.DisplayName);
        Assert.True(profile.AdditionalData.ContainsKey("future_field_not_modeled"));

        var roundTripped = JsonSerializer.Serialize(profile);
        Assert.Contains("future_field_not_modeled", roundTripped);
        Assert.Contains("preserved", roundTripped);
    }

    [Fact]
    public void ListResponse_Items_finds_collection_in_extension_data()
    {
        const string json = """
        {
          "timestamp": "1234",
          "Trip": [ { "id": "1", "display_name": "Trip A" } ]
        }
        """;
        var resp = JsonSerializer.Deserialize<TripIt.Models.ListResponse<TripIt.Models.Trip>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Single(resp.Items);
        Assert.Equal("Trip A", resp.Items[0].DisplayName);
    }
}
