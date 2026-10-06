using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Xunit;

namespace Deviant.Container.Tests;

public sealed class SegsContainerReaderTests
{
    [Fact]
    public void MixedLiteralAndCompressedSegmentsPreserveBytesAndPhysicalRanges()
    {
        var literal = new byte[] { 0x78, 0x9c, 0x3e, 0x6f, 0xaa, 0x01, 0x02 };
        var fixture = BuildContainer([Enumerable.Repeat((byte)7, 200).ToArray(), literal], [6, 8], literalSegments: new HashSet<int> { 1 });
        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.NotNull(result.Value);
        Assert.Equal(fixture.DecodedPrefix, result.Value.DecodedPrefix);
        Assert.Equal(literal, result.Value.Chunks[1].DecodedBytes);
        Assert.Equal(0u, result.Value.Chunks[1].EncodedOffsetTag & 1);
        Assert.Equal(literal.Length, result.Value.Chunks[1].PhysicalExtent.Length);
        Assert.Equal(fixture.RawSuffix, result.Value.RawSuffix);
    }

    [Fact]
    public void FullLiteralChunkUsesZeroLengthWords()
    {
        var literal = Enumerable.Range(0, 65536).Select(index => (byte)(index * 71)).ToArray();
        var fixture = BuildContainer([literal], [], literalSegments: new HashSet<int> { 0 });
        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.NotNull(result.Value);
        Assert.Equal(literal, result.Value.DecodedPrefix);
        Assert.Equal(0, result.Value.Chunks[0].CompressedLength);
        Assert.Equal(65536, result.Value.Chunks[0].PhysicalExtent.Length);
    }

