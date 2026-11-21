using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// <see cref="ISiteSessionStore"/> on disk: one Data Protection-encrypted file per person and account at
/// <c>DataPath\users\{owner}\sites\{slug}\sessions\{sha256(accountId)}.bin</c>, written atomically. The
/// encryption is bound to the owner too, so a file copied into another person's folder does not decrypt.
/// Snapshot values are never logged.
/// </summary>
public sealed class SiteSessionStore(IDataProtectionProvider provider, SignInPaths paths, ILogger<SiteSessionStore> logger)
    : ISiteSessionStore
{
    private readonly IDataProtector _protector = provider.CreateProtector("Multiplex.SignIn.SiteSession.v2");

    private IDataProtector ProtectorFor(string owner) => _protector.CreateProtector(SignInPaths.CheckOwner(owner));

    public async Task<string?> GetAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        var path = paths.SessionFile(owner, site, accountId);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = ProtectorFor(owner).Unprotect(await File.ReadAllBytesAsync(path, ct));
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            // Logged without the value; a session that cannot be read is treated as absent.
            logger.LogError("Stored {Site} session for an account could not be read ({Error}); treating it as missing.",
                site, ex.GetType().Name);
            return null;
        }
    }

    public Task SetAsync(string owner, string site, string accountId, string snapshotJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotJson);
        var bytes = ProtectorFor(owner).Protect(Encoding.UTF8.GetBytes(snapshotJson));
        return AtomicFile.WriteAsync(paths.SessionFile(owner, site, accountId), bytes, ct);
    }

    public Task RemoveAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        var path = paths.SessionFile(owner, site, accountId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Whether a snapshot is stored, without decrypting it.</summary>
    public bool Exists(string owner, string site, string accountId) => File.Exists(paths.SessionFile(owner, site, accountId));
}

/// <summary>
/// The accounts the server holds a session for, per person and site: a record of whose sessions are on
/// disk. The connect page never offers them (#52): every connection signs in as its own account.
/// Encrypted like the sessions, at <c>DataPath\users\{owner}\sites\{slug}\accounts.bin</c>.
/// A list that cannot be decrypted or parsed reads as empty; the next change moves it aside to
/// <c>accounts.bin.unreadable-{timestamp}</c> before writing a new one, so it is never lost silently.
/// </summary>
public sealed class SiteAccountIndex(IDataProtectionProvider provider, SignInPaths paths, ILogger<SiteAccountIndex>? logger = null)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Multiplex.SignIn.AccountIndex.v1");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ILogger _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public async Task<IReadOnlyList<SiteAccount>> ListAsync(string owner, string site, CancellationToken ct)
    {
        var gate = Lock(owner, site);
        await gate.WaitAsync(ct);
        try
        {
            return (await ReadAsync(owner, site, ct)).Accounts;
        }
        catch (IOException ex)
        {
            _logger.LogError("The {Site} account list could not be read ({Error}); offering no saved accounts.", site, ex.GetType().Name);
            return [];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SiteAccount?> FindAsync(string owner, string site, string accountId, CancellationToken ct) =>
        (await ListAsync(owner, site, ct)).FirstOrDefault(a => a.AccountId == accountId);

    public Task UpsertAsync(string owner, SiteAccount account, CancellationToken ct) =>
        UpdateAsync(owner, account.Site, list =>
        {
            list.RemoveAll(a => a.AccountId == account.AccountId);
            list.Add(account with { Site = SignInPaths.CheckSlug(account.Site) });
        }, ct);

    public Task RemoveAsync(string owner, string site, string accountId, CancellationToken ct) =>
        UpdateAsync(owner, site, list => list.RemoveAll(a => a.AccountId == accountId), ct);

    private async Task UpdateAsync(string owner, string site, Action<List<SiteAccount>> change, CancellationToken ct)
    {
        var gate = Lock(owner, site);
        await gate.WaitAsync(ct);
        try
        {
            // An IOException here propagates: a file that could not even be read is never overwritten.
            var (accounts, unreadable) = await ReadAsync(owner, site, ct);
            if (unreadable) MoveAside(owner, site);
            var list = accounts.ToList();
            change(list);
            var json = JsonSerializer.SerializeToUtf8Bytes(list.Select(a => new Entry(a.AccountId, a.DisplayName, [.. a.Emails])).ToList());
            await AtomicFile.WriteAsync(paths.AccountIndexFile(owner, site), _protector.Protect(json), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The site's accounts; <c>Unreadable</c> when the file exists but could not be decrypted or parsed.</summary>
    private async Task<(IReadOnlyList<SiteAccount> Accounts, bool Unreadable)> ReadAsync(string owner, string site, CancellationToken ct)
    {
        var path = paths.AccountIndexFile(owner, site);
        if (!File.Exists(path)) return ([], false);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(_protector.Unprotect(bytes)) ?? [];
            return ([.. entries.Select(e => new SiteAccount(site, e.AccountId, e.Emails ?? [], e.DisplayName ?? ""))], false);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            _logger.LogError("The {Site} account list could not be read ({Error}); treating it as empty.", site, ex.GetType().Name);
            return ([], true);
        }
    }

    /// <summary>Keeps an unreadable account list beside the new one, so a lost key or a damaged file can still be recovered.</summary>
    private void MoveAside(string owner, string site)
    {
        var path = paths.AccountIndexFile(owner, site);
        var aside = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid().ToString("N")[..6]}";
        File.Move(path, aside);
        _logger.LogError("Moved the unreadable {Site} account list aside to {File} and started a new one.", site, Path.GetFileName(aside));
    }

    private SemaphoreSlim Lock(string owner, string site) =>
        _locks.GetOrAdd(SignInPaths.CheckOwner(owner) + "/" + SignInPaths.CheckSlug(site), _ => new SemaphoreSlim(1, 1));

    private sealed record Entry(string AccountId, string? DisplayName, List<string>? Emails);
}

internal static class AtomicFile
{
    /// <summary>Writes to a sibling temp file, then renames it over the target, so readers never see half a file.</summary>
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
