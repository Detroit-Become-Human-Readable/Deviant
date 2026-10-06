using System.Buffers.Binary;
using System.IO.Compression;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeTextureEstimateTests
{
    [Fact]
    public async Task SourceVerificationHonorsItsByteAndExtentLimits()
    {
        using var archive = new GeneratedTextureArchive();
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        var row = snapshot.Records[0];
        var bytes = await new ArchiveResourceResolver(snapshot).ReadAsync(new(snapshot.IndexContentId, row.Ordinal),
            0, checked((int)row.StoredSize));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var extent = new SourceExtent(row.VolumeName, row.Offset, row.StoredSize, hash);
        var verifier = new SourceExtentVerifier();
        await Assert.ThrowsAsync<ArgumentException>(() => verifier.AcquireAsync(snapshot, [extent], maximumBytes: row.StoredSize - 1));
        using var pin = await verifier.AcquireAsync(snapshot, [extent], maximumBytes: row.StoredSize);
        pin.EnsureUnchanged();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => verifier.AcquireAsync(snapshot, [extent], maximumBytes: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => verifier.AcquireAsync(snapshot, [extent], maximumBytes: SourceExtentVerifier.MaximumBytes + 1));
        var manyExtents = Enumerable.Repeat(extent, SourceExtentVerifier.MaximumExtentCount + 1).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => verifier.AcquireAsync(snapshot, manyExtents,
            maximumBytes: SourceExtentVerifier.MaximumBytes));
        using var largePin = await verifier.AcquireAsync(snapshot, manyExtents,
            maximumBytes: SourceExtentVerifier.MaximumBytes, maximumExtentCount: SourceExtentVerifier.MaximumLargeExtentCount);
        largePin.EnsureUnchanged();
    }

    [Fact]
    public async Task ModelLoadRejectsANonCatalogIdentityBeforeLoadingAssets()
    {
        using var archive = new GeneratedTextureArchive();
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var textures = new NativeTextureSession(catalogs);
        var budget = new AssetMemoryBudget();
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), budget);
        var before = budget.UsedBytes;
        var models = new NativeModelSession(textures, budget);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => models.LoadAsync(companion,
            companion.Identity, []));
        Assert.Equal("NativeCatalogUnavailable", error.Code);
        Assert.Equal(before, budget.UsedBytes);
        companion.EnsureUnchanged();
    }

    [Theory]
    [InlineData(true, false, 5)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public async Task EstimateMatchesActualSourceForExplicitAliases(bool packed, bool converted, byte format)
    {
        using var archive = new GeneratedTextureArchive(packed, converted, format);
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        foreach (var frame in companion.Directory.Objects)
        {
            var estimate = await session.EstimateNativeSourceAsync(frame.Identity, new(snapshot.IndexContentId, 0), companion);
            var result = await session.ReadForNativeAsync(frame.Identity, new(snapshot.IndexContentId, 0), companion,
                reservedSourceBytes: NativeTextureSession.MaximumRetainedSourceBytes - estimate.RetainedSourceBytes);
            var source = Assert.IsType<NativeTextureSource>(result.Source);
            Assert.Equal(source.RetainedSourceBytes, estimate.RetainedSourceBytes);
            Assert.Equal(source.MetadataBytes.Length, estimate.MetadataBytes);
            Assert.Equal(source.Payloads.Count, estimate.RequiredPayloadCount);
            Assert.Equal(8, estimate.Width); Assert.Equal(4, estimate.Height); Assert.Equal(4, estimate.MipCount);
            Assert.Equal(converted, estimate.Converted); Assert.Equal(format, estimate.NativeFormat);
        }
        companion.EnsureUnchanged();
    }

    [Theory]
    [InlineData("duplicate", "AmbiguousNativeTextureReference")]
    [InlineData("tail", "TextureMetadataUnavailable")]
    public async Task MetadataAndResolutionFailuresAreTyped(string defect, string code)
    {
        using var archive = new GeneratedTextureArchive(defect: defect);
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var error = await Assert.ThrowsAsync<ArchiveException>(() => session.EstimateNativeSourceAsync(
            companion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), companion));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task RepeatedResolvedPayloadIsCountedOnce()
    {
        using var archive = new GeneratedTextureArchive(packed: false, defect: "shared");
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var native = companion.Directory.Objects[0].Identity; var catalog = new ResourceIdentity(snapshot.IndexContentId, 0);
        var estimate = await session.EstimateNativeSourceAsync(native, catalog, companion);
        var read = await session.ReadForNativeAsync(native, catalog, companion);
        var source = Assert.IsType<NativeTextureSource>(read.Source);
        Assert.Equal(3, source.Metadata.References.Count);
        Assert.Equal(2, estimate.RequiredPayloadCount);
        Assert.Equal(source.RetainedSourceBytes, estimate.RetainedSourceBytes);
    }

    [Fact]
    public async Task ResourceCapsAreCheckedFromRowsBeforePayloadRead()
    {
        using var archive = new GeneratedTextureArchive();
        var original = await new ArchiveReader().OpenAsync(archive.IndexPath);
        var snapshot = original with { Records = original.Records.Select(row => row.Type == 2229
            ? row with { StoredSize = NativeTextureSession.MaximumResourceBytes + 1L } : row).ToArray() };
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var error = await Assert.ThrowsAsync<ArchiveException>(() => session.EstimateNativeSourceAsync(
            companion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), companion));
        Assert.Equal("TextureResourceLimit", error.Code);
        companion.EnsureUnchanged();
    }

    [Fact]
    public async Task EstimateHonorsCancellationAndSessionDisposal()
    {
        using var archive = new GeneratedTextureArchive();
        var snapshot = await new ArchiveReader().OpenAsync(archive.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var native = companion.Directory.Objects[0].Identity; var catalog = new ResourceIdentity(snapshot.IndexContentId, 0);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EstimateNativeSourceAsync(native, catalog, companion, cancellation.Token));
        var estimate = await session.EstimateNativeSourceAsync(native, catalog, companion);
        Assert.True(estimate.RetainedSourceBytes > 0);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.EstimateNativeSourceAsync(native, catalog, companion));
        companion.EnsureUnchanged();
    }

    private sealed class GeneratedTextureArchive : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantTextureEstimateTests", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");
        public GeneratedTextureArchive(bool packed = true, bool converted = false, byte format = 5, string? defect = null)
        {
            Directory.CreateDirectory(root);
            var source = NativeMipFixture.Create(format, packed, conversion: converted);
            var metadata = source.MetadataBytes.ToArray();
            if (defect == "shared") BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(metadata.Length - 8), source.Payloads[1].Id);
            using var catalog = new MemoryStream(); using (var writer = new BinaryWriter(catalog, System.Text.Encoding.UTF8, true))
            {
                writer.Write("COM_CONT"u8); writer.Write(6u);
                writer.Write(4u + 9u * (uint)(source.Payloads.Count + 1)); writer.Write((uint)(source.Payloads.Count + 1));
                writer.Write(29u); writer.Write(50u); writer.Write((byte)0);
                foreach (var payload in source.Payloads) { writer.Write(payload.Type); writer.Write(payload.Id); writer.Write((byte)0); }
            }
            var payloads = new List<(uint Type, uint Id, byte[] Bytes)> { (3, 1, catalog.ToArray()),
                (29, 50, Companion(metadata, defect == "tail")) };
            payloads.AddRange(source.Payloads.Select(payload => (payload.Type, payload.Id, payload.OriginalBytes.ToArray())));
            if (defect == "duplicate") payloads.Add((source.Payloads[0].Type, source.Payloads[0].Id, source.Payloads[0].OriginalBytes.ToArray()));
            using var volume = new MemoryStream();
            var rows = new List<(uint Type, uint Id, uint Offset, uint Size)>();
            foreach (var payload in payloads)
            {
                rows.Add((payload.Type, payload.Id, (uint)volume.Position, (uint)payload.Bytes.Length)); volume.Write(payload.Bytes);
            }
            File.WriteAllBytes(Path.ChangeExtension(IndexPath, ".dat"), volume.ToArray());
            var index = new byte[IndexLayout.HeaderLength + rows.Count * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
            Put(index, 20, IndexLayout.SupportedVersion); Put(index, 101, (uint)rows.Count);
            for (var ordinal = 0; ordinal < rows.Count; ordinal++)
            {
                var row = rows[ordinal]; var at = IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength;
                Put(index, at, row.Type); Put(index, at + 8, row.Id); Put(index, at + 12, row.Offset); Put(index, at + 16, row.Size);
            }
            File.WriteAllBytes(IndexPath, index);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        private static byte[] Companion(byte[] metadata, bool invalidTail)
        {
            using var body = new MemoryStream();
            for (var alias = 0; alias < 2; alias++)
            {
                var record = metadata.ToArray(); record[12 + 16] = (byte)alias;
                using var frame = new MemoryStream();
                using (var writer = new BinaryWriter(frame, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(4u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
                    writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write(record); writer.Write("FTEXCRAW"u8); writer.Write(invalidTail ? 2u : 1u);
                }
                using var bodyWriter = new BinaryWriter(body, System.Text.Encoding.UTF8, true);
                bodyWriter.Write((uint)frame.Length); bodyWriter.Write((byte)0); bodyWriter.Write(0u); bodyWriter.Write(0u); bodyWriter.Write(frame.ToArray());
            }
            using var decoded = new MemoryStream();
            using (var writer = new BinaryWriter(decoded, System.Text.Encoding.UTF8, true))
            {
                writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(24u); writer.Write(3u); writer.Write(2u);
                writer.Write(2137u); writer.Write(100u); writer.Write(2137u); writer.Write(101u);
                writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length); writer.Write(body.ToArray());
            }
            var bytes = decoded.ToArray(); using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(bytes);
            var packed = compressed.ToArray(); var segs = new byte[32 + packed.Length]; "segs"u8.CopyTo(segs);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), (uint)bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), (uint)packed.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)packed.Length); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20), 1); packed.CopyTo(segs, 32); return segs;
        }
    }
}
