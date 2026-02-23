using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Serializers;

/// <summary>
/// JSON implementation of <see cref="IHybridCacheSerializer{T}"/> from
/// Microsoft.Extensions.Caching.Hybrid. Used when <c>UseBinarySerializer = false</c>.
/// </summary>
internal sealed class JsonHybridCacheSerializer<T> : IHybridCacheSerializer<T>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

    public T Deserialize(ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source);
        return JsonSerializer.Deserialize<T>(ref reader, JsonOptions)
               ?? throw new InvalidDataException($"JSON deserialization returned null for type {typeof(T).Name}.");
    }

    public void Serialize(T value, IBufferWriter<byte> target)
    {
        using var writer = new Utf8JsonWriter(target);
        JsonSerializer.Serialize(writer, value, JsonOptions);
    }
}
