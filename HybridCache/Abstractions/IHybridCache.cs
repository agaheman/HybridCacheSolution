namespace HybridCache.Abstractions;

public interface IHybridCache<T> where T : class, IVersioned
{
    ValueTask<T?> GetAsync(string id, CancellationToken ct = default);
    ValueTask SetAsync(string id, T value, CancellationToken ct = default);
    ValueTask RemoveAsync(string id, CancellationToken ct = default);
}