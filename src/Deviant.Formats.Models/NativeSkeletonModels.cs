using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed record NativeSkeletonLimits(int MaximumPayloadBytes = 8 * 1024 * 1024,
    int MaximumMatrices = 8192, int MaximumNestedRecords = 4096,
    int MaximumPolymorphicRecords = 65_536, int MaximumNameEntries = 65_536,
    int MaximumNameBytes = 1024 * 1024, int MaximumRawWords = 262_144,
    long MaximumMetadataBytes = 64L * 1024 * 1024);

/// <summary>An exact stored span borrowing the document's owned snapshot.</summary>
public sealed record SkeletonRawSection(string Name, int Count, int StoredStride,
    DecodedExtent Extent, [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes)
{
    public uint GetWordBits(int byteOffset)
    {
        if (byteOffset < 0 || byteOffset > RawBytes.Length - 4)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        return BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.Span.Slice(byteOffset, 4));
    }
}

public sealed record SkeletonStoredMatrix(int Ordinal, DecodedExtent Extent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes)
{
    public uint GetStoredWordBits(int index)
    {
        if ((uint)index >= 16) throw new ArgumentOutOfRangeException(nameof(index));
        return BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.Span.Slice(index * 4, 4));
    }
    public uint GetNativeWordBits(int index)
    {
        if ((uint)index >= 16) throw new ArgumentOutOfRangeException(nameof(index));
        return GetStoredWordBits(15 - index);
    }
    public float GetStoredValue(int index) => BitConverter.UInt32BitsToSingle(GetStoredWordBits(index));
}

public sealed record SkeletonRawReference(uint TypeWord, uint IdWord, DecodedExtent Extent);
public sealed record SkeletonHelperBlock(string Helper, ushort? LeadingWord,
    int? StoredCount, int ElementCount, ushort? SkipByteCount, SkeletonRawSection Section);
public sealed record SkeletonPolymorphicRecord(int Ordinal, byte Kind, byte ExtraByte, uint? LeadingWord,
    IReadOnlyList<SkeletonHelperBlock> Helpers, SkeletonRawSection Terminal, DecodedExtent Extent);
public sealed record SkeletonNestedRecord(int Ordinal, byte Version,
    SkeletonRawSection Words, SkeletonRawReference RawReference, SkeletonRawSection KindBytes,
    SkeletonRawSection ExtraBytes, IReadOnlyList<SkeletonPolymorphicRecord> Objects,
    SkeletonRawSection Blob, SkeletonRawSection OtherWords, SkeletonRawSection Label,
    uint Scalar64, uint Scalar68, byte Flag, ushort TailA, ushort TailB, DecodedExtent Extent);

public sealed record SkeletonNameEntry(int Ordinal, uint Key, int ByteCount,
    DecodedExtent Extent, DecodedExtent NameExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> StoredBytes)
{
    // Text is an explicit caller allocation; arbitrary stored bytes remain available.
    public bool TryGetUtf8Text(out string? text, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try { text = StrictUtf8.GetString(StoredBytes.Span); token.ThrowIfCancellationRequested(); return true; }
        catch (DecoderFallbackException) { token.ThrowIfCancellationRequested(); text = null; return false; }
    }
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
}
public sealed record SkeletonNameTable(int Ordinal, DecodedExtent Extent,
    IReadOnlyList<SkeletonNameEntry> Entries);

/// <summary>SKELETON v15 metadata and raw views over one owned frame snapshot.</summary>
public sealed record NativeSkeletonDocument(ResourceIdentity Identity, uint Version,
    ResourceReference? EntityNode, [property: JsonIgnore] NativeMeshPrefixDocument MeshPrefix,
    uint LeadingScalar, IReadOnlyList<SkeletonStoredMatrix> Matrices,
    IReadOnlyList<SkeletonNestedRecord> NestedRecords, uint TrailingScalar,
    IReadOnlyList<SkeletonNameTable> NameTables, DecodedExtent Extent,
    long AccountedMetadataBytes, [property: JsonIgnore] ReadOnlyMemory<byte> RawPayload)
{
    // Metadata accounting covers additional Skeleton DTOs; shared prefix names have their own cap.
    public long RetainedPayloadBytes => RawPayload.Length;
    public long SharedPrefixNameBytes => MeshPrefix.RetainedNameBytes;
}
