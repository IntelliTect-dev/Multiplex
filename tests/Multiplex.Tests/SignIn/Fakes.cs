using System.Collections.Concurrent;
using Multiplex.Web.Sites;

namespace Multiplex.Tests.SignIn;

/// <summary>Stands in for Auth/: holds pending authorizations and records how each one ended.</summary>
public sealed class FakeCompleter : IAuthorizationCompleter
{
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending = new();

    public ConcurrentQueue<(string PendingId, string Owner, SiteAccount? Account)> Completed { get; } = new();
    public ConcurrentQueue<(string PendingId, string Error, string Description)> Failed { get; } = new();

    /// <param name="owner">The person who started it (default: <see cref="Identity.TestPeople.Owner"/>); the connect pages let only them go on.</param>
    /// <param name="serviceName">Display name; defaults to the slug.</param>
    public PendingAuthorization Add(string id, string site, string clientName = "Test Client", TimeSpan? lifetime = null,
        string? owner = null, string? serviceName = null)
    {
        var pending = new PendingAuthorization(id, site, clientName, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromMinutes(10)),
            "client.example.test", owner ?? Identity.TestPeople.OwnerOf(), serviceName ?? site);
        _pending[id] = pending;
        return pending;
    }

    public Task<PendingAuthorization?> FindAsync(string pendingId, CancellationToken ct) =>
        Task.FromResult(_pending.TryGetValue(pendingId, out var p) ? p : null);

    public Task<Uri> CompleteAsync(string pendingId, string owner, SiteAccount? account, CancellationToken ct)
    {
        if (!_pending.TryGetValue(pendingId, out var pending)) throw new InvalidOperationException("Unknown pending authorization.");
        if (pending.Owner != owner) throw new ArgumentException("Started by another person.", nameof(owner));
        Completed.Enqueue((pendingId, owner, account));
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
    public ConcurrentQueue<(string Owner, string Site, string AccountId)> Revoked { get; } = new();

    public Task RevokeAccountAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        Revoked.Enqueue((owner, site, accountId));
        return Task.CompletedTask;
    }
}

/// <summary>A refresher whose answer the test decides: a snapshot, or null for "signed out".</summary>
public sealed class FakeRefresher : ISiteSessionRefresher
{
    public string? Result { get; set; } = """{"cookie":"refreshed"}""";
    public ConcurrentQueue<(string Owner, string Site, string AccountId)> Calls { get; } = new();

    public Task<string?> RefreshAsync(string owner, string site, string accountId, CancellationToken ct)
    {
        Calls.Enqueue((owner, site, accountId));
        return Task.FromResult(Result);
    }
}
