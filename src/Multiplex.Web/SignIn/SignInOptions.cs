using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplex.Web.SignIn;

/// <summary>
/// Settings for the connect pages and the server-owned site browsers (section <c>SignIn</c>),
/// plus the few <c>Multiplex:*</c> keys they share with the rest of the host.
/// </summary>
public sealed class SignInOptions
{
    /// <summary>Run site browsers without a window (default). False shows them on the server's desktop.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>At most this many live sign-in browsers at once.</summary>
    public int MaxLiveBrowsers { get; set; } = 2;

    /// <summary>A live sign-in that has not finished by then is closed and its profile deleted.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a live sign-in checks whether the person has signed in.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A refresh for the same account within this window returns the stored result.</summary>
    public TimeSpan RefreshCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a refresh or adopt waits for the site to show signed-in or signed-out.</summary>
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The mirrored page's initial viewport, in CSS pixels (the phone reports its own size later).</summary>
    public int ViewportWidth { get; set; } = 390;
    public int ViewportHeight { get; set; } = 844;
    public double DeviceScaleFactor { get; set; } = 2;

    /// <summary>JPEG quality of mirrored frames (1-100).</summary>
    public int FrameQuality { get; set; } = 70;

    /// <summary>Passes --no-sandbox to Chrome. Only for containers that cannot sandbox; off by default.</summary>
    public bool NoSandbox { get; set; }

    /// <summary>How long the gate stays passed for one pending authorization.</summary>
    public TimeSpan GateLifetime { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Where the sign-in machinery keeps its files, all under <c>Multiplex:DataPath</c>.</summary>
public sealed partial class SignInPaths(IConfiguration config)
{
    /// <summary><c>Multiplex:DataPath</c>, default <c>%LOCALAPPDATA%\Multiplex\data</c>.</summary>
    public string DataPath => Resolve(config);

    public static string Resolve(IConfiguration config)
    {
        var configured = config["Multiplex:DataPath"];
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data")
            : configured;
        return Path.GetFullPath(path);
    }

    public string KeysDirectory => Path.Combine(DataPath, "keys");

    public string SiteDirectory(string site) => Path.Combine(DataPath, "sites", CheckSlug(site));

    public string SessionsDirectory(string site) => Path.Combine(SiteDirectory(site), "sessions");

    public string SessionFile(string site, string accountId) =>
        Path.Combine(SessionsDirectory(site), HashName(accountId) + ".bin");

    public string AccountIndexFile(string site) => Path.Combine(SiteDirectory(site), "accounts.bin");

    public string ProfilesDirectory(string site) => Path.Combine(SiteDirectory(site), "profiles");

    /// <summary>The persistent browser profile of one signed-in account.</summary>
    public string AccountProfile(string site, string accountId) =>
        Path.Combine(ProfilesDirectory(site), ProfileName(accountId));

    /// <summary>A fresh, empty profile for one sign-in attempt.</summary>
    public string NewTempProfile(string site) =>
        Path.Combine(ProfilesDirectory(site), "temp-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Throws unless <paramref name="path"/> is strictly inside the data folder. Every browser
    /// launch, move and delete goes through this, so nothing outside it is ever touched.
    /// </summary>
    public string EnsureInsideDataPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = DataPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to use a browser profile outside Multiplex:DataPath.");
        if (full.Split(Path.DirectorySeparatorChar).Any(s => s.Equals("har-capture", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Refusing to use a har-capture browser profile.");
        return full;
    }

    /// <summary>Lower-case letters, digits and dashes only, so a slug is always a safe folder name.</summary>
    public static string CheckSlug(string site) =>
        SlugPattern().IsMatch(site) ? site.ToLowerInvariant()
            : throw new ArgumentException("Invalid site slug.", nameof(site));

    /// <summary>The account id when it is already a safe folder name, else a hash of it.</summary>
    public static string ProfileName(string accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        return SafeNamePattern().IsMatch(accountId)
               && !accountId.StartsWith("temp-", StringComparison.OrdinalIgnoreCase)
               && !accountId.StartsWith("h-", StringComparison.OrdinalIgnoreCase)
               && !ReservedWindowsName().IsMatch(accountId)
            ? accountId
            : "h-" + HashName(accountId)[..32];
    }

    public static string HashName(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex SlugPattern();

    // No leading or trailing dot (Windows drops trailing dots, so "a." and "a" would collide).
    [GeneratedRegex(@"^[A-Za-z0-9_-](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9_-])?$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedWindowsName();
}
