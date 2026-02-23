using System.Collections.Concurrent;

namespace HybridCache.Internal;

/// <summary>
/// Per-key async lock provider to prevent cache stampede (thundering herd) on L2 misses.
/// SemaphoreSlim entries are evicted from the dictionary once released and idle,
/// preventing unbounded memory growth for high-cardinality key spaces.
/// </summary>
internal sealed class KeyLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(key, semaphore, _locks);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _dict;
        private bool _disposed;

        internal Releaser(
            string key,
            SemaphoreSlim semaphore,
            ConcurrentDictionary<string, SemaphoreSlim> dict)
        {
            _key       = key;
            _semaphore = semaphore;
            _dict      = dict;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _semaphore.Release();

            // Evict the semaphore if no other waiter is queued.
            // CurrentCount == 1 means the semaphore is fully released (idle),
            // so it is safe to remove it from the dictionary.
            // Race: another thread may re-add it immediately after — that is fine;
            // both paths produce a valid SemaphoreSlim(1,1).
            if (_semaphore.CurrentCount == 1)
                _dict.TryRemove(_key, out _);
        }
    }
}
