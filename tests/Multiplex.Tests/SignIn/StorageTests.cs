using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplex.Web.SignIn;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

public sealed class StorageTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "multiplex-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly SignInPaths _paths;
    private readonly IDataProtectionProvider _protection;

    public StorageTests()
    {
        _paths = new SignInPaths(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Multiplex:DataPath"] = _dataPath }).Build());
        _protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dataPath, "keys")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    private SiteSessionStore Store() => new(_protection, _paths, NullLogger<SiteSessionStore>.Instance);

    [Fact]
    public async Task Session_round_trips_and_is_encrypted_on_disk_under_a_hashed_name()
    {
        var store = Store();
        const string snapshot = """{"cookie":"it_session_id=SECRET-VALUE","csrf_token":"CSRF-VALUE"}""";

        await store.SetAsync("tripit", "ref-123", snapshot, CancellationToken.None);

        Assert.Equal(snapshot, await store.GetAsync("tripit", "ref-123", CancellationToken.None));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_dataPath, "sites", "tripit", "sessions")));
        Assert.Equal(SignInPaths.HashName("ref-123") + ".bin", Path.GetFileName(file));
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
        Assert.DoesNotContain("SECRET-VALUE", raw);
        Assert.DoesNotContain("ref-123", raw);
    }

    [Fact]
    public async Task Overwrite_replaces_and_remove_deletes()
    {
        var store = Store();
        await store.SetAsync("tripit", "a", "one", CancellationToken.None);
        await store.SetAsync("tripit", "a", "two", CancellationToken.None);
        Assert.Equal("two", await store.GetAsync("tripit", "a", CancellationToken.None));

        await store.RemoveAsync("tripit", "a", CancellationToken.None);

        Assert.Null(await store.GetAsync("tripit", "a", CancellationToken.None));
        Assert.False(store.Exists("tripit", "a"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dataPath, "sites", "tripit", "sessions")));
    }

    [Fact]
    public async Task Unreadable_session_is_treated_as_missing()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SessionFile("tripit", "a"))!);
        await File.WriteAllTextAsync(_paths.SessionFile("tripit", "a"), "garbage");

        Assert.Null(await store.GetAsync("tripit", "a", CancellationToken.None));
    }

    [Fact]
    public async Task Account_index_keeps_one_entry_per_account_encrypted()
    {
        var index = new SiteAccountIndex(_protection, _paths);
        await index.UpsertAsync(new SiteAccount("tripit", "r1", ["one@example.test"], "One"), CancellationToken.None);
        await index.UpsertAsync(new SiteAccount("tripit", "r2", ["two@example.test"], "Two"), CancellationToken.None);
        await index.UpsertAsync(new SiteAccount("tripit", "r1", ["one@example.test", "uno@example.test"], "One again"), CancellationToken.None);

        var accounts = await index.ListAsync("tripit", CancellationToken.None);

        Assert.Equal(["r2", "r1"], accounts.Select(a => a.AccountId));
        Assert.Equal(["one@example.test", "uno@example.test"], accounts[1].Emails);
        Assert.DoesNotContain("one@example.test", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_paths.AccountIndexFile("tripit"))));

        await index.RemoveAsync("tripit", "r2", CancellationToken.None);
        Assert.Equal(["r1"], (await index.ListAsync("tripit", CancellationToken.None)).Select(a => a.AccountId));
    }

    /// <summary>
    /// An account list that cannot be read (a lost key, a damaged file) is kept beside the new
    /// one rather than silently overwritten by the next change, so nothing is lost for good.
    /// </summary>
    [Theory]
    [InlineData(false)] // not something this server's key ring encrypted
    [InlineData(true)]  // decrypts, but is not an account list
    public async Task Unreadable_account_index_is_moved_aside_before_a_new_one_is_written(bool decryptable)
    {
        var index = new SiteAccountIndex(_protection, _paths);
        var file = _paths.AccountIndexFile("tripit");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var unreadable = decryptable
            ? _protection.CreateProtector("Multiplex.SignIn.AccountIndex.v1").Protect("not json"u8.ToArray())
            : "encrypted under a key this server no longer has"u8.ToArray();
        await File.WriteAllBytesAsync(file, unreadable);

        // Reading changes nothing.
        Assert.Empty(await index.ListAsync("tripit", CancellationToken.None));
        Assert.Equal(unreadable, await File.ReadAllBytesAsync(file));

        await index.UpsertAsync(new SiteAccount("tripit", "r1", ["one@example.test"], "One"), CancellationToken.None);

        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, "accounts.bin.unreadable-*"));
        Assert.Equal(unreadable, await File.ReadAllBytesAsync(aside));
        Assert.Equal(["r1"], (await index.ListAsync("tripit", CancellationToken.None)).Select(a => a.AccountId));
    }

    [Fact]
    public async Task Removing_from_an_unreadable_account_index_also_keeps_the_old_file()
    {
        var index = new SiteAccountIndex(_protection, _paths);
        var file = _paths.AccountIndexFile("tripit");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "garbage");

        await index.RemoveAsync("tripit", "r1", CancellationToken.None);

        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, "accounts.bin.unreadable-*"));
        Assert.Equal("garbage", await File.ReadAllTextAsync(aside));
        Assert.Empty(await index.ListAsync("tripit", CancellationToken.None));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Bad_slugs_are_refused(string slug)
    {
        Assert.Throws<ArgumentException>(() => _paths.SiteDirectory(slug));
    }

    [Theory]
    [InlineData("12345678", "12345678")]
    [InlineData("abc-DEF_9.x", "abc-DEF_9.x")]
    public void Safe_account_ids_name_their_profile_folder_directly(string accountId, string expected)
    {
        Assert.Equal(expected, SignInPaths.ProfileName(accountId));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    [InlineData("temp-123")]
    [InlineData("someone@example.test")]
    [InlineData("someone.old-1a2b3c4d")] // would look like a replaced profile left behind, and be swept
    [InlineData("SOMEONE.OLD-1A2B3C4D")]
    public void Unsafe_account_ids_get_a_hashed_profile_folder(string accountId)
    {
        var name = SignInPaths.ProfileName(accountId);

        Assert.StartsWith("h-", name);
        Assert.Equal(34, name.Length);
        Assert.StartsWith(Path.Combine(_dataPath, "sites", "tripit", "profiles"), _paths.AccountProfile("tripit", accountId));
    }

    [Fact]
    public void Nothing_outside_the_data_folder_or_under_har_capture_may_be_used_as_a_profile()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Throws<InvalidOperationException>(() => _paths.EnsureInsideDataPath(Path.Combine(localAppData, "har-capture", "profile")));
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureInsideDataPath(_dataPath + "-sibling"));
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureInsideDataPath(Path.Combine(_dataPath, "..", "elsewhere")));
        Assert.Throws<InvalidOperationException>(() => _paths.EnsureInsideDataPath(Path.Combine(_dataPath, "x", "har-capture", "p")));
        Assert.EndsWith("temp-1", _paths.EnsureInsideDataPath(Path.Combine(_dataPath, "sites", "tripit", "profiles", "temp-1")));
    }

    [Fact]
    public void Default_data_path_is_under_local_app_data()
    {
        var paths = new SignInPaths(new ConfigurationBuilder().Build());

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Multiplex", "data"),
            paths.DataPath);
    }
}
