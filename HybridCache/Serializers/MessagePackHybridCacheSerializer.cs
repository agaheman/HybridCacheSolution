using System.Buffers;
using MessagePack;
using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Serializers;

/// <summary>
/// MessagePack binary implementation of <see cref="IHybridCacheSerializer{T}"/> from
/// Microsoft.Extensions.Caching.Hybrid. Used when <c>UseBinarySerializer = true</c>.
/// Types must be decorated with <c>[MessagePackObject]</c>.
/// Offers ~3–5x better throughput than JSON for large payloads.
/// </summary>
internal sealed class MessagePackHybridCacheSerializer<T> : IHybridCacheSerializer<T>
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

    public T Deserialize(ReadOnlySequence<byte> source)
        => MessagePackSerializer.Deserialize<T>(source, Options);

    public void Serialize(T value, IBufferWriter<byte> target)
        => MessagePackSerializer.Serialize(target, value, Options);
}
