using System.Buffers;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using System.Text.Json;
using Deviant.Assets;

namespace Deviant.Catalog;

internal static class BoundedFeatureJson
{
    private static readonly JsonSerializerOptions CacheOptions = CreateCacheOptions();

    public static string SerializeAsset(AssetDescriptor asset) => JsonSerializer.Serialize(asset, CacheOptions);

    public static string Serialize<T>(T value, int limit)
    {
        var buffer = new BoundedBuffer(limit);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, CacheOptions); writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonSerializerOptions CreateCacheOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Type != typeof(ResourceIdentity)) return;
            foreach (var property in typeInfo.Properties)
                if (property.Name == nameof(ResourceIdentity.Key))
                    property.ShouldSerialize = static (_, _) => false;
        });
        return new() { TypeInfoResolver = resolver };
    }

    private sealed class BoundedBuffer(int limit) : IBufferWriter<byte>
    {
        private byte[] bytes = [];
        private int written;
        public ReadOnlySpan<byte> WrittenSpan => bytes.AsSpan(0, written);
        public void Advance(int count)
        {
            if (count < 0 || count > bytes.Length - written) throw new InvalidDataException("The feature serializer exceeded its buffer.");
            written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return bytes.AsMemory(written); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return bytes.AsSpan(written); }
        private void Ensure(int hint)
        {
            if (hint < 0) throw new ArgumentOutOfRangeException(nameof(hint));
            hint = Math.Max(1, hint);
            if (hint > limit - written) throw new InvalidDataException("The feature JSON exceeds its size limit.");
            if (hint <= bytes.Length - written) return;
            var capacity = Math.Max(written + hint, Math.Min(limit, Math.Max(256, bytes.Length * 2)));
            var next = new byte[capacity]; bytes.AsSpan(0, written).CopyTo(next); bytes = next;
        }
    }
}
