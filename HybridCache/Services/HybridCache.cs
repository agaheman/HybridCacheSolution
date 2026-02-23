using System.Collections.Concurrent;
using HybridCache.Abstractions;
using HybridCache.Internal;
using HybridCache.Options;
using HybridCache.Serializers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HybridCache.Services;

/// <summary>
/// Two-level hybrid cache: L1 = <see cref="IMemoryCache"/>, L2 = Redis Hash.
/// <list type="bullet">
///   <item>Version is an internal Redis implementation detail (HINCRBY on 'ver' field)
///         — domain types are not required to implement any version interface.</item>
///   <item>L1 version tracking is maintained in a private <see cref="ConcurrentDictionary{TKey,TValue}"/>
///         keyed by memory-key, decoupled from the domain object.</item>
///   <item>Optional per-read version check against Redis controlled by
///         <see cref="HybridCacheOptions.CheckVersionOnRead"/> (default: false).</item>
///   <item>Per-key SemaphoreSlim prevents cache stampede on L2 misses.</item>
///   <item>Redis failures degrade gracefully to L1-only with full logging.</item>
/// </list>
/// </summary>
public sealed class HybridCache<T> : IHybridCache<T>
    where T : class
{
    private const string VersionField = "ver";
    private const string DataField = "data";

    private readonly IMemoryCache _memory;
    private readonly IDatabase _db;
    private readonly ISubscriber _subscriber;
    private readonly IHybridCacheSerializer<T> _serializer;
    private readonly BrdpHybridCacheOptions _options;
    private readonly KeyLockProvider _lockProvider;
    private readonly ILogger<HybridCache<T>> _logger;

    /// <summary>
    /// Tracks the Redis version of each key currently held in L1.
    /// Keyed by memoryKey. Entries are removed when L1 is evicted.
    /// This keeps version state fully internal — domain objects stay clean.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _l1Versions = new();

    public HybridCache(
        IMemoryCache memory,
        IConnectionMultiplexer mux,
        IHybridCacheSerializer<T> serializer,
        IOptions<BrdpHybridCacheOptions> options,
        ILogger<HybridCache<T>> logger)
    {
        _memory = memory;
        _db = mux.GetDatabase();
        _subscriber = mux.GetSubscriber();
        _serializer = serializer;
        _options = options.Value;
        _lockProvider = new KeyLockProvider();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<T?> GetAsync(string id, CancellationToken ct = default)
    {
        var memKey = BuildMemoryKey(id);
        var redisKey = BuildRedisKey(id);

        // ── L1 Hit path ───────────────────────────────────────────────────────
        if (_memory.TryGetValue(memKey, out T? cached) && cached is not null)
        {
            if (_options.CheckVersionOnRead)
            {
                var isValid = await CheckVersionAsync(memKey, redisKey, cached).ConfigureAwait(false);
                if (!isValid)
                    cached = null; // fall through to L2 fetch below
            }

            if (cached is not null)
            {
                _logger.LogDebug("L1 hit: {Key}", memKey);

                if (_options.RefreshRedisTtlOnRead)
                    await RefreshRedisTtlAsync(redisKey).ConfigureAwait(false);

                return cached;
            }
        }

        // ── Stampede guard: only one concurrent caller fetches from L2 ────────
        using (await _lockProvider.AcquireAsync(memKey, ct).ConfigureAwait(false))
        {
            // Re-check: another waiter may have already populated L1
            if (_memory.TryGetValue(memKey, out cached) && cached is not null)
            {
                _logger.LogDebug("L1 hit after lock: {Key}", memKey);
                return cached;
            }

            return await FetchFromRedisAsync(memKey, redisKey).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask SetAsync(string id, T value, CancellationToken ct = default)
    {
        var redisKey = BuildRedisKey(id);
        var memKey = BuildMemoryKey(id);

        try
        {
            var payload = _serializer.SerializeToBytes(value);
            var newVersion = (long)await _db.ScriptEvaluateAsync(
                HybridCacheLuaScripts.AtomicSetScript.OriginalScript,
                new RedisKey[] { redisKey },
                new RedisValue[] { (int)_options.RedisTtl.TotalSeconds, payload }
            ).ConfigureAwait(false);

            PopulateL1(memKey, value, newVersion);
            await PublishInvalidationAsync(memKey).ConfigureAwait(false);

            _logger.LogDebug("Set: {Key} v{Version}", memKey, newVersion);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during Set — writing L1 only for {Key}", memKey);
            PopulateL1(memKey, value, version: 0);
        }
    }

    /// <inheritdoc/>
    public async ValueTask RemoveAsync(string id, CancellationToken ct = default)
    {
        var memKey = BuildMemoryKey(id);
        var redisKey = BuildRedisKey(id);

        EvictL1(memKey);

        try
        {
            await _db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
            await PublishInvalidationAsync(memKey).ConfigureAwait(false);
            _logger.LogDebug("Removed: {Key}", memKey);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during Remove for {Key}", memKey);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks the Redis 'ver' field against the version stored in <see cref="_l1Versions"/>.
    /// If stale or the key no longer exists in Redis, L1 is evicted and false is returned.
    /// Serves stale L1 (returns true) if Redis is unreachable — availability over consistency.
    /// </summary>
    private async ValueTask<bool> CheckVersionAsync(string memKey, string redisKey, T cached)
    {
        try
        {
            var redisVer = await _db.HashGetAsync(redisKey, VersionField).ConfigureAwait(false);

            if (!redisVer.HasValue)
            {
                _logger.LogDebug("L1 evict (Redis key gone): {Key}", memKey);
                EvictL1(memKey);
                return false;
            }

            var l1Ver = _l1Versions.GetValueOrDefault(memKey, 0L);
            if ((long)redisVer == l1Ver)
                return true;

            _logger.LogDebug(
                "L1 stale (local v{Local} != redis v{Remote}): {Key}",
                l1Ver, (long)redisVer, memKey);

            EvictL1(memKey);
            return false;
        }
        catch (RedisException ex)
        {
            // Redis unavailable — serve stale L1 rather than fail the caller
            _logger.LogWarning(ex, "Redis unavailable during version check — serving stale L1 for {Key}", memKey);
            return true;
        }
    }

    private async ValueTask<T?> FetchFromRedisAsync(string memKey, string redisKey)
    {
        try
        {
            var entries = await _db.HashGetAsync(
                redisKey,
                new RedisValue[] { VersionField, DataField }
            ).ConfigureAwait(false);

            if (!entries[1].HasValue)
            {
                _logger.LogDebug("L2 miss: {Key}", memKey);
                return null;
            }

            var value = _serializer.DeserializeFromBytes((byte[])entries[1]!);
            if (value is null)
            {
                _logger.LogWarning("L2 deserialization returned null for {Key}", memKey);
                return null;
            }

            var version = entries[0].HasValue ? (long)entries[0] : 0L;
            PopulateL1(memKey, value, version);

            _logger.LogDebug("L2 hit: {Key} v{Version}", memKey, version);
            return value;
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable during L2 fetch for {Key}", memKey);
            return null;
        }
    }

    private void PopulateL1(string memKey, T value, long version)
    {
        _l1Versions[memKey] = version;

        var entryOptions = _options.UseSlidingExpiration
            ? new MemoryCacheEntryOptions
            {
                SlidingExpiration = _options.MemoryTtl
            }.RegisterPostEvictionCallback((key, _, _, _) =>
            {
                _l1Versions.TryRemove(key.ToString()!, out _);
            })
            : new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _options.MemoryTtl
            }.RegisterPostEvictionCallback((key, _, _, _) =>
            {
                _l1Versions.TryRemove(key.ToString()!, out _);
            });

        _memory.Set(memKey, value, entryOptions);
    }

    private void EvictL1(string memKey)
    {
        _memory.Remove(memKey);
        _l1Versions.TryRemove(memKey, out _);
    }

    private async ValueTask PublishInvalidationAsync(string memoryKey)
    {
        try
        {
            await _subscriber.PublishAsync(RedisChannel.Literal(_options.InvalidationChannel), memoryKey).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to publish invalidation for {Key}", memoryKey);
        }
    }

    private async ValueTask RefreshRedisTtlAsync(string redisKey)
    {
        try
        {
            await _db.KeyExpireAsync(redisKey, _options.RedisTtl).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Failed to refresh TTL for {Key}", redisKey);
        }
    }

    private string BuildRedisKey(string id) => $"{_options.KeyPrefix}:{id}";
    private string BuildMemoryKey(string id) => $"{typeof(T).FullName}:{_options.KeyPrefix}:{id}";
}