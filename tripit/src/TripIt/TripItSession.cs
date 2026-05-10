using System.Text.Json;
using System.Text.Json.Serialization;
using TripIt.Auth;

namespace TripIt;

/// <summary>
/// Holds the credentials needed to call the TripIt web SPA backend:
/// the Cookie header and the X-CSRF-Token-WA header value.
///
/// Loads from (in order of priority):
///   1. an injected <see cref="ISessionStore"/> (e.g. user-secrets, DPAPI), if it has a session
///   2. environment variables: TRIPIT_COOKIE, TRIPIT_CSRF
///   3. file pointed to by env var TRIPIT_SESSION_FILE
///   4. %USERPROFILE%/.tripit-mcp/session.json on Windows,
///      ~/.tripit-mcp/session.json elsewhere.
/// </summary>
public sealed class TripItSession
{
    private readonly ISessionStore? _store;

    public string Cookie { get; private set; } = "";
    public string CsrfToken { get; private set; } = "";
    public string? Source { get; private set; }

    public TripItSession() : this(null) { }

    public TripItSession(ISessionStore? store)
    {
        _store = store;
        Reload();
    }

    public void Reload()
    {
        if (_store is not null && _store.TryLoad(out var stored))
        {
            Cookie = stored.Cookie;
            CsrfToken = stored.CsrfToken;
            Source = _store.Description;
            return;
        }

        var envCookie = Environment.GetEnvironmentVariable("TRIPIT_COOKIE");
        var envCsrf   = Environment.GetEnvironmentVariable("TRIPIT_CSRF");
        if (!string.IsNullOrWhiteSpace(envCookie))
        {
            Cookie = envCookie!;
            CsrfToken = envCsrf ?? "";
            Source = "environment";
            return;
        }

        var path = Environment.GetEnvironmentVariable("TRIPIT_SESSION_FILE")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                   ".tripit-mcp", "session.json");

        if (File.Exists(path))
        {
            try
            {
                var data = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data is not null)
                {
                    Cookie = data.Cookie ?? "";
                    CsrfToken = data.CsrfToken ?? "";
                    Source = path;
                }
            }
            catch
            {
                // ignore malformed
            }
        }
    }

    /// <summary>
    /// Replaces the in-memory session and, if a session store is attached, persists to it.
    /// </summary>
    public void Save(SessionCredentials credentials)
    {
        Cookie = credentials.Cookie;
        CsrfToken = credentials.CsrfToken;
        if (_store is not null)
        {
            _store.Save(credentials);
            Source = _store.Description;
        }
        else
        {
            Source = "in-memory";
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Cookie);

    private sealed class SessionFile
    {
        [JsonPropertyName("cookie")]      public string? Cookie { get; set; }
        [JsonPropertyName("csrf_token")]  public string? CsrfToken { get; set; }
    }
}
