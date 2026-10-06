using System.Buffers.Binary;
using Deviant.Assets;

namespace Deviant.Formats.Containers;

public sealed record NativeCommonReference(uint Type, uint Id, byte Trailer, DecodedExtent Extent);
public sealed record NativeCommonPrefix(uint ClassCount, int NextClassOffset,
    IReadOnlyList<NativeCommonReference> References, DecodedExtent Extent);

/// <summary>Reads COM_CONT v6 and LOADCONT v2 at the start of a framed native object.</summary>
public sealed class NativeCommonPrefixReader
{
    public const int MaximumReferences = 4096;

    public bool TryRead(NativeObjectFrame frame, out NativeCommonPrefix? prefix, out string? diagnostic,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        prefix = null; diagnostic = null; token.ThrowIfCancellationRequested();
        var bytes = frame.RawPayload.Span;
        if (frame.PayloadExtent.Length != bytes.Length || frame.PayloadExtent.Offset < 0 ||
            frame.PayloadExtent.Offset > long.MaxValue - bytes.Length ||
            frame.Identity.IndexContentId != frame.PayloadExtent.Container.IndexContentId ||
            frame.Identity.Ordinal != frame.PayloadExtent.Container.Ordinal)
        { diagnostic = "NativePrefixSourceMismatch"; return false; }
        if (bytes.Length < 32 || !bytes.Slice(4, 8).SequenceEqual("COM_CONT"u8) || Word(bytes, 12) != 6)
        { diagnostic = "NativeCommonPrefixUnsupported"; return false; }
        var classes = Word(bytes, 0);
        if (classes is < 2 or > 64 || frame.ClassCount != classes)
        { diagnostic = "NativeClassCountUnsupported"; return false; }
        var count = Word(bytes, 16);
        if (count > MaximumReferences) { diagnostic = "NativeCommonReferenceLimit"; return false; }
        var loadOffset = checked(20 + (int)count * 9);
        if (loadOffset > bytes.Length - 12 || !bytes.Slice(loadOffset, 8).SequenceEqual("LOADCONT"u8) ||
            Word(bytes, loadOffset + 8) != 2)
        { diagnostic = "NativeLoadPrefixUnsupported"; return false; }
        var references = new NativeCommonReference[count];
        for (var index = 0; index < references.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var at = 20 + index * 9;
            references[index] = new(Word(bytes, at), Word(bytes, at + 4), bytes[at + 8],
                new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset + at, 9));
        }
        prefix = new(classes, loadOffset + 12, references,
            new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset, loadOffset + 12));
        return true;
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
