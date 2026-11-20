using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplex.Tests.Identity;
using Multiplex.Web.SignIn;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// Profile folders a crash or reboot left half-way through a sign-in (<c>temp-*</c>, and
/// <c>*.old-*</c> from a profile being replaced) can hold signed-in site cookies. The server
/// deletes them when it starts, and never touches an account's own profile.
/// </summary>
public sealed class StaleProfileTests
{
    private static readonly string Owner = TestPeople.OwnerOf();

    [Fact]
    public async Task Server_start_deletes_leftover_sign_in_profiles_and_keeps_account_profiles()
    {
        await using var host = new SignInTestHost(new SignInTestOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome.exe") });
        var leftoverTemp = Seed(host.ProfilesDirectory, "temp-" + Guid.NewGuid().ToString("N"));
        var leftoverOld = Seed(host.ProfilesDirectory, StubLoginSite.AccountId + ".old-1a2b3c4d");
        var account = Seed(host.ProfilesDirectory, StubLoginSite.AccountId);
        var hashedAccount = Seed(host.ProfilesDirectory, SignInPaths.ProfileName("someone@example.test"));
        var lookalike = Seed(host.ProfilesDirectory, "notes.old-backup"); // not a name the server ever gives a replaced profile

        _ = host.Services; // starts the server

        Assert.False(Directory.Exists(leftoverTemp));
        Assert.False(Directory.Exists(leftoverOld));
        Assert.True(File.Exists(Path.Combine(account, "Cookies")));
        Assert.True(File.Exists(Path.Combine(hashedAccount, "Cookies")));
        Assert.True(File.Exists(Path.Combine(lookalike, "Cookies")));
    }

    [Fact]
    public async Task Sweep_covers_every_site_and_every_person_and_leaves_other_folders_alone()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "multiplex-sweep-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (sweeper, paths) = Sweeper(dataPath, "stub", "other");
            var stubTemp = Seed(paths.ProfilesDirectory(Owner, "stub"), "temp-abc");
            var otherTemp = Seed(paths.ProfilesDirectory(Owner, "other"), "TEMP-def");
            var otherOld = Seed(paths.ProfilesDirectory(Owner, "other"), "acct.old-ABCDEF12");
            var otherAccount = Seed(paths.ProfilesDirectory(Owner, "other"), "acct");
            var friendTemp = Seed(paths.ProfilesDirectory(TestPeople.OwnerOf(TestPeople.Friend), "stub"), "temp-ghi");
            var sessions = Seed(paths.SessionsDirectory(Owner, "other"), "temp-not-a-profile");

            await sweeper.StartAsync(CancellationToken.None);

            Assert.False(Directory.Exists(stubTemp));
            Assert.False(Directory.Exists(otherTemp));
            Assert.False(Directory.Exists(otherOld));
            Assert.False(Directory.Exists(friendTemp));
            Assert.True(Directory.Exists(otherAccount));
            Assert.True(Directory.Exists(sessions)); // only profile folders are swept
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_a_browser_still_holds_is_left_alone()
    {
        if (!OperatingSystem.IsWindows()) return; // Chrome's Windows lock is an exclusively opened "lockfile"
        var dataPath = Path.Combine(Path.GetTempPath(), "multiplex-sweep-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (sweeper, paths) = Sweeper(dataPath, "stub");
            var busy = Seed(paths.ProfilesDirectory(Owner, "stub"), "temp-busy");
            var idle = Seed(paths.ProfilesDirectory(Owner, "stub"), "temp-idle");
            File.WriteAllText(Path.Combine(idle, "lockfile"), ""); // left by a Chrome that has exited
            using (new FileStream(Path.Combine(busy, "lockfile"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await sweeper.StartAsync(CancellationToken.None);
            }

            Assert.True(File.Exists(Path.Combine(busy, "Cookies")));
            Assert.False(Directory.Exists(idle));
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    private static (StaleProfileSweeper Sweeper, SignInPaths Paths) Sweeper(string dataPath, params string[] slugs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Multiplex:DataPath"] = dataPath }).Build();
        var paths = new SignInPaths(config);
        var manager = new SiteBrowserManager(config, Options.Create(new SignInOptions()), paths, NullLogger<SiteBrowserManager>.Instance);
        var definitions = slugs.Select(ISiteSignInDefinition (s) => new SlugOnlyDefinition(s)).ToArray();
        return (new StaleProfileSweeper(definitions, manager, NullLogger<StaleProfileSweeper>.Instance), paths);
    }

    /// <summary>A folder and a file in it, as a browser profile would have.</summary>
    internal static string Seed(string profilesDirectory, string name)
    {
        var dir = Path.Combine(profilesDirectory, name);
        Directory.CreateDirectory(Path.Combine(dir, "Default"));
        File.WriteAllText(Path.Combine(dir, "Cookies"), "pretend cookie jar");
        return dir;
    }
}
