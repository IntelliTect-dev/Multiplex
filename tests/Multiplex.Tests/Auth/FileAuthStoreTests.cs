using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplex.Web.Auth;
using Multiplex.Web.Auth.Store;

namespace Multiplex.Tests.Auth;

public sealed class FileAuthStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "multiplex-tests", Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_root, "data");
    private string StoreFile => Path.Combine(DataPath, "auth.dat");

    private FileAuthStore NewStore() => new(
        Options.Create(new MultiplexAuthOptions { DataPath = DataPath }),
        TimeProvider.System,
        NullLogger<FileAuthStore>.Instance);

    private static RegisteredClient Client(string id) =>
        new(id, "Visible Client Name", ["https://claude.ai/api/mcp/auth_callback"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Write_IsEncryptedAtRestAndReadBackAfterRestart()
    {
        using (var store = NewStore())
        {
            await store.WriteAsync(s => StoreWrite.Changed(s.Clients["client-round-trip"] = Client("client-round-trip")), CancellationToken.None);
        }

        var raw = await File.ReadAllBytesAsync(StoreFile);
        Assert.DoesNotContain("client-round-trip", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("Visible Client Name", Encoding.UTF8.GetString(raw));
        Assert.Equal(["auth.dat"], Directory.GetFiles(DataPath).Select(Path.GetFileName));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(DataPath, "keys"), "key-*.xml"));

        using var reopened = NewStore();
        var client = await reopened.ReadAsync(s => s.Clients.GetValueOrDefault("client-round-trip"), CancellationToken.None);
        Assert.NotNull(client);
        Assert.Equal("Visible Client Name", client.ClientName);
        Assert.Equal(["https://claude.ai/api/mcp/auth_callback"], client.RedirectUris);
    }

    [Fact]
    public async Task NothingTouchesTheDiskBeforeFirstUse()
    {
        using var store = NewStore();

        Assert.False(Directory.Exists(DataPath));
        await store.ReadAsync(s => s.Clients.Count, CancellationToken.None);
        Assert.False(Directory.Exists(DataPath));
    }

    [Fact]
    public async Task UnreadableStore_IsSetAsideAndStartsEmpty()
    {
        Directory.CreateDirectory(DataPath);
        await File.WriteAllTextAsync(StoreFile, "not protected data");

        using var store = NewStore();
        var count = await store.ReadAsync(s => s.Clients.Count, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.False(File.Exists(StoreFile));
        Assert.Single(Directory.GetFiles(DataPath, "auth.dat.unreadable-*"));
    }

    [Fact]
    public async Task WriteReportingNoChange_LeavesTheFileAlone()
    {
        using var store = NewStore();
        var missing = await store.WriteAsync(s => StoreWrite.Unchanged(s.Clients.ContainsKey("absent")), CancellationToken.None);
        Assert.False(missing);
        Assert.False(File.Exists(StoreFile));

        await store.WriteAsync(s => StoreWrite.Changed(s.Clients["kept"] = Client("kept")), CancellationToken.None);
        var saved = await File.ReadAllBytesAsync(StoreFile);
        await store.WriteAsync(s => StoreWrite.Unchanged(s.Clients.ContainsKey("kept")), CancellationToken.None);

        // Data Protection output is randomized, so a rewrite would change the bytes.
        Assert.Equal(saved, await File.ReadAllBytesAsync(StoreFile));
    }

    [Fact]
    public async Task FailedWrite_LeavesPersistedStateUnchanged()
    {
        using var store = NewStore();
        await store.WriteAsync(s => StoreWrite.Changed(s.Clients["kept"] = Client("kept")), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync<bool>(s =>
        {
            s.Clients["half-written"] = Client("half-written");
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));

        Assert.True(await store.ReadAsync(s => s.Clients.ContainsKey("kept"), CancellationToken.None));
        Assert.False(await store.ReadAsync(s => s.Clients.ContainsKey("half-written"), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
