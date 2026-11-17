using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Multiplex.Web.Sites;

namespace Multiplex.Web.SignIn;

/// <summary>
/// <see cref="ISiteSessionStore"/> on disk: one Data Protection-encrypted file per account at
/// <c>DataPath\sites\{slug}\sessions\{sha256(accountId)}.bin</c>, written atomically.
/// Snapshot values are never logged.
/// </summary>
public sealed class SiteSessionStore(IDataProtectionProvider provider, SignInPaths paths, ILogger<SiteSessionStore> logger)
    : ISiteSessionStore
{
    private readonly IDataProtector _protector = provider.CreateProtector("Multiplex.SignIn.SiteSession.v1");

    public async Task<string?> GetAsync(string site, string accountId, CancellationToken ct)
    {
        var path = paths.SessionFile(site, accountId);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = _protector.Unprotect(await File.ReadAllBytesAsync(path, ct));
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

    public Task SetAsync(string site, string accountId, string snapshotJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotJson);
        var bytes = _protector.Protect(Encoding.UTF8.GetBytes(snapshotJson));
        return AtomicFile.WriteAsync(paths.SessionFile(site, accountId), bytes, ct);
    }

    public Task RemoveAsync(string site, string accountId, CancellationToken ct)
    {
        var path = paths.SessionFile(site, accountId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Whether a snapshot is stored, without decrypting it.</summary>
    public bool Exists(string site, string accountId) => File.Exists(paths.SessionFile(site, accountId));
}

/// <summary>
/// The accounts the server holds a session for, per site, so the connect page can offer
/// "Continue as …". Encrypted like the sessions, at <c>DataPath\sites\{slug}\accounts.bin</c>.
/// </summary>
public sealed class SiteAccountIndex(IDataProtectionProvider provider, SignInPaths paths)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Multiplex.SignIn.AccountIndex.v1");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<SiteAccount>> ListAsync(string site, CancellationToken ct)
    {
        var gate = Lock(site);
        await gate.WaitAsync(ct);
        try { return await ReadAsync(site, ct); }
        finally { gate.Release(); }
    }

    public async Task<SiteAccount?> FindAsync(string site, string accountId, CancellationToken ct) =>
        (await ListAsync(site, ct)).FirstOrDefault(a => a.AccountId == accountId);

    public Task UpsertAsync(SiteAccount account, CancellationToken ct) =>
        UpdateAsync(account.Site, list =>
        {
            list.RemoveAll(a => a.AccountId == account.AccountId);
            list.Add(account with { Site = SignInPaths.CheckSlug(account.Site) });
        }, ct);

    public Task RemoveAsync(string site, string accountId, CancellationToken ct) =>
        UpdateAsync(site, list => list.RemoveAll(a => a.AccountId == accountId), ct);

    private async Task UpdateAsync(string site, Action<List<SiteAccount>> change, CancellationToken ct)
    {
        var gate = Lock(site);
        await gate.WaitAsync(ct);
        try
        {
            var list = (await ReadAsync(site, ct)).ToList();
            change(list);
            var json = JsonSerializer.SerializeToUtf8Bytes(list.Select(a => new Entry(a.AccountId, a.DisplayName, [.. a.Emails])).ToList());
            await AtomicFile.WriteAsync(paths.AccountIndexFile(site), _protector.Protect(json), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<SiteAccount>> ReadAsync(string site, CancellationToken ct)
    {
        var path = paths.AccountIndexFile(site);
        if (!File.Exists(path)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(_protector.Unprotect(await File.ReadAllBytesAsync(path, ct))) ?? [];
            return [.. entries.Select(e => new SiteAccount(site, e.AccountId, e.Emails ?? [], e.DisplayName ?? ""))];
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or IOException)
        {
            return [];
        }
    }

    private SemaphoreSlim Lock(string site) => _locks.GetOrAdd(SignInPaths.CheckSlug(site), _ => new SemaphoreSlim(1, 1));

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
