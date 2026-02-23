# HybridCache

Production-grade two-level cache for ASP.NET Core (.NET 9).  
**L1:** `IMemoryCache` (in-process, ~0ms) | **L2:** Redis Hash (shared, ~1ms)

Cross-instance coherence via Redis pub/sub. Optional strong consistency via per-read version checking. Zero domain coupling — your types need no interface, no base class, no version property.

---

## Requirements

- .NET 9+
- Redis 6.0+ (single node or sentinel; cluster reserved for future release)
- `IMemoryCache` registered in DI (`AddMemoryCache()`)
- `IConnectionMultiplexer` registered in DI (StackExchange.Redis)

---

## Installation

Add a project reference to `HybridCache`:

```xml
<ProjectReference Include="..\HybridCache\HybridCache.csproj" />
```

---

## Quick Start

### 1. Define your model

No interface required. For MessagePack support add the attributes; for JSON-only they are optional.

```csharp
using MessagePack;

[MessagePackObject]
public sealed class UserSession
{
    [Key(0)] public string UserId  { get; set; } = string.Empty;
    [Key(1)] public string Token   { get; set; } = string.Empty;
    [Key(2)] public bool   IsAdmin { get; set; }
}
```

### 2. Register in `Program.cs`

```csharp
using HybridCache.Extensions;
using HybridCache.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("Redis connection string missing.")));

builder.Services.Configure<HybridCacheOptions>(
    builder.Configuration.GetSection("HybridCache"));

// Registers: IHybridCache<T>, serializer, invalidation listener, options validator
builder.Services.AddHybridCache<UserSession>();
```

### 3. Configure `appsettings.json`

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379,connectRetry=5,abortConnect=false"
  },
  "HybridCache": {
    "KeyPrefix":             "sessions",
    "MemoryTtl":             "00:00:30",
    "RedisTtl":              "00:15:00",
    "UseSlidingExpiration":  true,
    "RefreshRedisTtlOnRead": false,
    "CheckVersionOnRead":    false,
    "UseBinarySerializer":   false,
    "InvalidationChannel":   "hybrid-cache-invalidated"
  }
}
```

### 4. Inject and use

```csharp
using HybridCache.Abstractions;

public sealed class SessionService
{
    private readonly IHybridCache<UserSession> _cache;

    public SessionService(IHybridCache<UserSession> cache)
        => _cache = cache;

    public ValueTask<UserSession?> GetAsync(string sessionId, CancellationToken ct = default)
        => _cache.GetAsync(sessionId, ct);

    public ValueTask SaveAsync(UserSession session, CancellationToken ct = default)
        => _cache.SetAsync(session.UserId, session, ct);

