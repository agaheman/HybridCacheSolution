using System.Buffers;
using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Serializers;

/// <summary>
/// Bridges <see cref="IHybridCacheSerializer{T}"/> (buffer-based) to the
/// byte-array contract required by Redis hash field storage.
/// </summary>
internal static class SerializerExtensions
{
    public static byte[] SerializeToBytes<T>(this IHybridCacheSerializer<T> serializer, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    public static T DeserializeFromBytes<T>(this IHybridCacheSerializer<T> serializer, byte[] bytes)
    {
        var sequence = new ReadOnlySequence<byte>(bytes);
        return serializer.Deserialize(sequence);
    }
}
