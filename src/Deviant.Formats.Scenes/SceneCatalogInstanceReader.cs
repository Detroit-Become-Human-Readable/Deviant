using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed class SceneCatalogInstanceReader
{
    public const string Version = "entity-catalog-v2-1";
    public const uint NativeType = 2207;
    public const int MaximumNameBytes = 4096;

    public bool TryRead(NativeObjectFrame frame, SceneEntityRecord entity, out SceneCatalogInstance? instance,
        out string? diagnostic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(entity);
        token.ThrowIfCancellationRequested(); instance = null; diagnostic = null;
        var offset = entity.NextClassOffset;
        var payload = frame.RawPayload.Span;
        if (frame.NativeType != NativeType || frame.Identity != entity.Identity || entity.ClassCount != 4 ||
            offset < 0 || offset > payload.Length - 28 || !payload.Slice(offset, 8).SequenceEqual("ENTCATLG"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 8)..]) != 2)
        { diagnostic = "CatalogInstanceProfileUnsupported"; return false; }
        var cursor = offset + 12;
        if (!ReadName(payload, ref cursor, out var name, out var nameStart, out var nameLength) ||
            !ReadName(payload, ref cursor, out var catalogName, out var catalogStart, out var catalogLength))
        { diagnostic = "CatalogInstanceNameInvalid"; return false; }
        if (payload.Length - cursor != 8)
        { diagnostic = "CatalogInstanceBoundaryMismatch"; return false; }
        var reference = SceneEntityReader.ReadReference(payload, cursor, frame.Identity);
        if (reference is not { ResourceType: 3 })
        { diagnostic = "CatalogInstanceReferenceUnsupported"; return false; }
        instance = new(frame.Identity, entity, name!, catalogName!, reference, new Dictionary<string, DecodedExtent>
        {
            ["InstanceName"] = At(nameStart, nameLength), ["CatalogName"] = At(catalogStart, catalogLength),
            ["Catalog"] = At(cursor, 8)
        });
        return true;
        DecodedExtent At(int start, int length) => new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset + start, length);
    }

    private static bool ReadName(ReadOnlySpan<byte> bytes, ref int cursor, out string? value, out int start, out int length)
    {
        value = null; start = 0; length = 0;
        if (bytes.Length - cursor < 4) return false;
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]); cursor += 4;
        if (declared > MaximumNameBytes || declared > bytes.Length - cursor) return false;
        start = cursor; length = (int)declared;
        var raw = bytes.Slice(cursor, length);
        foreach (var item in raw) if (item is < 32 or > 126) return false;
        value = Encoding.ASCII.GetString(raw); cursor += length;
        return true;
    }
}
