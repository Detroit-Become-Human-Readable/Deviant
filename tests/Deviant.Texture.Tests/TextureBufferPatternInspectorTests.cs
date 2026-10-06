using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureBufferPatternInspectorTests
{
    private const TextureBufferPatternProfile Profile = TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1;

    [Fact]
    public void RetainsObservedHeaderAndParsesBoundedSegsBytesWithoutImageMetadata()
    {
        var payload = CreateSegs([1, 2, 3, 4]);
        var stored = CreateEnvelope("ETF_RAW_", payload);
        var (identity, revision, extent) = Source(stored);

        var result = new TextureBufferPatternInspector().Parse(stored, identity, revision, extent, 2229, Profile);

        var inspection = Assert.IsType<TextureBufferPatternInspection>(result.Value);
        Assert.Equal(Profile, inspection.Profile);
        Assert.Equal(TextureBufferPatternKind.Raw, inspection.Kind);
        Assert.Equal(stored.AsSpan(0, 32).ToArray(), inspection.HeaderBytes);
        Assert.Equal(stored.AsSpan(21, 4).ToArray(), inspection.RawLengthWord);
        Assert.Equal(stored.AsSpan(25, 7).ToArray(), inspection.SkippedBytes);
        Assert.Equal(extent, inspection.StoredExtent);
        Assert.Equal(new SourceExtent(extent.RelativeSource, extent.Offset + 32, payload.Length, Sha256(payload)), inspection.PayloadExtent);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, inspection.Segments.DecodedPrefix);
        Assert.False(inspection.BoundaryBasis.CursorOriginEstablished);
        Assert.Equal("Inspected", result.UnknownFields["status"]);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("type")]
    [InlineData("range")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("profile")]
    [InlineData("stored-limit")]
    [InlineData("decoded-limit")]
    public void UnsupportedPatternsReturnNoInspection(string mutation)
    {
        var payload = CreateSegs([1, 2, 3, 4]);
        var stored = CreateEnvelope("ETF_RAW_", payload);
        var type = 2229u;
        var profile = Profile;
        var limits = new SegsDecodeLimits(16 * 1024 * 1024, 16 * 1024 * 1024, 2048);
        switch (mutation)
        {
            case "tag": stored[5] = (byte)'X'; break;
            case "type": type = 17; break;
            case "range": BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(21, 4), checked((uint)(payload.Length - 1))); break;
            case "hash": break;
            case "version": BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(32 + 4, 2), 2); break;
            case "profile": profile = (TextureBufferPatternProfile)999; break;
            case "stored-limit": limits = new SegsDecodeLimits(64, 16 * 1024 * 1024, 2048); break;
            case "decoded-limit": limits = new SegsDecodeLimits(16 * 1024 * 1024, 3, 2048); break;
        }
        var (identity, revision, extent) = Source(stored);
        if (mutation == "hash") extent = extent with { PayloadSha256 = new string('0', 64) };

        var result = new TextureBufferPatternInspector().Parse(stored, identity, revision, extent, type, profile, limits);

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, message => message.Contains("Unsupported", StringComparison.Ordinal));
        Assert.Equal("Unsupported", result.UnknownFields["status"]);
    }

    [Fact]
    public void RetainsSegsRawSuffixAndItsPhysicalExtent()
    {
        byte[] suffix = [0xA1, 0xB2, 0xC3];
        var payload = CreateSegs([9, 8, 7], suffix);
        var stored = CreateEnvelope("ETF_RAWL", payload);
        var (identity, revision, extent) = Source(stored);

        var result = new TextureBufferPatternInspector().Parse(stored, identity, revision, extent, 2230, Profile);

        var inspection = Assert.IsType<TextureBufferPatternInspection>(result.Value);
        Assert.Equal(TextureBufferPatternKind.RawLod, inspection.Kind);
        Assert.Equal(suffix, inspection.Segments.RawSuffix);
        Assert.Equal(new SourceExtent(extent.RelativeSource,
            extent.Offset + 32 + payload.Length - suffix.Length, suffix.Length, Sha256(suffix)), inspection.Segments.RawSuffixExtent);
    }

    [Fact]
    public void PreCancelledParsePropagatesCancellation()
    {
        var payload = CreateSegs([1, 2, 3, 4]);
        var stored = CreateEnvelope("ETF_RAW_", payload);
        var (identity, revision, extent) = Source(stored);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new TextureBufferPatternInspector()
            .Parse(stored, identity, revision, extent, 2229, Profile, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task IndexedReadChecksAndRetainsTheOwnerStoredBytesHash()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(CreateEnvelope("ETF_RAW_", CreateSegs([4, 3, 2, 1])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);

        var result = await new TextureBufferPatternInspector().ReadAsync(snapshot, Assert.Single(snapshot.Records), Profile);

        var inspection = Assert.IsType<TextureBufferPatternInspection>(result.Value);
        Assert.Equal(Sha256(fixture.StoredBytes), inspection.StoredExtent.PayloadSha256);
        Assert.Equal(snapshot.IndexContentId, result.Revision.IndexContentId);
    }

    [Fact]
    public async Task IndexedReadPropagatesCancellation()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(CreateEnvelope("ETF_RAW_", CreateSegs([4, 3, 2, 1])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TextureBufferPatternInspector()
            .ReadAsync(snapshot, Assert.Single(snapshot.Records), Profile, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task IndexedReadRejectsChangedIndexAndChangedOwnerBytes()
    {
        using (var fixture = new TextureArchiveFixture())
        {
            fixture.WriteArchive(CreateEnvelope("ETF_RAW_", CreateSegs([1, 2, 3, 4])));
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var changedIndex = await File.ReadAllBytesAsync(fixture.IndexPath);
            changedIndex[30] ^= 1;
            await File.WriteAllBytesAsync(fixture.IndexPath, changedIndex);

            var error = await Assert.ThrowsAsync<ArchiveException>(() => new TextureBufferPatternInspector()
                .ReadAsync(snapshot, Assert.Single(snapshot.Records), Profile));

            Assert.Equal("SourceChanged", error.Code);
        }

        using (var fixture = new TextureArchiveFixture())
        {
            fixture.WriteArchive(CreateEnvelope("ETF_RAW_", CreateSegs([1, 2, 3, 4])));
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var changed = await File.ReadAllBytesAsync(fixture.VolumePath);
            changed[5] ^= 1;
            await File.WriteAllBytesAsync(fixture.VolumePath, changed);

            var error = await Assert.ThrowsAsync<ArchiveException>(() => new TextureBufferPatternInspector()
                .ReadAsync(snapshot, Assert.Single(snapshot.Records), Profile));

            Assert.Equal("SourceChanged", error.Code);
        }
    }

    private static byte[] CreateEnvelope(string tag, byte[] payload)
    {
        var bytes = new byte[32 + payload.Length];
        "QZIP\0"u8.CopyTo(bytes);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 5);
        for (var i = 13; i < 21; i++) bytes[i] = checked((byte)(0x30 + i));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21, 4), checked((uint)payload.Length));
        for (var i = 0; i < 7; i++) bytes[25 + i] = checked((byte)(0x70 + i));
        payload.CopyTo(bytes, 32);
        return bytes;
    }

    private static byte[] CreateSegs(byte[] decoded, byte[]? rawSuffix = null)
    {
        rawSuffix ??= [];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(decoded);
        var compressedBytes = compressed.ToArray();
        var dataStart = 32;
        var bytes = new byte[dataStart + compressedBytes.Length + rawSuffix.Length];
        "segs"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), checked((uint)decoded.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)compressedBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16, 2), checked((ushort)compressedBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), checked((ushort)decoded.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 1);
        compressedBytes.CopyTo(bytes, dataStart);
        rawSuffix.CopyTo(bytes, dataStart + compressedBytes.Length);
        return bytes;
    }

    private static (ResourceIdentity Identity, SourceRevision Revision, SourceExtent Extent) Source(byte[] bytes)
    {
        const string indexId = "synthetic-index";
        return (new(indexId, 0), new(indexId, "synthetic-source"),
            new SourceExtent("sample-volume.dat", 128, bytes.Length, Sha256(bytes)));
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TextureArchiveFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "TextureBufferPatternTests", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");
        public string VolumePath => Path.Combine(root, "sample.dat");
        public byte[] StoredBytes { get; private set; } = [];

        public void WriteArchive(byte[] stored)
        {
            Directory.CreateDirectory(root);
            StoredBytes = stored;
            File.WriteAllBytes(VolumePath, stored);
            var index = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength];
            IndexLayout.Magic.CopyTo(index);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20, 4), IndexLayout.SupportedVersion);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength, 4), 2229);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + 8, 4), 101);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + 16, 4), checked((uint)stored.Length));
            File.WriteAllBytes(IndexPath, index);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
