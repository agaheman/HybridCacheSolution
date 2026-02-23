# HybridCache (.NET 9)

Production-grade Hybrid Cache using Redis Hash + IMemoryCache.

Generated: 2026-02-23T10:33:25.212181 UTC

## Features
- Atomic Lua Version Increment
- Pub/Sub Invalidation
- Sliding Expiration
- Binary (MessagePack) or JSON Serialization
- Redis Cluster Support
- Configurable via appsettings.json

## Redis Layout
{prefix}:{id} (HASH)
- ver
- data
