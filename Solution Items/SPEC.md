# HybridCache — Technical Specification

> **Version:** 1.0.0  
> **Target Framework:** .NET 9  
> **Last Updated:** 2026-02-24  
> **Status:** Production

---

## 1. Purpose

`HybridCache` is a production-grade, two-level distributed cache library for ASP.NET Core applications. It combines an in-process L1 memory cache (`IMemoryCache`) with an out-of-process L2 Redis Hash store, providing sub-millisecond reads on L1 hits while maintaining cross-instance coherence via Redis pub/sub invalidation and optional version checking.

Domain types require **no interface, no base class, and no version property** — versioning is a pure infrastructure concern managed internally.

---

## 2. Scope

| In Scope | Out of Scope |
|---|---|
| L1 (IMemoryCache) + L2 (Redis Hash) two-level cache | Distributed locking across instances |
| Atomic version increment via Lua | Redis Keyspace Notification subscription |
| Cross-instance L1 invalidation via pub/sub | Cache warming / pre-population strategies |
| Per-key stampede protection (SemaphoreSlim) | Persistent storage or write-through to DB |
| JSON and MessagePack serialization | Manual Redis CLI edits producing pub/sub events |
| Graceful Redis degradation (L1-only fallback) | Multi-tenancy key namespacing |
| Configurable version-check-on-read | Tag-based or group invalidation |

---

## 3. Architecture

### 3.1 Layer Model

```
┌─────────────────────────────────────────────────────┐
│                  Application Layer                   │
│          IHybridCache<T>  (injected via DI)          │
└──────────────────────┬──────────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────────┐
│                 HybridCache<T>                        │
│                                                       │
│  ┌──────────────────┐    ┌────────────────────────┐  │
│  │   L1: IMemory    │    │  _l1Versions           │  │
│  │   Cache          │◄───│  ConcurrentDictionary  │  │
│  │   (in-process)   │    │  <string, long>        │  │
│  └────────┬─────────┘    └────────────────────────┘  │
│           │ miss / stale                              │
│  ┌────────▼─────────┐    ┌────────────────────────┐  │
│  │   KeyLockProvider│    │  IHybridCacheSerializer │  │
│  │   (per-key       │    │  JSON | MessagePack     │  │
│  │    SemaphoreSlim)│    └────────────────────────┘  │
│  └────────┬─────────┘                                 │
└───────────┼─────────────────────────────────────────┘
            │
┌───────────▼─────────────────────────────────────────┐
│               L2: Redis Hash                          │
│                                                       │
│  Key:   {prefix}:{id}                                 │
│  Field  ver  → HINCRBY (atomic, monotonic)            │
│  Field  data → serialized payload (bytes)             │
│  TTL:   EXPIRE set atomically via Lua                 │
└───────────┬─────────────────────────────────────────┘
            │ pub/sub
┌───────────▼─────────────────────────────────────────┐
│         HybridCacheInvalidationListener              │
│         (IHostedService, per app instance)            │
│         Channel: {InvalidationChannel}               │
│         On message: IMemoryCache.Remove(key)         │
└─────────────────────────────────────────────────────┘
```

### 3.2 Project Structure

```
HybridCache/
├── Abstractions/
│   └── IHybridCache.cs              # Public contract
├── Extensions/
│   └── ServiceCollectionExtensions.cs  # AddHybridCache<T>()
├── Internal/
│   ├── HybridCacheLuaScripts.cs     # Pre-compiled Lua (AtomicSetScript)
│   └── KeyLockProvider.cs           # Per-key SemaphoreSlim with leak prevention
├── Options/
│   ├── HybridCacheOptions.cs        # Configuration model
│   └── HybridCacheOptionsValidator.cs  # IValidateOptions<T> startup check
├── Serializers/
│   ├── JsonHybridCacheSerializer.cs
│   ├── MessagePackHybridCacheSerializer.cs
│   └── SerializerExtensions.cs      # IBufferWriter ↔ byte[] bridge
└── Services/
    ├── HybridCache.cs               # Core implementation
    └── HybridCacheInvalidationListener.cs  # IHostedService pub/sub listener
```

---

## 4. Core Contracts

### 4.1 IHybridCache\<T\>

