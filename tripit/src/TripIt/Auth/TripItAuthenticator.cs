using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripIt.Auth;

/// <summary>
/// Handles initial TripIt sign-in. Two strategies:
///   1. <see cref="TryCliLoginAsync"/>: best-effort HttpClient form post against the Concur
///      login flow. Often blocked by Akamai bot detection (returns
///      <see cref="LoginChallengeException"/>).
///   2. <see cref="BrowserLoginAsync"/>: spawns <c>node tools/extract-session.js</c>
///      (existing Playwright-based capture) and reads the resulting <c>session.json</c>.
/// </summary>
public sealed class TripItAuthenticator
{
    private readonly ILogger<TripItAuthenticator> _log;

    public TripItAuthenticator(ILogger<TripItAuthenticator>? log = null)
    {
        _log = log ?? NullLogger<TripItAuthenticator>.Instance;
    }

    /// <summary>
    /// Best-effort credential flow using HttpClient. Akamai's bot detection routinely fails
    /// this; callers should fall back to <see cref="BrowserLoginAsync"/> on
    /// <see cref="LoginChallengeException"/>.
    /// </summary>
    public async Task<SessionCredentials> TryCliLoginAsync(
        string userName,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("userName required", nameof(userName));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("password required", nameof(password));

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        // 1. GET the home page so Akamai sets _abck / bm_sz cookies.
        _log.LogDebug("CLI login: GET https://www.tripit.com/");
        var homeResp = await http.GetAsync("https://www.tripit.com/", ct).ConfigureAwait(false);
        var homeBody = await homeResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (LooksLikeAkamaiChallenge(homeBody, cookies))
        {
            throw new LoginChallengeException(
                "Akamai bot challenge detected on the TripIt home page. " +
                "CLI login is unavailable; use browser-based capture instead.");
        }

        // 2. POST credentials. TripIt currently delegates to Concur SSO; the form action and
        //    fields are not stable enough to fully script in HttpClient. We attempt a generic
        //    application/x-www-form-urlencoded POST and surface any redirect that does NOT
        //    leave us with a session cookie as a challenge.
        var loginUrl = "https://www.tripit.com/account/login";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = userName,
            ["password"] = password,
        });
        _log.LogDebug("CLI login: POST {Url}", loginUrl);
        using var loginResp = await http.PostAsync(loginUrl, form, ct).ConfigureAwait(false);
        var loginBody = await loginResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (LooksLikeAkamaiChallenge(loginBody, cookies))
        {
            throw new LoginChallengeException(
                "Akamai bot challenge detected during sign-in. Use browser-based capture instead.");
        }

        var cookieHeader = SerializeCookies(cookies, "https://www.tripit.com");
        if (!cookieHeader.Contains("JSESSIONID", StringComparison.Ordinal) &&
            !cookieHeader.Contains("tripit-web-id", StringComparison.OrdinalIgnoreCase))
        {
            throw new LoginChallengeException(
                "Login did not yield a TripIt session cookie. Use browser-based capture instead.");
        }

        // CSRF token is only available after an authenticated XHR; CLI flow can't fetch it
        // reliably, so we leave it blank and let the caller upgrade via browser flow if needed.
        return new SessionCredentials(cookieHeader, "");
    }

    /// <summary>
    /// Spawns <c>node tools/extract-session.js</c> (existing Playwright capture) and reads the
    /// resulting <c>~/.tripit-mcp/session.json</c>.
    /// </summary>
    public async Task<SessionCredentials> BrowserLoginAsync(
        string? extractScriptPath = null,
        CancellationToken ct = default)
    {
        var script = extractScriptPath ?? FindExtractScript()
            ?? throw new FileNotFoundException(
                "Could not locate tools/extract-session.js. Pass extractScriptPath explicitly.");

        _log.LogInformation("Launching browser-based login via {Script}", script);
        var psi = new ProcessStartInfo("node", $"\"{script}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start node process.");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"extract-session.js exited with code {proc.ExitCode}. stderr:\n{stderr}");
        }

        var sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".tripit-mcp", "session.json");
        if (!File.Exists(sessionPath))
        {
            throw new FileNotFoundException(
                $"extract-session.js completed but no session file was written to {sessionPath}.");
        }

        var json = await File.ReadAllTextAsync(sessionPath, ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<SessionFileShape>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("session.json was empty or unparseable.");
        if (string.IsNullOrEmpty(data.Cookie))
        {
            throw new InvalidOperationException("session.json is missing the cookie value.");
        }
        return new SessionCredentials(data.Cookie, data.CsrfToken ?? "");
    }

    private static bool LooksLikeAkamaiChallenge(string body, CookieContainer cookies)
    {
        if (body.Contains("_abck", StringComparison.Ordinal) &&
            body.Contains("bm-verify", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // _abck cookie that is empty or starts with ~-1~ indicates an unsatisfied challenge.
        var cookieHeader = cookies.GetCookieHeader(new Uri("https://www.tripit.com"));
        if (cookieHeader.Contains("_abck=~-1~", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string SerializeCookies(CookieContainer container, string url)
    {
        return container.GetCookieHeader(new Uri(url));
    }

    private static string? FindExtractScript()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tools", "extract-session.js");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private sealed class SessionFileShape
    {
        [JsonPropertyName("cookie")]      public string? Cookie { get; set; }
        [JsonPropertyName("csrf_token")]  public string? CsrfToken { get; set; }
    }
}

/// <summary>
/// Thrown by <see cref="TripItAuthenticator.TryCliLoginAsync"/> when the CLI flow is blocked
/// (typically by Akamai). Callers should fall back to browser-based capture.
/// </summary>
public sealed class LoginChallengeException : Exception
{
    public LoginChallengeException(string message) : base(message) { }
}
