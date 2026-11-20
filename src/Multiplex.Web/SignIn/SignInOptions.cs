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
}

/// <summary>
/// Where the sign-in machinery keeps its files, all under <c>Multiplex:DataPath</c>. Everything a person
/// signs in to is kept under their own folder, <c>users/{owner}/sites/{slug}</c>, so one person's site
/// sessions and browser profiles are never read for another.
/// </summary>
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

    /// <summary>Every person's folder.</summary>
    public string UsersDirectory => Path.Combine(DataPath, "users");

    /// <summary>
    /// Where site sessions were kept before they belonged to a person (<c>sites</c> directly under the
    /// data folder). They belong to no one, so <see cref="UnownedSiteDataSweeper"/> removes them.
    /// </summary>
    public string UnownedSitesDirectory => Path.Combine(DataPath, "sites");

    public string UserDirectory(string owner) => Path.Combine(UsersDirectory, CheckOwner(owner));

    public string SiteDirectory(string owner, string site) => Path.Combine(UserDirectory(owner), "sites", CheckSlug(site));

    public string SessionsDirectory(string owner, string site) => Path.Combine(SiteDirectory(owner, site), "sessions");

    public string SessionFile(string owner, string site, string accountId) =>
        Path.Combine(SessionsDirectory(owner, site), HashName(accountId) + ".bin");

    public string AccountIndexFile(string owner, string site) => Path.Combine(SiteDirectory(owner, site), "accounts.bin");

    public string ProfilesDirectory(string owner, string site) => Path.Combine(SiteDirectory(owner, site), "profiles");

    /// <summary>The persistent browser profile of one person's signed-in account.</summary>
    public string AccountProfile(string owner, string site, string accountId) =>
        Path.Combine(ProfilesDirectory(owner, site), ProfileName(accountId));

    /// <summary>A fresh, empty profile for one sign-in attempt.</summary>
    public string NewTempProfile(string owner, string site) =>
        Path.Combine(ProfilesDirectory(owner, site), TempProfilePrefix + Guid.NewGuid().ToString("N"));

    /// <summary>Every person's profile folder for <paramref name="site"/> that exists.</summary>
    public IEnumerable<string> AllProfilesDirectories(string site) =>
        Directory.Exists(UsersDirectory)
            ? Directory.GetDirectories(UsersDirectory)
                .Where(dir => OwnerPattern().IsMatch(Path.GetFileName(dir)))
                .Select(dir => Path.Combine(dir, "sites", CheckSlug(site), "profiles"))
                .Where(Directory.Exists)
            : [];

    /// <summary>A Multiplex user id: <c>u</c> and 32 lower-case hex digits, so always a safe folder name.</summary>
    public static string CheckOwner(string owner) =>
        owner is not null && OwnerPattern().IsMatch(owner) ? owner
            : throw new ArgumentException("Invalid owner (Multiplex user id).", nameof(owner));

    /// <summary>Where an account's replaced profile is moved, beside it, until it is deleted.</summary>
    public static string OldProfile(string accountProfile) =>
        accountProfile + OldProfileMarker + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// True for a profile folder that only exists while a sign-in or a profile replacement is
    /// under way (<c>temp-*</c>, <c>{account}.old-xxxxxxxx</c>). <see cref="ProfileName"/> never
    /// gives an account such a name, so a folder found like this after a restart is a leftover.
    /// </summary>
    public static bool IsTransientProfileName(string folderName) =>
        folderName.StartsWith(TempProfilePrefix, StringComparison.OrdinalIgnoreCase) || OldProfileSuffix().IsMatch(folderName);

    private const string TempProfilePrefix = "temp-";
    private const string OldProfileMarker = ".old-";

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
               && !IsTransientProfileName(accountId)
               && !accountId.StartsWith("h-", StringComparison.OrdinalIgnoreCase)
               && !ReservedWindowsName().IsMatch(accountId)
            ? accountId
            : "h-" + HashName(accountId)[..32];
    }

    public static string HashName(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^u[0-9a-f]{32}$")]
    private static partial Regex OwnerPattern();

    // No leading or trailing dot (Windows drops trailing dots, so "a." and "a" would collide).
    [GeneratedRegex(@"^[A-Za-z0-9_-](?:[A-Za-z0-9._-]{0,62}[A-Za-z0-9_-])?$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedWindowsName();

    // What OldProfile appends: ".old-" and 8 hex digits, at the very end.
    [GeneratedRegex(@"\.old-[0-9a-f]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex OldProfileSuffix();
}
