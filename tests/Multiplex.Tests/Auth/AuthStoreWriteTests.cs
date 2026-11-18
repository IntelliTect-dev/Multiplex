using System.Net;
using static Multiplex.Tests.Auth.OAuthDriver;

namespace Multiplex.Tests.Auth;

/// <summary>
/// The encrypted auth store is rewritten (encrypt and fsync, under the one store lock that bearer
/// validation also waits on) only when something changed: refused and no-op requests leave it alone,
/// while every revocation, refusals included, still reaches the disk.
/// </summary>
public sealed class AuthStoreWriteTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "multiplex-tests", Guid.NewGuid().ToString("N"));

    private string StoreFile => Path.Combine(_dataPath, "auth.dat");

    [Fact]
    public async Task RefusedTokenRequest_OnAFreshServer_CreatesNoStore()
    {
        using var app = new FileStoreAuthTestApp(_dataPath);
        var driver = new OAuthDriver(app);

        var (status, body, _) = await driver.TokenAsync(Refresh("unknown-client", "unknown-refresh-token"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        Assert.False(File.Exists(StoreFile), "A refused token request must not write the auth store.");
    }

    [Fact]
    public async Task RefusedAndNoOpRequests_LeaveTheStoreFileUntouched()
    {
        using var app = new FileStoreAuthTestApp(_dataPath);
        var driver = new OAuthDriver(app);
        var tokens = await driver.ConnectAsync();
        var before = await File.ReadAllBytesAsync(StoreFile);

        // Token endpoint: unknown code, unknown refresh token, another client's refresh token.
        await AssertTokenError(driver, CodeExchange(tokens.ClientId, "no-such-code", NewPkce()), "invalid_grant");
        await AssertTokenError(driver, Refresh(tokens.ClientId, "no-such-refresh-token"), "invalid_grant");
        await AssertTokenError(driver, Refresh("another-client", tokens.RefreshToken), "invalid_grant");

        // Revocation of a token nobody holds.
        var revoke = await driver.Http.PostAsync("/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "no-such-token" }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // Connect pages finishing an authorization that does not exist, and an account with nothing to revoke.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.Completer.CompleteAsync("no-such-pending", Account(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.Completer.FailAsync("no-such-pending", "access_denied", "Cancelled.", CancellationToken.None));
        await driver.Revoker.RevokeAccountAsync("tripit", "nobody", CancellationToken.None);

        // Data Protection output is randomized, so any rewrite would change the bytes.
        Assert.Equal(before, await File.ReadAllBytesAsync(StoreFile));
        var (status, _, _) = await driver.TokenAsync(Refresh(tokens.ClientId, tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task RefreshTokenReuse_RevokesTheFamily_AcrossARestart()
    {
        string access;
        string refresh;
        string clientId;
        using (var first = new FileStoreAuthTestApp(_dataPath))
        {
            var driver = new OAuthDriver(first);
            var tokens = await driver.ConnectAsync();
            clientId = tokens.ClientId;
            var (_, rotated, _) = await driver.TokenAsync(Refresh(clientId, tokens.RefreshToken));
            access = rotated.GetProperty("access_token").GetString()!;
            refresh = rotated.GetProperty("refresh_token").GetString()!;

            await AssertTokenError(driver, Refresh(clientId, tokens.RefreshToken), "invalid_grant");
        }

        using var second = new FileStoreAuthTestApp(_dataPath);
        var restarted = new OAuthDriver(second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.InitializeAsync("/tripit/mcp", access)).StatusCode);
        await AssertTokenError(restarted, Refresh(clientId, refresh), "invalid_grant");
    }

    [Fact]
    public async Task AuthorizationCodeReuse_RevokesWhatItIssued_AcrossARestart()
    {
        string access;
        string refresh;
        string clientId;
        using (var first = new FileStoreAuthTestApp(_dataPath))
        {
            var driver = new OAuthDriver(first);
            clientId = await driver.RegisterAsync();
            var pkce = NewPkce();
            var code = await driver.CodeAsync(AuthorizeParameters(clientId, pkce), Account());
            var (_, issued, _) = await driver.TokenAsync(CodeExchange(clientId, code, pkce));
            access = issued.GetProperty("access_token").GetString()!;
            refresh = issued.GetProperty("refresh_token").GetString()!;

            await AssertTokenError(driver, CodeExchange(clientId, code, pkce), "invalid_grant");
        }

        using var second = new FileStoreAuthTestApp(_dataPath);
        var restarted = new OAuthDriver(second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await restarted.InitializeAsync("/tripit/mcp", access)).StatusCode);
        await AssertTokenError(restarted, Refresh(clientId, refresh), "invalid_grant");
    }

    private static async Task AssertTokenError(OAuthDriver driver, IDictionary<string, string> form, string error)
    {
        var (status, body, _) = await driver.TokenAsync(form);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(error, body.GetProperty("error").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }
}
