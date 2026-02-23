namespace HybridCache.Abstractions;
public interface IHybridCacheSerializer
{
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(byte[] payload);
}