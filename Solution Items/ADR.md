# Architecture Decision Records — HybridCache

> ADRs document significant design decisions: the context that drove them, the options considered, the decision made, and the consequences accepted. They are written to be read by future maintainers — including yourself six months from now.

---

## ADR-001: Two-Level Cache Architecture (L1 Memory + L2 Redis)

**Date:** 2026-02-24  
**Status:** Accepted

### Context

The system requires a distributed cache shared across multiple ASP.NET Core instances. A pure Redis solution is correct but adds ~1–5ms network latency on every cache read. A pure in-memory solution is fast but not shared — cache misses and invalidation cannot be coordinated across instances.

### Decision

Implement a two-level hierarchy: L1 (`IMemoryCache`, in-process, ~0ms) backed by L2 (Redis Hash, shared, ~1–5ms). L1 serves the hot path. L2 is the source of truth for data and versioning.

### Options Considered

| Option | Pros | Cons |
|---|---|---|
| Redis only | Simple, always consistent | ~1–5ms per read, higher Redis CPU |
| IMemoryCache only | ~0ms, no infra | Not shared across instances |
| L1 + L2 hybrid | Fast hot path + shared L2 | More complexity, coherence to manage |
| Distributed IMemoryCache (NCache, etc.) | Transparent | Vendor lock-in, licensing cost |

### Consequences

- Hot path is ~0ms on L1 hit.
- L1 coherence is eventually consistent by default; strong consistency opt-in via `CheckVersionOnRead`.
- Added complexity: version tracking, pub/sub listener, stampede guard.

---

## ADR-002: Redis Hash as Storage Format (not String)

**Date:** 2026-02-24  
**Status:** Accepted

### Context

Redis supports multiple data structures for key-value storage. The most common cache pattern uses `SET`/`GET` on a String key with the entire payload as value. We need to store both the version counter and the data payload together, atomically.

### Decision

Use Redis **Hash** with two fields: `ver` (INT64 version counter) and `data` (bytes payload). A single key maps to both fields.

### Options Considered

| Option | Atomic version+data? | Notes |
|---|---|---|
| Two separate String keys (`key:ver`, `key:data`) | No — MULTI/EXEC needed | Race window between two commands |
| Single String key (encode ver+data together) | Yes (single SET) | Custom framing required, error-prone |
| Redis Hash (single key, two fields) | Yes (via Lua) | Clean separation, Lua makes it atomic |

### Consequences

- Lua script (`HINCRBY` + `HSET` + `EXPIRE`) executes atomically — no race condition possible.
- `ver` is readable independently (`HGET key ver`) for cheap version checks without fetching payload.
- `HGETALL` / `HMGET` are slightly more verbose than `GET`, but negligibly so.

---

## ADR-003: Atomic Version Increment via Lua Script

**Date:** 2026-02-24  
**Status:** Accepted

### Context

Every write must atomically: increment the version, store the new payload, and reset the TTL. If these three operations are not atomic, a reader could observe a new `ver` with old `data`, or the key could expire mid-write.

### Decision

Use a pre-compiled Lua script (`LuaScript.Prepare`) that executes `HINCRBY`, `HSET`, and `EXPIRE` as a single atomic Redis command.

### Options Considered

| Option | Atomic? | Notes |
|---|---|---|
| `MULTI`/`EXEC` transaction | Yes | Requires two round-trips (MULTI + EXEC), more verbose |
| Optimistic locking (`WATCH`) | Conditional | Retry loop on contention, complex |
| Lua script | Yes | Single round-trip, no retry logic needed |
| Pipeline (non-transactional) | No | Commands not atomic |

### Consequences

- `LuaScript.Prepare` compiles the script once at startup and uses EVALSHA on subsequent calls — no re-sending the script body.
- Lua runs single-threaded in Redis — no other command can interleave.
- Script must be re-loaded after a Redis restart or SCRIPT FLUSH. StackExchange.Redis handles this transparently by falling back to EVAL if EVALSHA returns NOSCRIPT.

---

## ADR-004: Version Tracking Decoupled from Domain Objects (No IVersioned)

**Date:** 2026-02-24  
**Status:** Accepted

### Context

An earlier version of the library required cached types to implement `IVersioned { long Version { get; set; } }`. This forced a cache infrastructure concern onto domain models, violated the Dependency Inversion Principle, and prevented caching of third-party or sealed types.

### Decision