```csharp
public interface IHybridCache<T> where T : class
{
    ValueTask<T?> GetAsync(string id, CancellationToken ct = default);
    ValueTask SetAsync(string id, T value, CancellationToken ct = default);
    ValueTask RemoveAsync(string id, CancellationToken ct = default);
}
```

- `T` requires only `class`. No version interface, no marker interface.
- All methods return `ValueTask` to avoid heap allocations on the hot L1-hit path.
- `CancellationToken` is propagated to Redis calls and the stampede lock.

### 4.2 HybridCacheOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `KeyPrefix` | `string` | *(required)* | Redis key prefix, e.g. `auth:token` |
| `MemoryTtl` | `TimeSpan` | `00:00:30` | L1 entry lifetime |
| `RedisTtl` | `TimeSpan` | `00:15:00` | L2 entry lifetime (set via EXPIRE) |
| `UseSlidingExpiration` | `bool` | `true` | Sliding vs absolute for L1 |
| `RefreshRedisTtlOnRead` | `bool` | `false` | Re-EXPIRE Redis key on L1 hit |
| `CheckVersionOnRead` | `bool` | `false` | HGET `ver` on every L1 hit for strong consistency |
| `InvalidationChannel` | `string` | `hybrid-cache-invalidated` | Redis pub/sub channel name |
| `UseBinarySerializer` | `bool` | `false` | MessagePack (true) vs JSON (false) |
| `EnableClusterMode` | `bool` | `false` | Reserved for Redis Cluster topology |

**Validation rules (enforced at startup via `IValidateOptions<T>`):**
- `KeyPrefix` must not be null or whitespace
- `MemoryTtl` must be positive
- `RedisTtl` must be positive
- `MemoryTtl` must not exceed `RedisTtl`
- `InvalidationChannel` must not be null or whitespace

---

## 5. Detailed Flows

### 5.1 GetAsync — Full Decision Tree

```
GetAsync(id)
│
├─ L1 hit?
│   ├─ YES
│   │   ├─ CheckVersionOnRead = true?
│   │   │   ├─ YES → HGET {redisKey} ver
│   │   │   │   ├─ Redis key gone?     → EvictL1 → fall to L2 fetch
│   │   │   │   ├─ ver mismatch?       → EvictL1 → fall to L2 fetch
│   │   │   │   ├─ Redis unreachable?  → serve stale L1 (availability > consistency)
│   │   │   │   └─ ver match?          → continue ↓
│   │   │   └─ NO → skip version check
│   │   │
│   │   ├─ RefreshRedisTtlOnRead = true? → EXPIRE {redisKey} RedisTtl (best-effort)
│   │   └─ return cached value ✅
│   │
│   └─ NO (L1 miss or evicted)
│       │
│       └─ AcquireAsync(memKey)   ← per-key SemaphoreSlim (stampede guard)
│           │
│           ├─ L1 re-check (another waiter may have populated)
│           │   └─ hit? → return cached value ✅ (release lock)
│           │
│           └─ FetchFromRedisAsync(memKey, redisKey)
│               │
│               ├─ HMGET {redisKey} ver data
│               ├─ data missing?        → return null ✅
│               ├─ deserialize payload
│               ├─ PopulateL1(value, version)
│               │   ├─ _l1Versions[memKey] = version
│               │   └─ IMemoryCache.Set + PostEvictionCallback (cleans _l1Versions)
│               ├─ Redis unreachable?   → return null ✅ (graceful degrade)
│               └─ return value ✅
```

### 5.2 SetAsync Flow

```
SetAsync(id, value)
│
├─ Serialize value → byte[]  (IHybridCacheSerializer<T>)
├─ ScriptEvaluateAsync(AtomicSetScript)
│   └─ Lua (atomic):
│       HINCRBY {key} ver 1    → newVersion
│       HSET    {key} data {payload}
│       EXPIRE  {key} {RedisTtl}
│       return  newVersion
│
├─ PopulateL1(memKey, value, newVersion)
│   ├─ _l1Versions[memKey] = newVersion
│   └─ IMemoryCache.Set(value, TTL options + eviction callback)
│
├─ PublishAsync(InvalidationChannel, memKey)
│   └─ → all other instances receive → IMemoryCache.Remove(memKey)
│
└─ Redis unreachable?
    └─ PopulateL1(value, version: 0)   [L1-only fallback, no pub/sub]
```

