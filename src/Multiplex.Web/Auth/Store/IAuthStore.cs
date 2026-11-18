namespace Multiplex.Web.Auth.Store;

/// <summary>
/// Serialized access to the authorization server's <see cref="AuthState"/>. Each call runs its delegate
/// alone (single process), so a check and the update it guards (e.g. redeeming a code) happen atomically.
/// The delegates must not block or call back into the store.
/// </summary>
public interface IAuthStore
{
    /// <summary>Reads without persisting. The delegate must not mutate the state.</summary>
    Task<T> ReadAsync<T>(Func<AuthState, T> read, CancellationToken ct);

    /// <summary>
    /// Prunes expired entries, runs <paramref name="write"/>, then persists the state only when the delegate
    /// reports a change, so a refused or no-op request costs no encrypt and fsync under the lock. A delegate
    /// that mutates the state must return <see cref="StoreWrite.Changed{T}"/> (refusals that revoke something
    /// included); one that returns <see cref="StoreWrite.Unchanged{T}"/> must not have mutated it.
    /// </summary>
    Task<T> WriteAsync<T>(Func<AuthState, StoreWrite<T>> write, CancellationToken ct);
}

/// <summary>What an <see cref="IAuthStore.WriteAsync{T}"/> delegate returns, and whether it changed the state.</summary>
public readonly record struct StoreWrite<T>(T Result, bool Changed);

public static class StoreWrite
{
    /// <summary>The delegate changed the state, so it is persisted.</summary>
    public static StoreWrite<T> Changed<T>(T result) => new(result, true);

    /// <summary>The delegate left the state as it was, so nothing is written.</summary>
    public static StoreWrite<T> Unchanged<T>(T result) => new(result, false);
}

/// <summary>Locking, pruning and rollback shared by the store implementations.</summary>
public abstract class AuthStoreBase(TimeProvider time) : IAuthStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuthState? _state;

    protected TimeProvider Time { get; } = time;

    public async Task<T> ReadAsync<T>(Func<AuthState, T> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _state ??= await LoadAsync(ct);
            return read(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> WriteAsync<T>(Func<AuthState, StoreWrite<T>> write, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _state ??= await LoadAsync(ct);
            try
            {
                // Pruning alone is not saved: expired entries are dropped again after a reload.
                _state.Prune(Time.GetUtcNow());
                var (result, changed) = write(_state);
                if (changed) await SaveAsync(_state, CancellationToken.None);
                return result;
            }
            catch
            {
                // Memory may now differ from what was persisted; reload on next use.
                _state = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the persisted state, or a new empty one.</summary>
    protected abstract Task<AuthState> LoadAsync(CancellationToken ct);

    /// <summary>Persists the state after a write that changed it.</summary>
    protected abstract Task SaveAsync(AuthState state, CancellationToken ct);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _gate.Dispose();
    }
}

/// <summary>
/// Keeps the state in memory only; for tests and throwaway runs. Saves a serialized copy, so a failed
/// write rolls back and serialization is exercised just as in the file store.
/// </summary>
public sealed class InMemoryAuthStore(TimeProvider time) : AuthStoreBase(time)
{
    private byte[]? _saved;

    protected override Task<AuthState> LoadAsync(CancellationToken ct) =>
        Task.FromResult(_saved is null ? new AuthState() : AuthStateSerializer.Deserialize(_saved));

    protected override Task SaveAsync(AuthState state, CancellationToken ct)
    {
        _saved = AuthStateSerializer.Serialize(state);
        return Task.CompletedTask;
    }
}

internal static class AuthStateSerializer
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static byte[] Serialize(AuthState state) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state, Options);

    public static AuthState Deserialize(ReadOnlySpan<byte> json) =>
        System.Text.Json.JsonSerializer.Deserialize<AuthState>(json, Options)
        ?? throw new System.Text.Json.JsonException("The auth store is empty.");
}