    [Fact]
    public void LiteralLengthMismatchAndCompressedEmptyExtentAreRejected()
    {
        var fixture = BuildContainer([[1, 2, 3, 4]], [], literalSegments: new HashSet<int> { 0 });
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Stored.AsSpan(18), 3);
        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, text => text.StartsWith("LiteralSegmentLengthMismatch", StringComparison.Ordinal));
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Stored.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Stored.AsSpan(20), 1);
        result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, text => text.StartsWith("EmptyCompressedSegment", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesIndependentChunksAndKeepsGapsAndRawSuffixSeparate()
    {
        var first = Enumerable.Range(0, SegsContainerReader.DefaultDecodedSegmentBytes)
            .Select(value => (byte)((value * 37 + 11) & 0xFF)).ToArray();
        var second = Enumerable.Range(0, 31).Select(value => (byte)(255 - value * 3)).ToArray();
        var fixture = BuildContainer([first, second], [0x53, 0x45, 0x47, 0x53]);

        var decoded = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored),
            alternativeSize: checked((uint)fixture.RawSuffix.Length));

        Assert.NotNull(decoded.Value);
        Assert.Empty(decoded.Diagnostics);
        Assert.True(decoded.Value.AlternativeSizeMatchesRawSuffix);
        Assert.Equal(fixture.DecodedPrefix, decoded.Value.DecodedPrefix);
        Assert.Equal(fixture.RawSuffix, decoded.Value.RawSuffix);
        Assert.Equal(2, decoded.Value.Chunks.Count);
        Assert.Equal(0, decoded.Value.Chunks[0].DecodedLengthWord);
        Assert.Equal(first.Length, decoded.Value.Chunks[0].DecodedLength);
        Assert.Equal(second.Length, decoded.Value.Chunks[1].DecodedLength);
        Assert.All(decoded.Value.Chunks, chunk => Assert.Equal(1u, chunk.EncodedOffsetTag & 1u));
        Assert.True(decoded.Value.Gaps.Count > 0);
        Assert.All(decoded.Value.Gaps, gap => Assert.NotEmpty(gap.Bytes));
        Assert.Equal(fixture.Stored.Length, decoded.Extents.Single().Length);
        Assert.Equal(Hash(fixture.Stored), decoded.Extents.Single().PayloadSha256);
        Assert.Equal(0, decoded.Value.Chunks[0].DecodedExtent.Offset);
        Assert.Equal(first.Length, decoded.Value.Chunks[1].DecodedExtent.Offset);
    }

    [Fact]
    public void RejectsAnInvalidSignature()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        fixture.Stored[0] = (byte)'X';

        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("InvalidSignature", StringComparison.Ordinal));
    }

    [Fact]
    public void AlignsSingleChunkDataAndKeepsTheAlternativeSizeTail()
    {
        var decodedSegment = Enumerable.Range(0, 37).Select(value => (byte)(value * 5)).ToArray();
        var fixture = BuildContainer([decodedSegment], [1, 3, 5, 7, 9]);
        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored), 5);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(24, result.Value.TableEndOffset);
        Assert.Equal(32, result.Value.DataStartOffset);
        Assert.Equal(8, result.Value.Gaps.Single().Bytes.Length);
        Assert.Equal(decodedSegment, result.Value.DecodedPrefix);
        Assert.Equal(fixture.RawSuffix, result.Value.RawSuffix);
        Assert.True(result.Value.AlternativeSizeMatchesRawSuffix);
    }

    [Fact]
    public void RejectsUnsupportedVersion()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Stored.AsSpan(4, 2), 7);

        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("UnsupportedVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsTruncatedHeaderAndTable()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        var reader = new SegsContainerReader();
        var identity = Identity();
        var revision = Revision();
        Assert.Contains(reader.Parse(fixture.Stored.AsSpan(0, 12), identity, revision,
            new SourceExtent("sample.dat", 0, 12)).Diagnostics,
            item => item.StartsWith("TruncatedHeader", StringComparison.Ordinal));

        var shortTable = fixture.Stored.AsSpan(0, 20).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(shortTable.AsSpan(6, 2), 2);
        Assert.Contains(reader.Parse(shortTable, identity, revision, Extent(shortTable)).Diagnostics,
            item => item.StartsWith("TruncatedTable", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsOverlappingCompressedRangesBeforeInflation()
    {
        var fixture = BuildContainer([[1, 2, 3], [4, 5, 6]], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Stored.AsSpan(16 + SegsContainerReader.TableEntryLength + 4, 4), 1);

        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("SegmentExtentOverlap", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsOutOfRangeAndMisSizedZlibChunks()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Stored.AsSpan(20, 4), 1_000_001);
        var outOfRange = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.Null(outOfRange.Value);
        Assert.Contains(outOfRange.Diagnostics, item => item.StartsWith("SegmentExtentOutOfRange", StringComparison.Ordinal));

        fixture = BuildContainer([[1, 2, 3]], []);
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Stored.AsSpan(18, 2), 2);
        var wrongDecodedSize = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        Assert.Null(wrongDecodedSize.Value);
        Assert.Contains(wrongDecodedSize.Diagnostics, item => item.StartsWith("InvalidZlibSegment", StringComparison.Ordinal) ||
            item.StartsWith("DecodedTotalMismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAChunkWhoseZlibAdlerTrailerWasRemoved()
    {
        var fixture = BuildContainer([[1, 2, 3]], [], stripFirstZlibTrailer: true);

        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("InvalidZlibSegment", StringComparison.Ordinal) &&
            item.Contains("Adler-32", StringComparison.Ordinal));
    }

    [Fact]
    public void InspectsARawTaggedRecordAcrossDecodedPrefixAndPhysicalSuffix()
    {
        var decodedPart = new byte[19];
        "DATA_RAW"u8.CopyTo(decodedPart);
        BinaryPrimitives.WriteUInt32LittleEndian(decodedPart.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(decodedPart.AsSpan(12, 4), 7);
        new byte[] { 1, 2, 3 }.CopyTo(decodedPart, 16);
        var fixture = BuildContainer([decodedPart], [4, 5, 6, 7, 8]);
        var container = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        var containerValue = Assert.IsType<SegsDocument>(container.Value);

        var result = new RawNestedRecordReader().Inspect(container, 0);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("DATA_RAW", result.Value.TagText);
        Assert.Equal(4u, result.Value.Version);
        Assert.Equal(7u, result.Value.DeclaredBodyLength);
        Assert.Equal(Identity(), result.Value.HeaderExtent.Container);
        Assert.Collection(result.Value.BodySlices,
            prefix =>
            {
                Assert.Equal(RawRecordSliceSource.DecodedPrefix, prefix.Source);
                Assert.Equal(0, prefix.BodyOffset);
                Assert.Equal(new byte[] { 1, 2, 3 }, prefix.Bytes.ToArray());
                Assert.Equal(new DecodedExtent(Identity(), 16, 3), prefix.DecodedExtent);
                Assert.Null(prefix.PhysicalExtent);
            },
            suffix =>
            {
                Assert.Equal(RawRecordSliceSource.RawSuffix, suffix.Source);
                Assert.Equal(3, suffix.BodyOffset);
                Assert.Equal(new byte[] { 4, 5, 6, 7 }, suffix.Bytes.ToArray());
                Assert.Null(suffix.DecodedExtent);
                var physicalExtent = Assert.IsType<SourceExtent>(suffix.PhysicalExtent);
                Assert.Equal(containerValue.RawSuffixExtent!.Offset, physicalExtent.Offset);
                Assert.Equal(4, physicalExtent.Length);
                Assert.Equal(Hash([4, 5, 6, 7]), physicalExtent.PayloadSha256);
            });
        Assert.NotNull(result.Value.UnparsedRawSuffixRemainder);
        Assert.Equal(1, result.Value.UnparsedRawSuffixRemainder!.Length);
        Assert.Equal(Hash([8]), result.Value.UnparsedRawSuffixRemainder.PayloadSha256);
        Assert.Single(result.Extents);
    }

    [Fact]
    public void RejectsARawRecordWhoseBodyExceedsBothAvailableSlices()
    {
        var decodedPart = new byte[16];
        "DATA_RAW"u8.CopyTo(decodedPart);
        BinaryPrimitives.WriteUInt32LittleEndian(decodedPart.AsSpan(12, 4), 100);
        var fixture = BuildContainer([decodedPart], [1, 2]);
        var container = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));

        var result = new RawNestedRecordReader().Inspect(container, 0);

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("RecordBodyOutOfRange", StringComparison.Ordinal));
    }

    [Fact]
    public void RawRecordInspectionPropagatesCancellation()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        var container = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), Extent(fixture.Stored));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new RawNestedRecordReader().Inspect(container, 0, cancellation.Token));
    }

    [Fact]
    public void RejectsAClaimedPayloadHashThatDoesNotMatchInput()
    {
        var fixture = BuildContainer([[1, 2, 3]], []);
        var extent = Extent(fixture.Stored) with { PayloadSha256 = new string('0', 64) };

        var result = new SegsContainerReader().Parse(fixture.Stored, Identity(), Revision(), extent);

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("PayloadHashMismatch", StringComparison.Ordinal));
        Assert.Equal(Hash(fixture.Stored), result.Extents.Single().PayloadSha256);
    }

    [Fact]
    public async Task ReadAsyncRejectsAResourceRecordThatDoesNotMatchItsIndexRow()
    {
        using var fixture = TemporaryArchive.Create(BuildContainer([[1, 2, 3]], []).Stored);
        var snapshot = fixture.Snapshot;
        var indexed = snapshot.Records.Single();
        var forged = indexed with { Offset = indexed.Offset + 1 };

        var result = await new SegsContainerReader().ReadAsync(snapshot, forged);

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.StartsWith("ResourceRecordMismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsOneIndexedResourceWithRevisionAndExtentEvidence()
    {
        using var fixture = TemporaryArchive.Create(BuildContainer([[1, 2, 3], [4, 5, 6]], [9, 8]).Stored, 2);
        var result = await new SegsContainerReader().ReadAsync(fixture.Snapshot, fixture.Snapshot.Records.Single());

        Assert.NotNull(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(fixture.Snapshot.IndexContentId, result.Identity.IndexContentId);
        Assert.Equal(fixture.Snapshot.PhysicalSourceStamp, result.Revision.PhysicalSourceStamp);
        Assert.Equal(fixture.Snapshot.Records.Single().StoredSize, result.Extents.Single().Length);
        Assert.Equal(2, result.Value.Chunks.Count);
    }

    private static ResourceIdentity Identity() => new("synthetic-index", 7);
    private static SourceRevision Revision() => new("synthetic-index", "synthetic-source");
    private static SourceExtent Extent(byte[] bytes) => new("sample.dat", 123, bytes.Length, Hash(bytes));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static SegsFixture BuildContainer(IReadOnlyList<byte[]> decodedSegments, byte[] suffix,
        bool stripFirstZlibTrailer = false, IReadOnlySet<int>? literalSegments = null)
    {
        if (decodedSegments.Count == 0) throw new ArgumentException("At least one test segment is required.", nameof(decodedSegments));
        var compressed = decodedSegments.Select((bytes, index) => literalSegments?.Contains(index) == true ? bytes.ToArray() : Compress(bytes)).ToArray();
        if (stripFirstZlibTrailer)
        {
            if (compressed[0].Length < 4) throw new InvalidOperationException("Test zlib chunk has no trailer to remove.");
            compressed[0] = compressed[0][..^4];
        }
        if (compressed.Where((bytes, index) => bytes.Length > ushort.MaxValue &&
            (literalSegments?.Contains(index) != true || bytes.Length != SegsContainerReader.DefaultDecodedSegmentBytes)).Any())
            throw new ArgumentException("Test segment is too large.");
        var tableEnd = SegsContainerReader.HeaderLength + decodedSegments.Count * SegsContainerReader.TableEntryLength;
        var dataStart = (tableEnd + 15) & ~15;
        var relativeOffsets = new int[compressed.Length];
        var decodedLengthWords = new ushort[decodedSegments.Count];
        var current = 0;
        for (var i = 0; i < compressed.Length; i++)
        {
            if (i > 0) current = (current + 15) & ~15;
            relativeOffsets[i] = current;
            decodedLengthWords[i] = decodedSegments[i].Length == SegsContainerReader.DefaultDecodedSegmentBytes
                ? (ushort)0 : checked((ushort)decodedSegments[i].Length);
            current = checked(current + compressed[i].Length);
        }
        var compressedSpan = current;
        var decodedLength = decodedSegments.Sum(bytes => (long)bytes.Length);
        var stored = new byte[checked(dataStart + compressedSpan + suffix.Length)];
        "segs"u8.CopyTo(stored);
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(6, 2), checked((ushort)decodedSegments.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(8, 4), checked((uint)decodedLength));
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(12, 4), checked((uint)compressedSpan));
        for (var i = 0; i < compressed.Length; i++)
        {
            var entryOffset = SegsContainerReader.HeaderLength + i * SegsContainerReader.TableEntryLength;
            BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(entryOffset, 2),
                compressed[i].Length == SegsContainerReader.DefaultDecodedSegmentBytes ? (ushort)0 : checked((ushort)compressed[i].Length));
            BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(entryOffset + 2, 2), decodedLengthWords[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(entryOffset + 4, 4), checked((uint)relativeOffsets[i]) |
                (literalSegments?.Contains(i) == true ? 0u : 1u));
            compressed[i].CopyTo(stored, dataStart + relativeOffsets[i]);
        }
        suffix.CopyTo(stored, dataStart + compressedSpan);
        return new SegsFixture(stored, decodedSegments.SelectMany(bytes => bytes).ToArray(), suffix);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        return output.ToArray();
    }

    private sealed record SegsFixture(byte[] Stored, byte[] DecodedPrefix, byte[] RawSuffix);

    private sealed class TemporaryArchive : IDisposable
    {
        private readonly string root;
        private TemporaryArchive(string root, ArchiveSnapshot snapshot) { this.root = root; Snapshot = snapshot; }
        public ArchiveSnapshot Snapshot { get; }

        public static TemporaryArchive Create(byte[] payload, uint alternativeSize = 0)
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-container-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var indexPath = Path.Combine(root, "sample.idx");
                var dataPath = Path.Combine(root, "sample.dat");
                File.WriteAllBytes(indexPath, [1, 2, 3, 4]);
                File.WriteAllBytes(dataPath, payload);
                var revisions = new FileRevisionProvider();
                var indexStamp = revisions.Capture(indexPath);
                var volumeStamp = revisions.Capture(dataPath);
                var record = new ResourceRecord(0, 29, 1, 7, 0, 0, payload.Length, alternativeSize,
                    "sample.dat", ResourceValidity.Valid);
                var volume = new VolumeSource(0, "sample.dat", dataPath, volumeStamp);
                var snapshot = new ArchiveSnapshot(indexPath, "synthetic-index", 18, 0, new byte[73], 1,
                    [record], [volume], indexStamp, "synthetic-physical-stamp", []);
                return new TemporaryArchive(root, snapshot);
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
