using Deviant.Assets;

namespace Deviant.Formats.Models;

internal sealed record NativeMeshFixedPrefix(int Start, IReadOnlyList<NativeMeshLod> Lods,
    NativeMeshPrefixFixedFields FixedFields, uint NativeFlags, uint GroupIndex,
    IReadOnlyDictionary<string, DecodedExtent> FieldExtents);

internal static class NativeMeshPrefixCore
{
    public const int MaximumLods = 64;

    // Admission belongs to each facade; the legacy facade keeps its original static profile and tail behavior.
    public static NativeMeshFixedPrefix Read(NativeMeshByteReader reader, ResourceIdentity owner,
        CancellationToken token)
    {
        var start = reader.Position;
        reader.Tag("MESH____"u8, 29);
        var lodCountAt = reader.Position;
        var lods = new NativeMeshLod[reader.Count(MaximumLods)];
        if (lods.Length == 0) throw new InvalidDataException("NativeMeshLodsEmpty");
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["ClassHeader"] = reader.Extent(start, 12), ["LodCount"] = reader.Extent(lodCountAt, 4)
        };
        for (var i = 0; i < lods.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var at = reader.Position;
            var reference = reader.Reference(owner); var threshold = reader.Float();
            if (reference.ResourceType != 2130) throw new InvalidDataException("NativeMeshLodTypeUnsupported");
            lods[i] = new(reference, threshold, reader.Extent(at, 12));
            fields[$"Lod{i}"] = lods[i].Extent;
        }
        var fixedAt = reader.Position;
        var word0 = reader.Word(); var word1 = reader.Word();
        var reference0 = reader.Reference(owner); var reference1 = reader.Reference(owner);
        fields["FixedWord0"] = reader.Extent(fixedAt, 4);
        fields["FixedWord1"] = reader.Extent(fixedAt + 4, 4);
        fields["FixedReference0"] = reader.Extent(fixedAt + 8, 8);
        fields["FixedReference1"] = reader.Extent(fixedAt + 16, 8);
        var fixedFields = new NativeMeshPrefixFixedFields(word0, word1, reference0, reference1, reader.Extent(fixedAt, 24));
        var flagsAt = reader.Position; var flags = reader.Word();
        fields["NativeFlags"] = reader.Extent(flagsAt, 4);
        fields["FixedFields"] = reader.Extent(fixedAt, 28);
        var groupAt = reader.Position; var group = reader.Word();
        fields["GroupIndex"] = reader.Extent(groupAt, 4);
        return new(start, lods, fixedFields, flags, group, fields);
    }
}
