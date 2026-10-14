using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class ScriptBundleReaderTests
{
    private static readonly ResourceIdentity Resource = new("test-index", 7);
    private static readonly SourceRevision Revision = new("test-index", "test-source");
    private static readonly byte[] HeaderOnly = [27, 76, 117, 97, 81, 0, 1, 4, 4, 4, 4, 0];

    [Fact]
    public void FramesChunksMetadataAndOpaqueBytesAtExactSourceOffsets()
    {
        byte[] opaque = [0, 1, 2, 3, .. "NOCHK___"u8, .. HeaderOnly];
        var bytes = Build(new Chunk(17, [.. HeaderOnly, 0xaa], [new(23, opaque, 0xfe, 0x7f)]), new(29, HeaderOnly, []));
        var result = Read(bytes, offset: 400);
        var document = Assert.IsType<ScriptBundleDocument>(result.Value);
        Assert.Equal(bytes, document.RawBytes.ToArray());
        Assert.Equal(400, document.InputExtent.Offset);
        Assert.Equal(new DecodedExtent(Resource, 400, bytes.Length), document.ConsumedExtent);
        Assert.Equal(new DecodedExtent(Resource, 400, 25), document.HeaderExtent);
        Assert.Equal(2, document.Chunks.Count);
        var first = document.Chunks[0];
        Assert.Equal("script/chunk/0", first.Identity.MemberPath);
        Assert.Equal(17UL, first.OpaqueKey);
        Assert.Equal(new DecodedExtent(Resource, 425, 12), first.HeaderExtent);
        Assert.Equal(new DecodedExtent(Resource, 437, 13), first.BytecodeSpan);
        Assert.Equal((byte[])[.. HeaderOnly, 0xaa], first.BytecodeBytes.ToArray());
        var metadata = Assert.Single(first.Metadata);
        Assert.Equal("script/chunk/0/metadata/0", metadata.Identity.MemberPath);
        Assert.Equal(19000U, metadata.RawDiscriminator);
        Assert.Equal(1U, metadata.ClassCount);
        Assert.Equal(1U, metadata.FormatVersion);
        Assert.Equal(0xfe, metadata.Flag0);
        Assert.Equal(0x7f, metadata.Flag1);
        Assert.Equal(opaque, metadata.OpaquePayload.ToArray());
        Assert.Equal(metadata.HeaderExtent.Offset + 34, metadata.PayloadExtent.Offset);
        Assert.Equal(metadata.HeaderExtent.Offset + 28, metadata.FlagsExtent.Offset);
        Assert.Equal(metadata.HeaderExtent.Offset + 30, metadata.PayloadLengthExtent.Offset);
        Assert.Equal(34 + opaque.Length, metadata.Extent.Length);
        Assert.Equal("Header only", result.UnknownFields["bytecodeValidation"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Assert.Single(result.Steps).PayloadSha256);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void PreservesDuplicateKeysAndEveryIdentityWithBoundedDiagnostics()
    {
        var bytes = Build(new Chunk(17, HeaderOnly, [new(31, [1]), new(31, [2])]), new(17, HeaderOnly, [new(31, [3])]));
        var result = Read(bytes);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Chunks.Count);
        Assert.Equal(3, result.Value.Chunks.Sum(chunk => chunk.Metadata.Count));
        Assert.Equal(3, result.Value.Chunks.SelectMany(chunk => chunk.Metadata).Select(row => row.Identity).Distinct().Count());
        Assert.Contains("DuplicateScriptChunkKey: 1", result.Diagnostics);
        Assert.Contains("DuplicateScriptMetadataKey: 2", result.Diagnostics);
    }

    [Fact]
    public void SnapshotDoesNotRetainTheCallerMutableArrayOrSourceCollection()
    {
        var bytes = Build(new Chunk(11, HeaderOnly, [new(13, [1, 2, 3])]));
        var original = bytes.ToArray();
        var sources = new[] { new SourceExtent("volume.bin", 100, bytes.Length) };
        var result = Read(bytes, sources: sources);
        Assert.NotNull(result.Value);
        Array.Fill(bytes, (byte)0xff);
        sources[0] = new("replacement.bin", 0, 0);
        Assert.Equal(original, result.Value.RawBytes.ToArray());
        Assert.Equal(HeaderOnly, result.Value.Chunks[0].BytecodeBytes.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Value.Chunks[0].Metadata[0].OpaquePayload.ToArray());
        Assert.Equal("volume.bin", Assert.Single(result.Value.OriginalPhysicalExtents).RelativeSource);
    }

    [Fact]
    public void AcceptsAnEmptyBundleAndDoesNotRequireInnerLuaPrototypeData()
    {
        var empty = Read(Build());
        Assert.NotNull(empty.Value);
        Assert.Empty(empty.Value.Chunks);
        var headerOnly = Read(Build(new Chunk(1, HeaderOnly, [])));
        Assert.NotNull(headerOnly.Value);
        Assert.Equal(12, Assert.Single(headerOnly.Value.Chunks).BytecodeSpan.Length);
        Assert.Equal("Header only", headerOnly.UnknownFields["bytecodeValidation"]);
    }

    [Theory]
    [InlineData(0, "ScriptBundleUnsupportedTag")]
    [InlineData(5, "ScriptBundleUnsupportedTag")]
    [InlineData(13, "ScriptBundleUnsupportedVersion")]
    public void RejectsUnsupportedEnvelopeProfiles(int offset, string code)
    {
        var bytes = Build(new Chunk(1, HeaderOnly, [])); bytes[offset] ^= 0x40;
        Assert.Contains(code, Read(bytes).Diagnostics);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(65)]
    [InlineData(69)]
    [InlineData(77)]
    public void RejectsUnsupportedMetadataProfiles(int offset)
    {
        var bytes = Build(new Chunk(1, HeaderOnly, [new(2, [1])])); bytes[offset] ^= 0x40;
        Assert.Contains("ScriptBundleUnsupportedMetadataProfile", Read(bytes).Diagnostics);
    }

    [Fact]
    public void RejectsTruncationLengthMismatchAndUnconsumedTail()
    {
        var bytes = Build(new Chunk(1, HeaderOnly, [new(2, [1, 2, 3])]));
        Assert.Contains("ScriptBundleTruncatedData", Read(bytes[..10]).Diagnostics);
        var wrongBody = bytes.ToArray(); WriteWord(wrongBody, 17, 1);
        Assert.Contains("ScriptBundleBodyLengthMismatch", Read(wrongBody).Diagnostics);
        byte[] tail = [.. bytes, 0x7f]; WriteWord(tail, 17, (uint)(tail.Length - 21));
        Assert.Contains("ScriptBundleTrailingBytes", Read(tail).Diagnostics);
        var shortened = bytes[..^1]; WriteWord(shortened, 17, (uint)(shortened.Length - 21));
        Assert.Contains("ScriptBundleTruncatedData", Read(shortened).Diagnostics);
    }

    [Fact]
    public void RejectsLowerDeclaredCountsInsteadOfScanningForTheNextHeader()
    {
        var bytes = Build(new Chunk(1, HeaderOnly, [new(2, [1, 2, 3])]), new(3, HeaderOnly, []));
        var fewerChunks = bytes.ToArray(); WriteWord(fewerChunks, 21, 1);
        Assert.Contains("ScriptBundleTrailingBytes", Read(fewerChunks).Diagnostics);
        var fewerMetadata = bytes.ToArray(); WriteWord(fewerMetadata, 49, 0);
        Assert.Null(Read(fewerMetadata).Value);
    }

    [Fact]
    public void RejectsCountsBeyondRemainingWireAndLengthOverflow()
    {
        var one = Build(new Chunk(1, HeaderOnly, [])); WriteWord(one, 21, 2);
        Assert.Contains("ScriptBundleChunkCountBounds", Read(one).Diagnostics);
        var metadata = Build(new Chunk(1, HeaderOnly, [new(2, [1])])); WriteWord(metadata, 49, 2);
        Assert.Contains("ScriptBundleMetadataCountBounds", Read(metadata).Diagnostics);
        var bytecode = Build(new Chunk(1, HeaderOnly, [])); WriteWord(bytecode, 33, uint.MaxValue);
        Assert.Contains("ScriptBundleBytecodeLimit", Read(bytecode).Diagnostics);
        var payload = Build(new Chunk(1, HeaderOnly, [new(2, [1])])); WriteWord(payload, 83, uint.MaxValue);
        Assert.Contains("ScriptBundleMetadataPayloadLimit", Read(payload).Diagnostics);
    }

    [Fact]
    public void RejectsAnUnknownBytecodeHeaderWithoutClaimingInnerValidation()
    {
        var bytes = Build(new Chunk(1, HeaderOnly, [])); bytes[37] = 0;
        Assert.Contains("ScriptBundleUnsupportedBytecodeHeader", Read(bytes).Diagnostics);
        var shortHeader = Build(new Chunk(1, [1, 2], []));
        Assert.Null(Read(shortHeader).Value);
    }

    [Fact]
    public void EnforcesInputChunkMetadataAndPayloadLimits()
    {
        var bytes = Build(new Chunk(1, [.. HeaderOnly, 0], [new(2, [1])]), new(3, HeaderOnly, [new(4, [2])]));
        Assert.Contains("ScriptBundleInputLimit", Read(bytes, new(MaximumInputBytes: bytes.Length - 1)).Diagnostics);
        Assert.Contains("ScriptBundleChunkLimit", Read(bytes, new(MaximumChunks: 1)).Diagnostics);
        Assert.Contains("ScriptBundleBytecodeLimit", Read(bytes, new(MaximumBytecodeBytes: 12)).Diagnostics);
        Assert.Contains("ScriptBundleMetadataLimit", Read(bytes, new(MaximumMetadataRecords: 1)).Diagnostics);
        Assert.Contains("ScriptBundleMetadataPayloadLimit", Read(bytes, new(MaximumMetadataPayloadBytes: 0)).Diagnostics);
        Assert.Contains("ScriptBundleInvalidLimits", Read(bytes, new(MaximumInputBytes: 0)).Diagnostics);
    }

    [Fact]
    public void RetainedBudgetIncludesRecordsIdentityStringsAndProvenance()
    {
        var many = Build(Enumerable.Range(0, 100).Select(i => new Chunk((ulong)i, HeaderOnly, [])).ToArray());
        Assert.True(many.Length < 32 * 1024);
        Assert.Contains("ScriptBundleMemoryLimit", Read(many, new(MaximumRetainedBytes: 32 * 1024)).Diagnostics);
        var empty = Build();
        var sources = Enumerable.Range(0, 100).Select(i => new SourceExtent("part/" + i + ".bin", 0, 0)).ToArray();
        Assert.Contains("ScriptBundleMemoryLimit", Read(empty, new(MaximumRetainedBytes: 32 * 1024), sources: sources).Diagnostics);
        var identity = Resource with { MemberPath = new string('x', 4000) };
        var result = new ScriptBundleReader().Read(Build(new Chunk(1, HeaderOnly, []), new(2, HeaderOnly, [])), identity,
            Revision, new(identity, 0, 81), [], new(MaximumRetainedBytes: 16 * 1024));
        Assert.Contains("ScriptBundleMemoryLimit", result.Diagnostics);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../volume.bin")]
    [InlineData("/volume.bin")]
    [InlineData("C:/volume.bin")]
    [InlineData("part//volume.bin")]
    [InlineData("part/./volume.bin")]
    public void RejectsUnsafePhysicalSourceNames(string path)
    {
        Assert.Contains("ScriptBundleInvalidPhysicalSource", Read(Build(), sources: [new(path, 0, 0)]).Diagnostics);
    }

    [Fact]
    public void RejectsPhysicalBoundsHashAndHugeCollectionBeforeIndexingIt()
    {
        Assert.Contains("ScriptBundleInvalidPhysicalSource", Read(Build(), sources: [new("v.bin", long.MaxValue, 1)]).Diagnostics);
        Assert.Contains("ScriptBundleInvalidPhysicalSource", Read(Build(), sources: [new("v.bin", 0, -1)]).Diagnostics);
        Assert.Contains("ScriptBundleInvalidPhysicalSource", Read(Build(), sources: [new("v.bin", 0, 1, "not-a-sha")]).Diagnostics);
        var hostile = new SourceList(int.MaxValue, _ => throw new InvalidOperationException("Must not index"));
        Assert.Contains("ScriptBundlePhysicalSourceLimit", Read(Build(), sources: hostile).Diagnostics);
        Assert.Equal(0, hostile.IndexReads);
    }

    [Fact]
    public void ValidatesExactRevisionExtentParentAndOverflow()
    {
        var bytes = Build(new Chunk(1, HeaderOnly, []));
        var reader = new ScriptBundleReader();
        Assert.Contains("ScriptBundleSourceIdentityMismatch", reader.Read(bytes, Resource, new("other-index", "source"),
            new(Resource, 0, bytes.Length), []).Diagnostics);
        Assert.Contains("ScriptBundleSourceIdentityMismatch", reader.Read(bytes, Resource, Revision,
            new(Resource with { Ordinal = 8 }, 0, bytes.Length), []).Diagnostics);
        Assert.Contains("ScriptBundleSourceIdentityMismatch", reader.Read(bytes, Resource, Revision,
            new(Resource, long.MaxValue, bytes.Length), []).Diagnostics);
        Assert.Contains("ScriptBundleSourceIdentityMismatch", reader.Read(bytes, Resource, Revision,
            new(Resource, 0, bytes.Length + 1), []).Diagnostics);
        var parent = Resource with { MemberPath = "owner" };
        var sibling = Resource with { MemberPath = "owner-other" };
        Assert.Contains("ScriptBundleSourceIdentityMismatch", reader.Read(bytes, sibling, Revision,
            new(parent, 0, bytes.Length), []).Diagnostics);
        var nested = Resource with { MemberPath = "owner/bundle" };
        var accepted = reader.Read(bytes, nested, Revision, new(parent, 200, bytes.Length), []);
        Assert.NotNull(accepted.Value);
        Assert.Equal("owner/bundle/script/chunk/0", Assert.Single(accepted.Value.Chunks).Identity.MemberPath);
        Assert.Equal(parent, Assert.Single(accepted.Value.Chunks).BytecodeSpan.Container);
    }

    [Fact]
    public void CancellationThrowsBeforeAndDuringSourceEnumeration()
    {
        using var already = new CancellationTokenSource(); already.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(Build(), token: already.Token));
        using var during = new CancellationTokenSource();
        var sources = new SourceList(2, _ => { during.Cancel(); return new("v.bin", 0, 0); });
        Assert.Throws<OperationCanceledException>(() => Read(Build(), sources: sources, token: during.Token));
        Assert.Equal(1, sources.IndexReads);
    }

    private static DecodeResult<ScriptBundleDocument> Read(byte[] bytes, ScriptBundleLimits? limits = null,
        long offset = 0, IReadOnlyList<SourceExtent>? sources = null, CancellationToken token = default) =>
        new ScriptBundleReader().Read(bytes, Resource, Revision, new(Resource, offset, bytes.Length), sources ?? [], limits, token);
    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private sealed record Chunk(ulong Key, byte[] Bytes, Metadata[] Metadata);
    private sealed record Metadata(ulong Key, byte[] Bytes, byte Flag0 = 0, byte Flag1 = 0);
    private static byte[] Build(params Chunk[] chunks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("QZIP\0SBCHK___"u8); writer.Write(1U); writer.Write(0U); writer.Write((uint)chunks.Length);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Key); writer.Write((uint)chunk.Bytes.Length); writer.Write(chunk.Bytes); writer.Write((uint)chunk.Metadata.Length);
            foreach (var row in chunk.Metadata)
            {
                writer.Write(row.Key); writer.Write(19000U); writer.Write(1U); writer.Write("NOCHK___"u8); writer.Write(1U);
                writer.Write(row.Flag0); writer.Write(row.Flag1); writer.Write((uint)row.Bytes.Length); writer.Write(row.Bytes);
            }
        }
        writer.Flush(); var bytes = stream.ToArray(); WriteWord(bytes, 17, (uint)(bytes.Length - 21)); return bytes;
    }
    private sealed class SourceList(int count, Func<int, SourceExtent> get) : IReadOnlyList<SourceExtent>
    {
        public int Count => count;
        public int IndexReads { get; private set; }
        public SourceExtent this[int index] { get { IndexReads++; return get(index); } }
        public IEnumerator<SourceExtent> GetEnumerator() => throw new InvalidOperationException("Index access required");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
