using Microsoft.Extensions.Options;
using Multiplex.Web.Sites;
using PuppeteerSharp;

namespace Multiplex.Web.SignIn;

/// <summary>
/// Admin helper: makes an existing, already signed-in Chrome user-data folder the server's
/// profile for that site account. The folder is <em>copied</em> into the data folder first and
/// only the copy is ever launched; the original is read, never written. Whoever can run this
/// already has the server's data folder, so there is no further check on the account. Used by the
/// installer (<c>dotnet run -- adopt-profile &lt;site&gt; &lt;dir&gt;</c>) and by tests.
/// </summary>
public sealed class SiteProfileAdopter(
    IEnumerable<ISiteSignInDefinition> definitions,
    SiteBrowserManager manager,
    SiteSessionStore store,
    SiteAccountIndex index,
    IOptions<SignInOptions> options,
    ILogger<SiteProfileAdopter> logger)
{
    public const string Command = "adopt-profile";

    // Caches and lock files are not worth copying (and the locks would confuse Chrome).
    private static readonly HashSet<string> s_skipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "DawnCache", "DawnGraphiteCache",
        "DawnWebGPUCache", "Crashpad", "component_crx_cache", "extensions_crx_cache", "Safe Browsing",
        "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile", "DevToolsActivePort", "BrowserMetrics",
    };

    public async Task<SiteAccount> AdoptAsync(string site, string sourceProfileDir, CancellationToken ct)
    {
        var definition = definitions.FirstOrDefault(d => string.Equals(d.Slug, site, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No sign-in definition for site '{site}'.");
        var source = Path.GetFullPath(sourceProfileDir);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("The profile folder to adopt does not exist.");
        if (!File.Exists(Path.Combine(source, "Local State")) && !Directory.Exists(Path.Combine(source, "Default")))
            throw new InvalidOperationException("That folder does not look like a Chrome user-data folder (no 'Local State' or 'Default').");
        var profilesRoot = manager.Paths.ProfilesDirectory(definition.Slug);
        if (source.StartsWith(Path.GetFullPath(profilesRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That folder is already one of the server's own profiles.");
        if (SiteBrowserManager.IsProfileInUse(source))
            throw new InvalidOperationException("Chrome is still running on that profile. Close it first.");

        var copy = manager.Paths.NewTempProfile(definition.Slug);
        IBrowser? browser = null;
        var kept = false;
        try
        {
            CopyDirectory(source, manager.Paths.EnsureInsideDataPath(copy), ct);
            browser = await manager.LaunchAsync(definition.Slug, copy, ct);
            var page = (await browser.PagesAsync()).FirstOrDefault() ?? await browser.NewPageAsync();
            var outcome = await SignInProbe.WaitForOutcomeAsync(definition, page, options.Value.RefreshTimeout, ct);
            var account = outcome.Account ?? throw new InvalidOperationException(outcome.SignedOut
                ? $"That profile is not signed in to {definition.DisplayName}."
                : $"Could not tell whether that profile is signed in to {definition.DisplayName} (the site may be blocking the browser).");

            var snapshot = await definition.CaptureSnapshotAsync(page, ct);
            await manager.CloseAsync(browser);
            browser = null;

            using (await manager.LockAccountAsync(definition.Slug, account.AccountId, ct))
            {
                await manager.PromoteProfileAsync(definition.Slug, copy, account.AccountId, ct);
                kept = true;
                await store.SetAsync(definition.Slug, account.AccountId, snapshot, ct);
                await index.UpsertAsync(account, ct);
            }
            logger.LogInformation("Adopted a {Site} profile for account {AccountId}.", definition.Slug, account.AccountId);
            return account;
        }
        finally
        {
            await manager.CloseAsync(browser);
            if (!kept) await manager.DiscardProfileAsync(copy);
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (s_skipNames.Contains(entry.Name) || entry.LinkTarget is not null) continue;
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo dir) CopyDirectory(dir.FullName, destination, ct);
            else File.Copy(entry.FullName, destination, overwrite: false);
        }
    }

    /// <summary><c>adopt-profile &lt;site&gt; &lt;profileDir&gt;</c>: prints the adopted account id, never any session value.</summary>
    public static async Task<int> RunCliAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error)
    {
        if (args is not [Command, var site, var dir])
        {
            await error.WriteLineAsync($"Usage: {Command} <site-slug> <chrome-user-data-dir>");
            return 2;
        }
        try
        {
            var account = await services.GetRequiredService<SiteProfileAdopter>().AdoptAsync(site, dir, CancellationToken.None);
            await output.WriteLineAsync($"Adopted {account.Site} account {account.AccountId}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or PuppeteerException)
        {
            await error.WriteLineAsync("Adopt failed: " + ex.Message);
            return 1;
        }
    }
}
