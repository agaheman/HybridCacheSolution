namespace HybridCache.Options;

public sealed class HybridCacheOptions
{
    public string KeyPrefix { get; init; } = default!;
    public TimeSpan MemoryTtl { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RedisTtl { get; init; } = TimeSpan.FromMinutes(15);
    public bool UseSlidingExpiration { get; init; } = true;
    public bool RefreshRedisTtlOnRead { get; init; } = false;
    public string InvalidationChannel { get; init; } = "hybrid-cache-invalidated";
    public bool UseBinarySerializer { get; init; } = false;
    public bool EnableClusterMode { get; init; } = false;
}