    public ValueTask InvalidateAsync(string sessionId, CancellationToken ct = default)
        => _cache.RemoveAsync(sessionId, ct);
}
```

---

## Caching Multiple Types

Call `AddHybridCache<T>()` once per type. Each type gets its own scoped `IHybridCache<T>`, serializer, and key namespace. They all share the single `IConnectionMultiplexer` and `IMemoryCache`.

```csharp
builder.Services.AddHybridCache<UserSession>();
builder.Services.AddHybridCache<ProductCatalog>();
builder.Services.AddHybridCache<FeatureFlags>();
```

The invalidation listener is registered once (not per type) — duplicate `AddHostedService` registrations are deduplicated by ASP.NET Core.

---

## Configuration Reference

| Key | Type | Default | Notes |
|---|---|---|---|
| `KeyPrefix` | string | *(required)* | Redis key prefix. E.g. `sessions` → Redis key: `sessions:{id}` |
| `MemoryTtl` | TimeSpan | `00:00:30` | Must be ≤ `RedisTtl` |
| `RedisTtl` | TimeSpan | `00:15:00` | Applied atomically via Lua EXPIRE |
| `UseSlidingExpiration` | bool | `true` | Sliding resets L1 TTL on access; absolute does not |
| `RefreshRedisTtlOnRead` | bool | `false` | Re-EXPIRE Redis key on every L1 hit |
| `CheckVersionOnRead` | bool | `false` | HGET `ver` on every L1 hit — +1 Redis RTT, strong consistency |
| `UseBinarySerializer` | bool | `false` | `true` = MessagePack+LZ4, `false` = JSON (camelCase) |
| `InvalidationChannel` | string | `hybrid-cache-invalidated` | Redis pub/sub channel for cross-instance L1 eviction |
| `EnableClusterMode` | bool | `false` | Reserved — not yet implemented |

---

## Serialization

### JSON (default)

Works with any POCO. No attributes needed. Readable in RedisInsight.

```json
"UseBinarySerializer": false
```

### MessagePack + LZ4

~3–5× smaller payload and faster serialization. Requires `[MessagePackObject]` and `[Key(n)]` attributes.

```json
"UseBinarySerializer": true
```

```csharp
[MessagePackObject]
public sealed class ProductCatalog
{
    [Key(0)] public string  Id       { get; set; } = string.Empty;
    [Key(1)] public string  Name     { get; set; } = string.Empty;
    [Key(2)] public decimal Price    { get; set; }
    [Key(3)] public int     Stock    { get; set; }
}
```

---

## Consistency Modes

### Default: Eventual Consistency (`CheckVersionOnRead = false`)

```
L1 hit → return immediately (no Redis call)
```

L1 stays coherent via pub/sub invalidation. Maximum stale window = `MemoryTtl`. Best for high-throughput read scenarios where a sub-second stale window is acceptable.

### Strong Read Consistency (`CheckVersionOnRead = true`)

```
L1 hit → HGET ver → compare with _l1Versions → evict if mismatch → fetch fresh
```

Every L1 hit costs one extra Redis `HGET` (~1ms). Use when:
- Multiple app instances can write to the same keys concurrently
- You cannot tolerate a stale `MemoryTtl`-sized window between write and read

```json
"CheckVersionOnRead": true
```

---

## Redis Outage Behaviour

The library degrades gracefully — it never throws to the caller for Redis connectivity issues.

| Operation | Redis Down Behaviour |
|---|---|
| `GetAsync` — L1 hit | Serves stale L1 (availability preferred) |
| `GetAsync` — L1 miss | Returns `null` |
| `SetAsync` | Writes L1-only (version 0). Self-heals when Redis recovers |
| `RemoveAsync` | Evicts L1 locally. Redis DEL and pub/sub silently skipped |

All Redis failures are logged at `Warning` level with the key and exception detail.

---

## Logging

Set log level per namespace in `appsettings.json`:

```json
"Logging": {
  "LogLevel": {
    "HybridCache": "Debug"
  }
}
```

| Level | Events |
|---|---|
| `Debug` | L1 hit, L1 miss, L2 hit, L2 miss, version mismatch, eviction, stampede lock |
| `Warning` | Redis unavailable (any operation), publish failure, deserialization null |
| `Information` | Invalidation listener started / stopped |

---

## Redis Key Layout

```
{KeyPrefix}:{id}    (Hash)
  ver   → INT64, monotonically increasing (1, 2, 3, ...)
  data  → BYTES, serialized payload
```

Example with `KeyPrefix = "sessions"` and `id = "user-abc"`:
```
HGETALL sessions:user-abc
1) "ver"
2) "3"
3) "data"
4) "{\"userId\":\"user-abc\",\"token\":\"...\",\"isAdmin\":false}"
```

---

## Known Constraints

- **Manual Redis edits:** If you modify `data` directly in Redis without incrementing `ver`, the change will not be detected (even with `CheckVersionOnRead = true`). This is accepted — direct Redis edits are an ops tool action, not a production flow.
- **Pub/Sub is fire-and-forget:** A dropped message means a remote L1 is not immediately invalidated. Self-heals within `MemoryTtl`.
- **No cross-instance stampede protection:** The per-key lock is in-process. On cold start across many instances, each instance makes one Redis fetch.
- **Redis Cluster:** `EnableClusterMode` is reserved. Not yet implemented.