Remove `IVersioned`. Track versions internally in `HybridCache<T>` via `ConcurrentDictionary<string, long> _l1Versions`. The domain type constraint is reduced to `where T : class`.

### Options Considered

| Option | Domain coupling | Notes |
|---|---|---|
| `IVersioned` interface on T | High — infects all cached types | Simple to implement |
| Wrapper/envelope `CacheEntry<T>` | Medium — wrapping at boundary | Extra allocation per entry |
| Internal `ConcurrentDictionary<string, long>` | None | Version invisible to domain layer |

### Consequences

- Domain types are clean — no cache interface needed.
- Third-party types, records, and sealed classes can be cached without modification.
- `_l1Versions` must be kept in sync with `IMemoryCache` — handled via `PostEvictionCallback` registered on every cache entry, ensuring the version map self-cleans when L1 evicts.
- Slight indirection: version is not on the object but looked up by memory key string.

---

## ADR-005: Per-Key SemaphoreSlim for Stampede Protection

**Date:** 2026-02-24  
**Status:** Accepted

### Context

On a cache miss, concurrent requests for the same key will all attempt to fetch from Redis simultaneously — the "thundering herd" or "cache stampede" problem. Under high concurrency this produces N redundant Redis calls for N concurrent waiters on the same key.

### Decision

Use `KeyLockProvider`, a `ConcurrentDictionary<string, SemaphoreSlim>` that provides per-key async mutual exclusion. Only the first waiter fetches from Redis; subsequent waiters re-check L1 after acquiring the lock and return the already-populated value.

### Options Considered

| Option | Granularity | Notes |
|---|---|---|
| Global `SemaphoreSlim(1,1)` | All keys blocked | Serialises all cache misses globally — unacceptable |
| `AsyncLazy<T>` per key | Per key | Harder to evict, less reusable |
| `ConcurrentDictionary<string, SemaphoreSlim>` | Per key | Clean, reusable, leak-preventable |
| No protection | — | Thundering herd on every cold start |

### Memory Leak Prevention

A naive per-key dictionary grows unboundedly for high-cardinality key spaces (e.g., one key per user session). `KeyLockProvider.Releaser.Dispose()` calls `TryRemove(key)` when `SemaphoreSlim.CurrentCount == 1` (idle). If another thread races and re-adds the key, both paths produce a valid `SemaphoreSlim(1,1)`.

### Consequences

