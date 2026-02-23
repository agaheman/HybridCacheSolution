using MessagePack;

namespace HybridCache.ApiTest.Models;

/// <summary>
/// Sample cache model. No IVersioned required — version is managed
/// internally by the cache infrastructure via the Redis 'ver' hash field.
/// </summary>
[MessagePackObject]
public sealed class CacheToken
{
    [Key(0)]
    public string Value { get; set; } = string.Empty;
}