### 5.3 RemoveAsync Flow

```
RemoveAsync(id)
│
├─ EvictL1(memKey)
│   ├─ IMemoryCache.Remove(memKey)
│   └─ _l1Versions.TryRemove(memKey)
│
├─ KeyDeleteAsync({redisKey})
├─ PublishAsync(InvalidationChannel, memKey)
│   └─ → all other instances receive → IMemoryCache.Remove(memKey)
│
└─ Redis unreachable? → log warning only (L1 already evicted locally)
```

### 5.4 Cross-Instance Invalidation Flow

```
Instance A                    Redis                     Instance B
    │                           │                           │
    │  SetAsync("user:1")       │                           │
    │──────────────────────────►│                           │
    │  Lua: HINCRBY ver         │                           │
    │  HSET data                │                           │
    │  EXPIRE                   │                           │
    │◄──────────────────────────│                           │
    │  newVersion = 5           │                           │
    │                           │                           │
    │  PUBLISH                  │                           │
    │  hybrid-cache-invalidated │                           │
    │  memKey                   │──────────────────────────►│
    │──────────────────────────►│    IMemoryCache.Remove    │
    │                           │    (memKey)               │
    │                           │◄──────────────────────────│
    │                           │                           │
    │                           │   GetAsync("user:1")      │
    │                           │◄──────────────────────────│
    │                           │   HMGET ver data (L2 hit) │
    │                           │──────────────────────────►│
    │                           │   PopulateL1(v5)          │
    │                           │◄──────────────────────────│
```

### 5.5 Stampede Protection Flow

```
  10 concurrent requests for cache miss on key "X"
  │
  ├─ All 10 → L1 miss
  ├─ All 10 → AcquireAsync("X")
  │
  ├─ Request 1 → acquires semaphore → FetchFromRedisAsync → PopulateL1
  │
  └─ Requests 2–10 → waiting on semaphore
      └─ When Request 1 releases:
          └─ Request 2 acquires → L1 re-check → HIT → return (no Redis call)
          └─ Requests 3–10 → same (each re-checks L1, each hits)
  
  Result: 1 Redis round-trip instead of 10 ✅
```

---

## 6. Redis Data Model

```
Key:    {KeyPrefix}:{id}          (Redis Hash)
Fields:
  ver   INT64    Monotonically increasing version counter (HINCRBY, starts at 1)
  data  BYTES    Serialized payload (JSON UTF-8 or MessagePack+LZ4)
TTL:    Set atomically with EXPIRE inside Lua script on every write
```

**Example:**
```
HGETALL auth:token:user-abc-123
1) "ver"
2) "7"
3) "data"
4) "{\"value\":\"eyJhbGciOiJIUzI1NiJ9...\"}"
```

---

## 7. Lua Script Specification

```lua
-- AtomicSetScript
-- Guarantees: ver increment, data write, and TTL reset are one atomic operation.
-- No WATCH/MULTI/EXEC needed — single Lua call is atomic in Redis.
local key  = KEYS[1]
local ttl  = tonumber(ARGV[1])
local data = ARGV[2]
local ver  = redis.call('HINCRBY', key, 'ver', 1)
redis.call('HSET',   key, 'data', data)
redis.call('EXPIRE', key, ttl)
return ver   -- returned to caller as the new version number
```

**Properties:**
- Atomic: no partial writes possible
- `ver` starts at 1 on first write (HINCRBY on non-existent field initialises to 0, then increments)
- Pre-compiled via `LuaScript.Prepare()` — SHA cached by StackExchange.Redis, no re-send on repeat calls

---

## 8. Serialization

| Strategy | Class | Format | When to Use |
|---|---|---|---|
| JSON | `JsonHybridCacheSerializer<T>` | UTF-8 JSON, camelCase | Default. Human-readable, debuggable in Redis |
| MessagePack | `MessagePackHybridCacheSerializer<T>` | Binary + LZ4 block compression | High-throughput, large payloads, ~3–5× smaller |

