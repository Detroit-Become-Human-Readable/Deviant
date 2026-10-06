using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Scenes;

public sealed class SceneEntityReader
{
    public const string Version = "entity-v7-1";

    public bool TryRead(NativeObjectFrame frame, out SceneEntityRecord? entity, out string? diagnostic,
        CancellationToken token = default)
    {
        entity = null;
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out diagnostic, token)) return false;
        if (prefix!.ClassCount < 3) { diagnostic = "EntityClassUnavailable"; return false; }
        var offset = prefix.NextClassOffset;
        var payload = frame.RawPayload.Span;
        if (offset > payload.Length - 49 || !payload.Slice(offset, 8).SequenceEqual("ENTITY  "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 8)..]) != 7)
        { diagnostic = "EntityVersionUnsupported"; return false; }
        var body = payload.Slice(offset + 12, 37);
        var node = ReadReference(body, 0, frame.Identity);
        var others = new[] { ReadReference(body, 8, frame.Identity), ReadReference(body, 16, frame.Identity),
            ReadReference(body, 24, frame.Identity) }.OfType<ResourceReference>().ToArray();
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["Node"] = At(0, 8), ["Reference1"] = At(8, 8), ["Reference2"] = At(16, 8),
            ["Reference3"] = At(24, 8), ["Word32"] = At(32, 4), ["Flag36"] = At(36, 1)
        };
        entity = new(frame.Identity, frame.NativeType, frame.NativeId, node, others,
            BinaryPrimitives.ReadUInt32LittleEndian(body[32..]), body[36], prefix.ClassCount, offset + 49,
            new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset + offset, 49), fields);
        diagnostic = null; return true;
        DecodedExtent At(int relative, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + offset + 12 + relative), length);
    }

    internal static ResourceReference? ReadReference(ReadOnlySpan<byte> bytes, int offset, ResourceIdentity owner)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
        return type == 0 && id == 0 ? null : new(IdentifierNamespace.Native,
            id.ToString(CultureInfo.InvariantCulture), owner, type);
    }
}
