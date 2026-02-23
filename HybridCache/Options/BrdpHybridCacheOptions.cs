namespace HybridCache.Options;

public sealed class BrdpHybridCacheOptions
{
    public string KeyPrefix { get; init; } = string.Empty;
    public TimeSpan MemoryTtl { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RedisTtl { get; init; } = TimeSpan.FromMinutes(15);
    public bool UseSlidingExpiration { get; init; } = true;
    public bool RefreshRedisTtlOnRead { get; init; } = false;

    /// <summary>
    /// When true, every L1 hit triggers a lightweight Redis HGET on the 'ver' field
    /// to detect external mutations (e.g. another app instance's SetAsync).
    /// On mismatch, L1 is evicted and fresh data is fetched from Redis.
    /// 
    /// Cost: +1 Redis round-trip per L1 hit (~1ms).
    /// Default: false — pub/sub invalidation alone is relied upon for coherence.
    /// Enable when: you need strong consistency across instances and can afford the RTT.
    /// </summary>
    public bool CheckVersionOnRead { get; init; } = false;

    public string InvalidationChannel { get; init; } = "hybrid-cache-invalidated";
    public bool UseBinarySerializer { get; init; } = false;
    public bool EnableClusterMode { get; init; } = false;
}
