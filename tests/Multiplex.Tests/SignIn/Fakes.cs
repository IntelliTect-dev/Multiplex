using System.Collections.Concurrent;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>Stands in for Auth/: holds pending authorizations and records how each one ended.</summary>
public sealed class FakeCompleter : IAuthorizationCompleter
{
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new();

    public ConcurrentQueue<(string PendingId, SiteAccount Account)> Completed { get; } = new();
    public ConcurrentQueue<(string PendingId, string Error, string Description)> Failed { get; } = new();

    public PendingAuthorization Add(string id, string site, string clientName = "Test Client", TimeSpan? lifetime = null)
    {
        var pending = new PendingAuthorization(id, site, clientName, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromMinutes(10)));
        _pending[id] = pending;
        return pending;
    }

    public Task<PendingAuthorization?> FindAsync(string pendingId, CancellationToken ct) =>
        Task.FromResult(_pending.TryGetValue(pendingId, out var p) ? p : null);

    public Task<Uri> CompleteAsync(string pendingId, SiteAccount account, CancellationToken ct)
    {
        Completed.Enqueue((pendingId, account));
        _pending.TryRemove(pendingId, out _);
        return Task.FromResult(new Uri($"https://client.example.test/callback?code=code-for-{pendingId}&state=s1"));
    }

    public Task<Uri> FailAsync(string pendingId, string error, string description, CancellationToken ct)
    {
        Failed.Enqueue((pendingId, error, description));
        _pending.TryRemove(pendingId, out _);
        return Task.FromResult(new Uri($"https://client.example.test/callback?error={error}&state=s1"));
    }
}

public sealed class FakeRevoker : IGrantRevoker
{
    public ConcurrentQueue<(string Site, string AccountId)> Revoked { get; } = new();

    public Task RevokeAccountAsync(string site, string accountId, CancellationToken ct)
    {
        Revoked.Enqueue((site, accountId));
        return Task.CompletedTask;
    }
}

/// <summary>A refresher whose answer the test decides: a snapshot, or null for "signed out".</summary>
public sealed class FakeRefresher : ISiteSessionRefresher
{
    public string? Result { get; set; } = """{"cookie":"refreshed"}""";
    public ConcurrentQueue<(string Site, string AccountId)> Calls { get; } = new();

    public Task<string?> RefreshAsync(string site, string accountId, CancellationToken ct)
    {
        Calls.Enqueue((site, accountId));
        return Task.FromResult(Result);
    }
}
