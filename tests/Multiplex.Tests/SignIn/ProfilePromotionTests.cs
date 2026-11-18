using System.Diagnostics;
using Multiplex.Web.SignIn;
using Xunit;

namespace Multiplex.Tests.SignIn;

/// <summary>
/// After a sign-in browser closes, Chrome's helper processes (and on Windows, antivirus) can keep
/// files in its profile open for a while, longer on a busy PC. Making that profile the account's
/// profile must wait that out, within a bound, rather than fail the sign-in after a few seconds.
/// These tests hold a file open themselves, so they do not depend on load or on Chrome.
/// </summary>
public sealed class ProfilePromotionTests
{
    private const string Account = "account-1";

    [Fact]
    public async Task Promote_waits_while_a_file_in_the_new_profile_is_still_held()
    {
        if (!OperatingSystem.IsWindows()) return; // An open file blocks a directory move only on Windows.
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        Directory.CreateDirectory(temp);
        var held = new FileStream(Path.Combine(temp, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(7)); // longer than the 5 s the promote used to allow
            await held.DisposeAsync();
        });

        var target = await manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account);
        await release;

        Assert.True(File.Exists(Path.Combine(target, "Cookies")));
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public async Task Promote_waits_while_a_file_in_the_old_profile_is_still_held()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        var existing = manager.Paths.AccountProfile(StubSignInDefinition.SiteSlug, Account);
        Directory.CreateDirectory(existing);
        var held = new FileStream(Path.Combine(existing, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(7));
            await held.DisposeAsync();
        });
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        Directory.CreateDirectory(temp);
        await File.WriteAllTextAsync(Path.Combine(temp, "new"), "new profile");

        var target = await manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account);
        await release;

        Assert.True(File.Exists(Path.Combine(target, "new")));
        Assert.False(File.Exists(Path.Combine(target, "Cookies")));
    }

    [Fact]
    public async Task Promote_gives_up_with_the_file_error_once_the_bounded_wait_is_over()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var host = new SignInTestHost();
        var manager = host.Get<SiteBrowserManager>();
        manager.ProfileReleaseTimeout = TimeSpan.FromSeconds(2);
        var temp = manager.Paths.NewTempProfile(StubSignInDefinition.SiteSlug);
        Directory.CreateDirectory(temp);
        await using var held = new FileStream(Path.Combine(temp, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => manager.PromoteProfileAsync(StubSignInDefinition.SiteSlug, temp, Account));

        Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().Name);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        Assert.True(Directory.Exists(temp), "A failed promote leaves the new profile where it was.");
    }
}
