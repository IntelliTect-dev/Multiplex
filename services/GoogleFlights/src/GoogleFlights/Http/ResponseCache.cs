using System.Collections.Concurrent;

namespace Multiplex.Sites.GoogleFlights.Http;

/// <summary>
/// Answers kept for a short time, keyed on the exact request the client would send (RPC, language, region,
/// currency and the encoded request): two calls that would send the same bytes share one answer.
/// Identical calls in flight at the same time share one request to Google.
/// </summary>
internal sealed class ResponseCache(TimeSpan duration, int capacity, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight = new(StringComparer.Ordinal);

    private sealed record Entry(object Value, DateTimeOffset StoredAt);

    public bool TryGet<T>(string key, out T value) where T : class
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Value is T found && time.GetUtcNow() - entry.StoredAt < duration)
        {
            value = found;
            return true;
        }
        value = null!;
        return false;
    }

    public void Set(string key, object value)
    {
        if (duration <= TimeSpan.Zero || capacity <= 0)
            return;
        _entries[key] = new Entry(value, time.GetUtcNow());
        if (_entries.Count > capacity)
            Trim();
    }

    /// <summary>Runs <paramref name="fetch"/> once for every concurrent caller with the same key.</summary>
    public async Task<T> SingleFlightAsync<T>(string key, Func<Task<T>> fetch) where T : class
    {
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await fetch().ConfigureAwait(false)));
        try
        {
            return (T)await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, lazy));
        }
    }

    private void Trim()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.StoredAt >= duration)
                _entries.TryRemove(key, out _);
        }
        foreach (var (key, _) in _entries.OrderBy(e => e.Value.StoredAt).Take(Math.Max(0, _entries.Count - capacity)))
            _entries.TryRemove(key, out _);
    }
}
