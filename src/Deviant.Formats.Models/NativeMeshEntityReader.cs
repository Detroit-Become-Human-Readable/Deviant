using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Models;

public sealed class NativeMeshEntityReader
{
    public const int MaximumLods = 64;

    public bool TryRead(NativeObjectFrame frame, SceneEntityRecord entity,
        out NativeMeshEntityDocument? document, out string? diagnostic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(entity);
        document = null; diagnostic = null; token.ThrowIfCancellationRequested();
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out diagnostic, token)) return false;
        try
        {
            if (frame.NativeType != 2009 || entity.Identity != frame.Identity || entity.NativeType != frame.NativeType ||
                entity.NativeId != frame.NativeId || entity.ClassCount != prefix!.ClassCount || prefix.ClassCount != 4 ||
                entity.NextClassOffset != prefix.NextClassOffset + 49 ||
                entity.EntityExtent.Container != frame.PayloadExtent.Container ||
                entity.EntityExtent.Offset != frame.PayloadExtent.Offset + prefix.NextClassOffset ||
                entity.EntityExtent.Length != 49)
                throw new InvalidDataException("NativeMeshEntitySourceMismatch");
            var r = new NativeMeshByteReader(frame.RawPayload, frame.PayloadExtent);
            r.Skip(entity.NextClassOffset);
            var start = r.Position; r.Tag("MESH____"u8, 29);
            var lods = new NativeMeshLod[r.Count(MaximumLods)];
            if (lods.Length == 0) throw new InvalidDataException("NativeMeshLodsEmpty");
            for (var i = 0; i < lods.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var at = r.Position; var reference = r.Reference(frame.Identity); var threshold = r.Float();
                if (reference.ResourceType != 2130) throw new InvalidDataException("NativeMeshLodTypeUnsupported");
                lods[i] = new(reference, threshold, r.Extent(at, 12));
            }
            // Fixed MESH fields between the LOD array and the selected group are retained as source extents.
            var fixedAt = r.Position;
            r.Skip(24);
            var flagsAt = r.Position; var nativeFlags = r.Word();
            var fixedExtent = r.Extent(fixedAt, 28);
            var selectorAt = r.Position; var group = r.Word();
            var remaining = r.Skip(frame.RawPayload.Length - r.Position);
            document = new(frame.Identity, entity, 29, lods, group,
                r.Extent(start, frame.RawPayload.Length - start), new Dictionary<string, DecodedExtent>
                {
                    ["FixedFields"] = fixedExtent, ["NativeFlags"] = r.Extent(flagsAt, 4),
                    ["GroupIndex"] = r.Extent(selectorAt, 4),
                    ["Tail"] = remaining
                }) { NativeFlags = nativeFlags };
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshSizeOverflow"; return false; }
    }
}
