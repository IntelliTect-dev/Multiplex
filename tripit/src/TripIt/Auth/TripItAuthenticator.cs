using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripIt.Auth;

/// <summary>
/// Handles initial TripIt sign-in, always in a real browser: <see cref="BrowserLoginAsync"/>
/// spawns <c>node tools/extract-session.js</c> and reads the resulting <c>session.json</c>.
/// The library never takes the TripIt password, and never signs in from an HTTP client posing
/// as a browser (an earlier password form post did, with a fixed Chrome User-Agent and an
/// Akamai warm-up request; it was removed on 2026-11-17).
/// </summary>
public sealed class TripItAuthenticator
{
    private readonly ILogger<TripItAuthenticator> _log;

    public TripItAuthenticator(ILogger<TripItAuthenticator>? log = null)
    {
        _log = log ?? NullLogger<TripItAuthenticator>.Instance;
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
