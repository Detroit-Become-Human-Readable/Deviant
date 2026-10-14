using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed partial class NativeTextureSessionTests
{
    [Fact]
    public async Task MetadataOnlyNativeTextureSourceCanBeCapturedAndFingerprintedForExport()
    {
        using var fixture = new NativeFrameArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);

        using var metadataCompanion = await NativeContainerLease.OpenMetadataAsync(snapshot,
            new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        Assert.True(metadataCompanion.IsMetadataOnly);
        var metadataRead = await session.ReadForNativeAsync(metadataCompanion.Directory.Objects[0].Identity,
            new(snapshot.IndexContentId, 0), metadataCompanion);
        Assert.True(metadataRead.Source is not null, $"{metadataRead.FailureCode}: {metadataRead.Reason}");
        var previewSource = Assert.IsType<NativeTextureSource>(metadataRead.Source);
        var metadataSnapshot = CaptureForExport(session, previewSource);
        var metadataKey = FingerprintForExport(metadataSnapshot);

        using var completeCompanion = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var completeResult = await session.ReadForNativeAsync(previewSource.NativeTextureIdentity!,
            new(snapshot.IndexContentId, 0), completeCompanion);
        var completeSource = Assert.IsType<NativeTextureSource>(completeResult.Source);
        Assert.False(completeCompanion.IsMetadataOnly);
        var completeSnapshot = CaptureForExport(session, completeSource);
        var completeKey = FingerprintForExport(completeSnapshot);

        Assert.Equal(metadataKey, completeKey);
    }

    private static object CaptureForExport(NativeTextureSession session, NativeTextureSource source)
    {
        var method = typeof(NativeTextureSession).GetMethod("CaptureNativeValidation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(session, [source, CancellationToken.None])
            ?? throw new InvalidOperationException("Native texture validation did not produce a snapshot.");
    }

    private static string FingerprintForExport(object validation)
    {
        var method = typeof(NativeTextureSession).Assembly.GetType("Deviant.Browser.NativeModelExportPreparationCache")!
            .GetMethod("TextureKey", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [validation, null]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvesExplicitNativeAliasesWhileArchiveSelectionRemainsAmbiguous(bool metadataOnly)
    {
        using var fixture = new NativeFrameArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        await using var session = new NativeTextureSession(catalogs);
        using var companion = metadataOnly
            ? await NativeContainerLease.OpenMetadataAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget())
            : await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var first = await session.ReadForNativeAsync(companion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), companion);
        var second = await session.ReadForNativeAsync(companion.Directory.Objects[1].Identity, new(snapshot.IndexContentId, 0), companion);
        Assert.NotNull(first.Source); Assert.NotNull(second.Source);
        Assert.Equal(first.Source.SelectedIdentity, second.Source.SelectedIdentity);
        Assert.NotEqual(first.Source.Metadata.Identity, second.Source.Metadata.Identity);
        Assert.NotEqual(first.Source.MetadataSha256, second.Source.MetadataSha256);
        Assert.NotEqual(NativeTextureSession.SourceVersion(first.Source), NativeTextureSession.SourceVersion(second.Source));
        Assert.Equal(companion.Directory.Objects[0].Identity, first.Source.NativeTextureIdentity);
        Assert.Equal(companion.Directory.Objects[1].Identity, second.Source.NativeTextureIdentity);
        Assert.Same(companion, first.Source.CompanionLease);
        Assert.Equal(first.Source.MetadataBytes.Length + first.Source.Payloads.Sum(payload => (long)payload.OriginalBytes.Length),
            first.Source.RetainedSourceBytes);
        await session.RevalidateAsync(first.Source);
        await session.RevalidateAsync(second.Source);
        var archiveOnly = await session.ReadAsync(first.Source.SelectedIdentity);
        Assert.Null(archiveOnly.Source);
        Assert.Contains("Multiple texture records", archiveOnly.Reason);
        Assert.Equal("TextureMetadataAmbiguous", archiveOnly.FailureCode);
    }

    [Fact]
    public async Task NativeReadAndRevalidationDoNotDependOnCatalogDiscoveryCompletion()
    {
        using var fixture = new NativeFrameArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            sessionOptions: new(MaximumCatalogs: 1, PriorityCatalogs: [new(snapshot.IndexContentId, 3)]));
        await catalogs.EnrichmentTask;
        Assert.DoesNotContain(catalogs.Groups, group => group.CatalogIdentity.Ordinal == 0);
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var result = await session.ReadForNativeAsync(companion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), companion);
        Assert.NotNull(result.Source);
        await session.RevalidateAsync(result.Source);
        using var pin = session.PinSource(result.Source);
        companion.Dispose();
        Assert.NotNull(companion.Directory);
        companion.EnsureUnchanged();
    }

    [Fact]
    public async Task RejectsCatalogCompanionMismatchAndForeignRevision()
    {
        using var fixture = new NativeFrameArchive(); using var otherFixture = new NativeFrameArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var otherSnapshot = await new ArchiveReader().OpenAsync(otherFixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var wrongCompanion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 4), new AssetMemoryBudget());
        var mismatch = await Assert.ThrowsAsync<ArchiveException>(() => session.ReadForNativeAsync(
            wrongCompanion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), wrongCompanion));
        Assert.Equal("TextureSourceMismatch", mismatch.Code);
        using var foreign = await NativeContainerLease.OpenAsync(otherSnapshot, new(otherSnapshot.IndexContentId, 1), new AssetMemoryBudget());
        var changed = await Assert.ThrowsAsync<ArchiveException>(() => session.ReadForNativeAsync(
            foreign.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), foreign));
        Assert.Equal("SourceChanged", changed.Code);
    }

    [Fact]
    public async Task RejectsAmbiguousBackingResourcesAndMalformedClassBoundary()
    {
        foreach (var defect in new[] { "duplicate", "tail" })
        {
            using var fixture = new NativeFrameArchive(defect);
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
            await using var session = new NativeTextureSession(catalogs);
            using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
            var result = await session.ReadForNativeAsync(companion.Directory.Objects[0].Identity, new(snapshot.IndexContentId, 0), companion);
            Assert.Null(result.Source);
            Assert.NotNull(result.Reason);
        }
    }

    [Fact]
    public async Task HonorsCancellationBudgetAndExplicitContextDuringRevalidation()
    {
        using var fixture = new NativeFrameArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var session = new NativeTextureSession(catalogs);
        using var companion = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), new AssetMemoryBudget());
        var identity = companion.Directory.Objects[0].Identity; var catalog = new ResourceIdentity(snapshot.IndexContentId, 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadForNativeAsync(identity, catalog, companion, cancelled.Token));
        var limited = await session.ReadForNativeAsync(identity, catalog, companion, reservedSourceBytes: NativeTextureSession.MaximumRetainedSourceBytes);
        Assert.Null(limited.Source); Assert.Contains("budget", limited.Reason);
        var result = await session.ReadForNativeAsync(identity, catalog, companion);
        var source = Assert.IsType<NativeTextureSource>(result.Source);
        var wrongContext = source with { NativeTextureIdentity = companion.Directory.Objects[1].Identity };
        var mismatch = await Assert.ThrowsAsync<ArchiveException>(() => session.RevalidateAsync(wrongContext));
        Assert.Equal("TextureSourceMismatch", mismatch.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RevalidateAsync(source, cancelled.Token));
        companion.EnsureUnchanged();
    }

    [Fact]
    public async Task ArchivePreviewReadsOnlyTheChosenMipFromAnOversizedCompanion()
    {
        using var fixture = new NativeFrameArchive(includeOversizedSources: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        await using var session = new NativeTextureSession(catalogs);
        var budget = new AssetMemoryBudget();

        var result = await session.ReadPreviewAsync(new(snapshot.IndexContentId, 2), budget, maximumPreviewDimension: 4);

        var source = Assert.IsType<NativeTextureSource>(result.Source);
        using var previewLease = Assert.IsType<NativeContainerLease>(source.CompanionLease);
        Assert.Equal(new(snapshot.IndexContentId, 2), source.SelectedIdentity);
        Assert.Equal(4, source.PreviewDimension);
        Assert.True(source.CompanionLease?.IsMetadataOnly);
        Assert.Equal(new[] { new ResourceIdentity(snapshot.IndexContentId, 4) }, source.Payloads.Select(payload => payload.Identity));
        var document = new NativeTextureDecoder().ReadPreview(source, 4).Document;
        Assert.Equal(4, document.Info.Width);
        Assert.Equal(2, document.Info.Height);
        Assert.True(snapshot.RequireRecord(1).StoredSize > NativeTextureSession.MaximumResourceBytes);
        Assert.True(snapshot.RequireRecord(3).StoredSize > NativeTextureSession.MaximumResourceBytes);
        await session.RevalidateAsync(source);
    }

    [Theory]
    [InlineData(true, 2, 4, 4, 2)]
    [InlineData(false, 3, 8, 8, 4)]
    public async Task LegacyFileTextMetadataSupportsSelectivePreviewAndMipResourceSelection(
        bool packed, long selectedOrdinal, int maximumDimension, int expectedWidth, int expectedHeight)
    {
        using var fixture = new NativeFrameArchive(includeLegacyMetadata: true, packed: packed);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        await using var session = new NativeTextureSession(catalogs);
        var result = await session.ReadPreviewAsync(new(snapshot.IndexContentId, selectedOrdinal),
            new AssetMemoryBudget(), maximumDimension);

        var source = Assert.IsType<NativeTextureSource>(result.Source);
        using var previewLease = Assert.IsType<NativeContainerLease>(source.CompanionLease);
        Assert.True(previewLease.IsMetadataOnly);
        Assert.StartsWith("filetext/", source.Metadata.Identity.MemberPath);
        Assert.Equal(new(snapshot.IndexContentId, selectedOrdinal), source.SelectedIdentity);
        var document = new NativeTextureDecoder().ReadPreview(source, maximumDimension).Document;
        Assert.Equal(expectedWidth, document.Info.Width);
        Assert.Equal(expectedHeight, document.Info.Height);
        await session.RevalidateAsync(source);
    }

    private sealed class NativeFrameArchive : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantNativeFrameTests", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");
        public NativeFrameArchive(string? defect = null, bool includeExportFrames = false,
            bool includeOversizedSources = false, bool includeLegacyMetadata = false, bool packed = true)
        {
            Directory.CreateDirectory(root);
            var source = NativeMipFixture.Create(packed: includeLegacyMetadata ? packed : !includeOversizedSources);
            var first = source.MetadataBytes.ToArray(); var second = source.MetadataBytes.ToArray();
            if (defect == "unsupported-format") first[12] = 9;
            second[12 + 16] = 1;
            var companion = WrapContainer(includeOversizedSources || includeLegacyMetadata ? [first] : includeExportFrames ? [first, second, first, first] : [first, second],
                defect == "tail", includeExportFrames, includeLegacyMetadata);
            if (includeOversizedSources) Array.Resize(ref companion, 17 * 1024 * 1024);
            var payloads = new List<(uint Type, uint Id, byte[] Bytes)>();
            payloads.Add((3, 1, Catalog(50, source.Payloads)));
            payloads.Add((29, 50, companion));
            payloads.AddRange(source.Payloads.Select(payload => (payload.Type, payload.Id,
                includeOversizedSources && payload.Id == 11 ? new byte[17 * 1024 * 1024] : payload.OriginalBytes.ToArray())));
            if (!includeOversizedSources && !includeLegacyMetadata)
            {
                payloads.Add((3, 2, Catalog(51, source.Payloads)));
                payloads.Add((29, 51, companion));
            }
            if (defect == "duplicate") payloads.Add((2229, 10, source.Payloads[0].OriginalBytes.ToArray()));
            using var volume = new MemoryStream();
            var rows = new List<(uint Type, uint Id, uint Offset, uint Size)>();
            foreach (var payload in payloads)
            {
                rows.Add((payload.Type, payload.Id, (uint)volume.Position, (uint)payload.Bytes.Length));
                volume.Write(payload.Bytes);
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
        private static byte[] Catalog(uint companionId, IReadOnlyList<NativeTexturePayload> textures)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(checked(4u + (uint)(textures.Count + 1) * 9)); writer.Write(checked((uint)(textures.Count + 1)));
            writer.Write(29u); writer.Write(companionId); writer.Write((byte)0);
            foreach (var texture in textures) { writer.Write(texture.Type); writer.Write(texture.Id); writer.Write((byte)0); }
            return stream.ToArray();
        }
        private static byte[] WrapContainer(byte[][] metadata, bool invalidTail, bool includeExportFrames, bool legacyMetadata = false)
        {
            using var body = new MemoryStream();
            foreach (var record in metadata)
            {
                using var frame = new MemoryStream();
                using (var writer = new BinaryWriter(frame, System.Text.Encoding.UTF8, true))
                {
                    if (!legacyMetadata)
                    {
                        writer.Write(4u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
                        writer.Write("LOADCONT"u8); writer.Write(2u);
                    }
                    writer.Write(record);
                    writer.Write("FTEXCRAW"u8); writer.Write(invalidTail ? 2u : 1u);
                }
                using var bodyWriter = new BinaryWriter(body, System.Text.Encoding.UTF8, true);
                bodyWriter.Write((uint)frame.Length); bodyWriter.Write((byte)0); bodyWriter.Write(0u); bodyWriter.Write(0u); bodyWriter.Write(frame.ToArray());
            }
            using var decoded = new MemoryStream();
            using (var writer = new BinaryWriter(decoded, System.Text.Encoding.UTF8, true))
            {
                writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(8u + (uint)metadata.Length * 8); writer.Write(3u); writer.Write((uint)metadata.Length);
                for (var frame = 0; frame < metadata.Length; frame++)
                { writer.Write(includeExportFrames && frame == 2 ? 2130u : includeExportFrames && frame == 3 ? 2133u : 2137u); writer.Write((uint)frame + 100); }
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
