using System.Diagnostics;
using Multiplex.Web.SignIn;
using Xunit;

namespace Multiplex.Tests.SignIn;

/// <summary>A fact that only means something on Windows (an open file blocks a directory move only there).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "An open file blocks moving its folder only on Windows.";
    }
}

/// <summary>
/// After a sign-in browser closes, Chrome's helper processes (and on Windows, antivirus) can keep
/// files in its profile open for a while, longer on a busy PC. Making that profile the account's
/// profile must wait that out, within one bounded and cancellable wait, and never lose the new
/// sign-in over the old profile. These tests hold files open themselves, so they depend on
/// neither load nor Chrome.
/// </summary>
public sealed class ProfilePromotionTests
{
    private const string Account = "account-1";

    private static FileStream Hold(string folder, string name = "Cookies")
    {
        Directory.CreateDirectory(folder);
        return new FileStream(Path.Combine(folder, name), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task ReleaseAfter(FileStream held, TimeSpan delay)
    {
        await Task.Delay(delay);
        await held.DisposeAsync();
    }

    [WindowsFact]
    public async Task Promote_waits_while_a_file_in_the_new_profile_is_still_held()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        var held = Hold(temp);
        var release = ReleaseAfter(held, TimeSpan.FromSeconds(7)); // longer than the 5 s the promote used to allow
        try
        {
            var target = await manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account);

            Assert.True(File.Exists(Path.Combine(target, "Cookies")));
            Assert.False(Directory.Exists(temp));
        }
        finally
        {
            await release;
        }
    }

    [WindowsFact]
    public async Task Promote_waits_while_a_file_in_the_old_profile_is_still_held()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        var existing = manager.Paths.AccountProfile(StubSignInDefinition.SiteSlug, Account);
        var held = Hold(existing);
        var release = ReleaseAfter(held, TimeSpan.FromSeconds(7));
        try
        {
            var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
            Directory.CreateDirectory(temp);
            await File.WriteAllTextAsync(Path.Combine(temp, "new"), "new profile");

            var target = await manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account);

            Assert.True(File.Exists(Path.Combine(target, "new")));
            Assert.False(File.Exists(Path.Combine(target, "Cookies")));
        }
        finally
        {
            await release;
        }
    }

    [WindowsFact]
    public async Task Promote_gives_up_within_one_shared_bound_and_puts_the_old_profile_back()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        manager.ProfileReleaseTimeout = TimeSpan.FromSeconds(2);
        var existing = manager.Paths.AccountProfile(StubSignInDefinition.SiteSlug, Account);
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "old"), "old profile");
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        await using (Hold(temp))
        {
            var clock = Stopwatch.StartNew();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account));

            Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
            Assert.True(Directory.Exists(temp), "A failed promote leaves the new profile where it was.");
            Assert.True(File.Exists(Path.Combine(existing, "old")), "The account keeps its old profile when the new one cannot move in.");
        }
    }

    [WindowsFact]
    public async Task Promote_stops_waiting_when_cancelled()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        await using (Hold(temp))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var clock = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account, cancel.Token));

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
            Assert.True(Directory.Exists(temp));
        }
    }

    [WindowsFact]
    public async Task An_old_profile_that_cannot_be_deleted_does_not_cost_the_new_sign_in()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        manager.DiscardTimeout = TimeSpan.FromSeconds(1);
        var existing = manager.Paths.AccountProfile(StubSignInDefinition.SiteSlug, Account);
        Directory.CreateDirectory(existing);
        var stuck = Path.Combine(existing, "read-only");
        await File.WriteAllTextAsync(stuck, "old"); // a read-only file can be moved with its folder but not deleted
        File.SetAttributes(stuck, FileAttributes.ReadOnly);
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        Directory.CreateDirectory(temp);
        await File.WriteAllTextAsync(Path.Combine(temp, "new"), "new profile");
        try
        {
            var target = await manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account);

            Assert.True(File.Exists(Path.Combine(target, "new")), "The new sign-in's profile is in place.");
            Assert.False(Directory.Exists(temp));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(manager.Paths.ProfilesDirectory(StubSignInDefinition.SiteSlug), "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [WindowsFact]
    public async Task Discarding_an_unneeded_profile_waits_only_briefly_and_never_throws()
    {
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        manager.DiscardTimeout = TimeSpan.FromSeconds(1);
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        await using (Hold(temp))
        {
            var clock = Stopwatch.StartNew();

            var deleted = await manager.DiscardProfileAsync(temp);

            Assert.False(deleted);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"took {clock.Elapsed}");
        }
        Assert.True(await manager.DiscardProfileAsync(temp));
        Assert.False(Directory.Exists(temp));
    }
}