**MessagePack requirements:** types must be decorated with `[MessagePackObject]` and `[Key(n)]` on each property.

Both implement `IHybridCacheSerializer<T>` from `Microsoft.Extensions.Caching.Hybrid`, using `IBufferWriter<byte>` / `ReadOnlySequence<byte>` for zero-copy buffer operations. A `SerializerExtensions` adapter bridges to `byte[]` for Redis storage.

---

## 9. Version Tracking — Internal Design

Version state is decoupled from domain objects via `ConcurrentDictionary<string, long> _l1Versions`:

```
_l1Versions["MyApp.Models.CacheToken:auth:token:user-1"] = 5L
```

**Lifecycle:**
- Written in `PopulateL1()` with the version returned from Redis or Lua
- Read in `CheckVersionAsync()` to compare against Redis live `ver`
- Cleaned up automatically via `MemoryCacheEntryOptions.RegisterPostEvictionCallback` — when `IMemoryCache` evicts a key (TTL, memory pressure, or explicit Remove), the corresponding `_l1Versions` entry is removed immediately

**Why not on the domain object?**
- Domain types remain clean — no infrastructure coupling
- Version is meaningless outside the cache boundary
- Allows caching of third-party types or sealed classes without modification

---

## 10. Consistency Model

| Mode | Consistency Level | Extra Cost |
|---|---|---|
| `CheckVersionOnRead = false` (default) | **Eventual** — pub/sub delivers invalidation; L1 TTL is the maximum stale window | Zero extra Redis calls on L1 hit |
| `CheckVersionOnRead = true` | **Strong read** — every L1 hit verified against Redis `ver` | +1 HGET per L1 hit (~1ms RTT) |

**Redis outage behaviour (both modes):**
- `GetAsync` — serves stale L1 if available, returns `null` on L1 miss
- `SetAsync` — writes L1-only (version 0), no pub/sub
- `RemoveAsync` — evicts L1 locally, Redis DEL and pub/sub silently skipped

---

## 11. Known Constraints

1. **Manual Redis edits without `ver` change are not detected** — ops/dev tool edits that modify `data` directly without incrementing `ver` will not be detected even with `CheckVersionOnRead = true`. This is accepted by design.

2. **Pub/Sub is fire-and-forget** — Redis pub/sub does not guarantee delivery. A dropped message means a remote instance's L1 is not immediately invalidated. It self-heals within `MemoryTtl` or on the next `CheckVersionOnRead` cycle.

3. **`HybridCacheInvalidationListener` shares `IMemoryCache` with all `HybridCache<T>` instances** — the listener removes by raw `memKey` string. If two different `HybridCache<T>` types share the same `KeyPrefix` and `id`, both will be evicted on an invalidation message for either. `memKey` is namespaced by `typeof(T).FullName` to prevent this in practice.

4. **No cross-instance stampede protection** — the `KeyLockProvider` is in-process only. Under a cold-start scenario across many instances simultaneously, each instance will make one Redis fetch. This is acceptable; only intra-process stampede is protected.

5. **`SetAsync` during Redis outage produces version 0** — if Redis recovers and another instance reads the key, the Redis version will be ahead of version 0, causing an immediate eviction and re-fetch from Redis. This is the correct self-healing behaviour.

6. **`EnableClusterMode` is reserved** — the flag exists in options but is not yet implemented. Cluster support requires per-slot key routing and per-node pub/sub subscription.

---

## 12. Dependencies

| Package | Version | Purpose |
|---|---|---|
| `StackExchange.Redis` | 2.11.3 | Redis client, pub/sub, Lua scripting |
| `MessagePack` | 3.1.4 | Binary serialization with LZ4 compression |
| `Microsoft.Extensions.Caching.Hybrid` | 9.3.0 | `IHybridCacheSerializer<T>` contract |
| `Microsoft.Extensions.Caching.Memory` | 9.0.10 | `IMemoryCache` L1 |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 9.0.10 | Options binding and `IValidateOptions<T>` |
| `Microsoft.Extensions.Hosting.Abstractions` | 9.0.5 | `IHostedService` for invalidation listener |
| `Microsoft.Extensions.Logging.Abstractions` | 9.0.5 | `ILogger<T>` structured logging |
