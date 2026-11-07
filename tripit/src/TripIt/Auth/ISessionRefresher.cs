using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TripIt;

/// <summary>
/// Obtains a fresh session when the current one is rejected. The TripIt session's Akamai
/// bot cookies last about two hours, so a long-running consumer (the MCP server) needs a
/// way to re-read them from the still-signed-in browser without user involvement.
/// </summary>
public interface ISessionRefresher
{
    /// <summary>Refreshes the persisted session; the client re-reads it afterwards.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>
/// Refreshes by running a shell command — normally <c>node tools/extract-session.js</c>,
/// which re-reads cookies from the signed-in browser profile into
/// <c>~/.tripit-mcp/session.json</c>.
///
/// Configure with <c>TRIPIT_SESSION_REFRESH_COMMAND</c>; when unset, <see cref="FromEnvironment"/>
/// looks for <c>tools/extract-session.js</c> beside or above the running assembly.
/// </summary>
public sealed class CommandSessionRefresher : ISessionRefresher
{
    private readonly string _command;
    private readonly string? _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly ILogger _log;

    public CommandSessionRefresher(string command, string? workingDirectory = null, TimeSpan? timeout = null,
        ILogger? log = null)
    {
        _command = command;
        _workingDirectory = workingDirectory;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _log = log ?? NullLogger.Instance;
    }

    public string Command => _command;

    /// <summary>
    /// Builds a refresher from <c>TRIPIT_SESSION_REFRESH_COMMAND</c>, or from a discovered
    /// <c>tools/extract-session.js</c>. Returns null when neither is available, or when
    /// <c>TRIPIT_SESSION_REFRESH_COMMAND</c> is set to <c>none</c>.
    /// </summary>
    public static CommandSessionRefresher? FromEnvironment(ILogger? log = null)
    {
        var cmd = Environment.GetEnvironmentVariable("TRIPIT_SESSION_REFRESH_COMMAND");
        if (string.Equals(cmd, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(cmd)) return new CommandSessionRefresher(cmd, null, null, log);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var script = Path.Combine(dir.FullName, "tools", "extract-session.js");
            if (File.Exists(script))
            {
                return new CommandSessionRefresher($"node \"{script}\"", dir.FullName, null, log);
            }
        }
        return null;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isWindows)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(_command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_command);
        }
        if (_workingDirectory is not null) psi.WorkingDirectory = _workingDirectory;

        _log.LogInformation("Refreshing TripIt session: {Command}", _command);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start: {_command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var err = await stderr.ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Session refresh command exited {proc.ExitCode}: {(err.Length > 400 ? err[..400] : err)}");
        }
    }
}