- In-process protection only — cross-instance stampede is not prevented (accepted; see Constraint #4 in SPEC).
- `SemaphoreSlim` instances are garbage collected after eviction from the dictionary — no long-lived heap pressure.
- `CancellationToken` is passed to `WaitAsync` so callers can cancel while waiting for the lock.

---

## ADR-006: `IHybridCacheSerializer<T>` from Microsoft.Extensions.Caching.Hybrid

**Date:** 2026-02-24  
**Status:** Accepted

### Context

The library needs pluggable serialization. Defining a custom `IHybridCacheSerializer` interface would duplicate the contract that Microsoft already provides in `Microsoft.Extensions.Caching.Hybrid` (shipped in .NET 9).

### Decision

Implement `IHybridCacheSerializer<T>` from `Microsoft.Extensions.Caching.Hybrid` for both JSON and MessagePack serializers. This uses `IBufferWriter<byte>` and `ReadOnlySequence<byte>` — zero-copy buffer APIs.

### Options Considered

| Option | Notes |
|---|---|
| Custom `ISerializer` interface | Duplicates the .NET 9 standard; extra maintenance |
| `System.Text.Json` directly | Not swappable; JSON coupled to core service |
| `IHybridCacheSerializer<T>` (Microsoft) | Standard contract, zero-copy buffers, extensible |

### Consequences

- `SerializerExtensions` bridge (`SerializeToBytes` / `DeserializeFromBytes`) adapts the buffer API to `byte[]` for Redis Hash field storage. Minor allocation on each call — acceptable given Redis I/O dominates.
- Consumers can register their own `IHybridCacheSerializer<T>` in DI to override without changing library code.
- Dependency on `Microsoft.Extensions.Caching.Hybrid` 9.3.0 is explicit in the `.csproj`.

---

## ADR-007: Pub/Sub Invalidation is Fire-and-Forget (Not Guaranteed Delivery)

**Date:** 2026-02-24  
**Status:** Accepted

### Context

Redis pub/sub does not guarantee message delivery. A subscriber that is disconnected during a publish will miss the message. This means cross-instance L1 invalidation via pub/sub is best-effort, not guaranteed.

### Decision

Accept eventual consistency as the default coherence model. Pub/sub is the primary invalidation mechanism. `CheckVersionOnRead` is provided as an opt-in to achieve strong read consistency at the cost of +1 Redis HGET per L1 hit.

### Options Considered

| Option | Consistency | Cost |
|---|---|---|
| Pub/sub only | Eventual | Zero extra reads |
| Pub/sub + `CheckVersionOnRead` | Strong read | +1 HGET per L1 hit |
| Redis Streams (guaranteed delivery) | Strong | Significant complexity; consumer groups required |
| Short `MemoryTtl` only | Eventual (bounded) | No explicit invalidation |
| Redis Keyspace Notifications | Strong | Requires server config; high Redis CPU |

### Consequences

- Dropped pub/sub messages self-heal within `MemoryTtl` seconds.
- `CheckVersionOnRead = true` closes the consistency gap entirely at the cost of one HGET per L1 hit.
- Redis Keyspace Notifications were explicitly ruled out — they require `notify-keyspace-events` on the Redis server (not always permitted in managed Redis) and generate significant overhead.

---

## ADR-008: Graceful Redis Degradation (Availability over Consistency)

**Date:** 2026-02-24  
**Status:** Accepted

### Context

Redis is an external dependency. Network partitions, Redis restarts, or maintenance windows will cause transient unavailability. The question is: should the cache throw to the caller, or degrade gracefully?

### Decision

All Redis operations are wrapped in `try/catch RedisException`. The library never throws to the caller for Redis connectivity failures. It degrades to L1-only mode and logs a `Warning`.

### Degradation Behaviour

| Operation | Redis Down |
|---|---|
| `GetAsync` (L1 hit, `CheckVersionOnRead = true`) | Serve stale L1 — availability preferred |
| `GetAsync` (L1 miss) | Return `null` |
| `SetAsync` | Write L1 only (version 0). Self-heals on Redis recovery |
| `RemoveAsync` | Evict L1 locally. Skip Redis DEL and pub/sub |

### Consequences

- Callers (application services) do not need to handle cache exceptions.
- During Redis outage, `SetAsync` writes version 0 to L1. When Redis recovers and the key is read from another instance, the Redis `ver` will be ahead of 0, causing an immediate re-fetch — correct self-healing behaviour.
- Accepting stale data during outage is the correct trade-off for a cache layer. The database remains the source of truth.

---

## ADR-009: Options Validation via `IValidateOptions<T>` at Startup

**Date:** 2026-02-24  
**Status:** Accepted

### Context

`HybridCacheOptions.KeyPrefix` was originally typed as `string` with `= default!` — a nullable suppression that would silently produce Redis keys like `:user-1` if the consumer forgot to configure `KeyPrefix`. Other misconfigurations (e.g. `MemoryTtl > RedisTtl`) would fail silently at runtime.

### Decision

Implement `HybridCacheOptionsValidator : IValidateOptions<HybridCacheOptions>`. ASP.NET Core calls this before the first request and throws `OptionsValidationException` with descriptive messages if any rule fails.

### Validated Rules

- `KeyPrefix` must not be null or whitespace
- `MemoryTtl` must be positive
- `RedisTtl` must be positive
- `MemoryTtl` must not exceed `RedisTtl`
- `InvalidationChannel` must not be null or whitespace

### Consequences

- Misconfiguration fails fast at startup with a clear error message, not silently at runtime.
- `KeyPrefix` default changed from `default!` to `string.Empty` — still invalid (fails validator), but no longer a null suppression.
- No additional runtime overhead — validation runs once.

---

## ADR-010: `RedisChannel.Literal()` over Implicit String Cast

**Date:** 2026-02-24  
**Status:** Accepted

### Context

StackExchange.Redis 2.x deprecated the implicit `string → RedisChannel` cast (CS0618). The replacement API requires explicitly choosing `RedisChannel.Literal(name)` (exact match) or `RedisChannel.Pattern(glob)` (wildcard subscription).

### Decision

Use `RedisChannel.Literal(_options.InvalidationChannel)` in all `Subscribe`, `Unsubscribe`, and `PublishAsync` calls. The invalidation channel is always a fixed, configured string — never a glob pattern.

### Consequences

- Eliminates all CS0618 warnings.
- Makes intent explicit: this is an exact-match channel subscription, not a pattern subscription.
- No behaviour change — the implicit cast defaulted to `Literal` mode anyway.
