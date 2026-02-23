using StackExchange.Redis;

namespace HybridCache.Internal;

/// <summary>
/// Pre-compiled Lua scripts for atomic Redis operations.
/// LuaScript.Prepare compiles once and reuses the SHA across calls.
/// </summary>
internal static class HybridCacheLuaScripts
{
    /// <summary>
    /// Atomically increments the version field, stores the payload, and sets TTL.
    /// Returns the new version number.
    /// KEYS[1] = redis hash key
    /// ARGV[1] = TTL in seconds
    /// ARGV[2] = serialized payload (bytes)
    /// </summary>
    public static readonly LuaScript AtomicSetScript = LuaScript.Prepare(@"
        local key  = KEYS[1]
        local ttl  = tonumber(ARGV[1])
        local data = ARGV[2]
        local ver  = redis.call('HINCRBY', key, 'ver', 1)
        redis.call('HSET',   key, 'data', data)
        redis.call('EXPIRE', key, ttl)
        return ver
    ");
}
