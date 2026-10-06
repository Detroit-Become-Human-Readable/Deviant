using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Textures;

public sealed record TextureShaderSlot(int SlotOrdinal, uint StoredWord, TextureContextReference Reference,
    byte TrailingByte, DecodedExtent Extent, byte[] RawBytes, string Sha256);

public sealed record TextureShaderSlotsDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent TagExtent, DecodedExtent CountExtent, byte[] RawFixedFields35,
    IReadOnlyList<TextureShaderSlot> Slots, DecodedExtent UnparsedSuffixExtent,
    ReadOnlyMemory<byte> UnparsedSuffixBytes, IReadOnlyList<SourceExtent> OriginalPhysicalExtents);

/// <summary>Reads the ordered native texture bindings from a framed SHADCUST v91 shader object.</summary>
public sealed class TextureUsageReader
{
    public const string Version = "texture-usage-shadcust91-v1";
    public const int MaximumTextureSlots = byte.MaxValue;
    private const int TagOffset = 32;
    private const int CountOffset = TagOffset + 47;
    private const int SlotsOffset = CountOffset + 4;

    public DecodeResult<TextureShaderSlotsDocument> ParseShaderSlots(TextureContainerObjectFrame frame,
        SourceRevision revision, IReadOnlyList<SourceExtent> originalPhysicalExtents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(originalPhysicalExtents);
        var physical = originalPhysicalExtents.ToArray();
        DecodeResult<TextureShaderSlotsDocument> Fail(string diagnostic) =>
            new(null, frame.Identity, revision, physical,
                [new(nameof(TextureUsageReader), Version, "SHADCUST-v91; native-typed-pairs; ordered-slots")],
                [diagnostic], [], new Dictionary<string, string>());
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Identity.IndexContentId != revision.IndexContentId ||
            frame.Identity.IndexContentId != frame.PayloadExtent.Container.IndexContentId ||
            frame.Identity.Ordinal != frame.PayloadExtent.Container.Ordinal ||
            frame.PayloadExtent.Offset < 0 || frame.PayloadExtent.Length != frame.RawPayload.Length ||
            frame.PayloadExtent.Offset > long.MaxValue - frame.PayloadExtent.Length ||
            physical.Any(extent => extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length))
            return Fail("UsageSourceIdentityMismatch");
        if (frame.NativeType != 2133) return Fail("UsageNativeTypeUnsupported");
        var payload = frame.RawPayload.Span;
        if (payload.Length < SlotsOffset || Word(payload, 0) != 3 ||
            !payload.Slice(4, 8).SequenceEqual("COM_CONT"u8) || Word(payload, 12) != 6 || Word(payload, 16) != 0 ||
            !payload.Slice(20, 8).SequenceEqual("LOADCONT"u8) || Word(payload, 28) != 2 ||
            !payload.Slice(TagOffset, 8).SequenceEqual("SHADCUST"u8))
            return Fail("UsageClassEnvelopeUnsupported");
        if (Word(payload, TagOffset + 8) != 91) return Fail("UsageVersionUnsupported");
        var count = Word(payload, CountOffset);
        if (count > MaximumTextureSlots) return Fail("UsageSlotCountUnsupported");
        var consumed = checked(SlotsOffset + (int)count * 13);
        if (consumed > payload.Length) return Fail("UsageSlotsTruncated");
        var slots = new TextureShaderSlot[count];
        for (var index = 0; index < slots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = SlotsOffset + index * 13;
            var raw = payload.Slice(offset, 13).ToArray();
            slots[index] = new(index, Word(raw, 0),
                new(Word(raw, 4), Word(raw, 8), At(offset + 4, 8)), raw[12], At(offset, 13), raw, Hash(raw));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var remainder = frame.RawPayload[consumed..];
        var value = new TextureShaderSlotsDocument(frame.Identity, revision, At(TagOffset, 12),
            At(CountOffset, 4), payload.Slice(TagOffset + 12, 35).ToArray(), slots,
            At(consumed, remainder.Length), remainder, physical);
        return new(value, frame.Identity, revision, physical,
            [new(nameof(TextureUsageReader), Version, "SHADCUST-v91; native-typed-pairs; ordered-slots",
                Hash(payload.Slice(TagOffset, consumed - TagOffset)))], [], [], new Dictionary<string, string>());

        DecodedExtent At(int offset, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + offset), length);
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
