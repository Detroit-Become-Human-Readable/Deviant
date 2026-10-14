using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeModelSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedMaterialsReuseGeometryAndKeepItsOwnershipIndependent(bool releaseGeometryFirst)
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var before = fixture.Budget.UsedBytes;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var geometryMethod = typeof(NativeModelSession).GetMethod("LoadGeometryAsync", flags)!;
        var materialsMethod = typeof(NativeModelSession).GetMethod("LoadMaterialsAsync", flags)!;
        var geometry = await (Task<NativeModelBatch>)geometryMethod.Invoke(fixture.Models,
            [fixture.Container, fixture.CatalogIdentity, fixture.Selections, null, CancellationToken.None, 1024, NativeModelSession.MaximumSelections])!;
        NativeModelBatch? complete = null;
        try
        {
            Assert.Empty(geometry.Materials);
            complete = await (Task<NativeModelBatch>)materialsMethod.Invoke(fixture.Models,
                [fixture.Container, fixture.CatalogIdentity, geometry, null, CancellationToken.None, 1024])!;
            Assert.Empty(geometry.Materials);
            Assert.Equal(geometry.Meshes.Count, complete.Meshes.Count);
            Assert.All(geometry.Meshes, pair => Assert.Same(pair.Value.Geometry, complete.Meshes[pair.Key].Geometry));
            if (releaseGeometryFirst) geometry.Dispose(); else complete.Dispose();
            Assert.True(fixture.Budget.UsedBytes > before);
            fixture.Container.EnsureUnchanged();
        }
        finally { complete?.Dispose(); geometry.Dispose(); }
        Assert.Equal(before, fixture.Budget.UsedBytes);
    }
    [Fact]
    public async Task RetainedSkinSourceDoesNotCreateACapturedPoseAndReleasesItsBudget()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var baseline = fixture.Budget.UsedBytes;
        using (var snapshot = await fixture.Models.LoadSnapshotAsync(fixture.Container, fixture.CatalogIdentity,
            [fixture.Selections[0]], retainSkinSource: true))
        {
            var mesh = Assert.Single(snapshot.Meshes.Values);
            Assert.Null(mesh.Pose);
            Assert.Same(mesh.Geometry, mesh.SkinSourceGeometry);
            Assert.NotNull(mesh.SkinSourceMetadata);
            Assert.NotNull(mesh.SkinAttributes);
            Assert.Equal(mesh.Identity, mesh.SkinAttributes.Identity);
            Assert.Equal(mesh.GroupIndex, mesh.SkinAttributes.GroupIndex);
        }
        Assert.Equal(baseline, fixture.Budget.UsedBytes);
        fixture.Container.EnsureUnchanged();
    }

    [Fact]
    public async Task SnapshotRetainsMultipleMeshGroupsAsModelLocalPlacements()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var before = fixture.Budget.UsedBytes;
        var snapshot = await fixture.Models.LoadSnapshotAsync(fixture.Container, fixture.CatalogIdentity,
            fixture.Selections);
        try
        {
            Assert.Equal(3, snapshot.Meshes.Count);
            Assert.Equal(3, snapshot.Occurrences.Count);
            Assert.Empty(snapshot.Hierarchy);
            Assert.Equal(0, snapshot.Coverage.TestedOccurrences);
            Assert.Equal(6, snapshot.Coverage.RenderedParts);
            Assert.Same(fixture.Budget, snapshot.MemoryBudget);
            Assert.All(snapshot.Occurrences, occurrence =>
            {
                Assert.Equal(RenderPlacementOrigin.ModelLocal, occurrence.PlacementOrigin);
                var placement = Assert.IsType<ModelLocalPlacement>(occurrence.ModelPlacement);
                Assert.Equal(placement.MeshIdentity, occurrence.ModelAnchorIdentity);
                Assert.Null(occurrence.SceneEntityIdentity);
                Assert.Null(occurrence.SceneNodeIdentity);
                Assert.False(occurrence.SourceVisibilityKnown);
                Assert.Null(occurrence.NativeFlags);
                Assert.Equal(Matrix4x4.Identity, occurrence.World);
                Assert.Equal(placement.MeshIdentity, occurrence.Entity);
                Assert.Equal(placement.MeshIdentity, occurrence.Node);
                Assert.Contains(occurrence.GeometryKey, snapshot.Meshes.Keys);
            });
            Assert.Contains(snapshot.Occurrences, occurrence => occurrence.ModelPlacement ==
                new ModelLocalPlacement(fixture.MeshIdentities[0], 0));
            Assert.Contains(snapshot.Occurrences, occurrence => occurrence.ModelPlacement ==
                new ModelLocalPlacement(fixture.MeshIdentities[0], 1));
            Assert.Contains(snapshot.Occurrences, occurrence => occurrence.ModelPlacement ==
                new ModelLocalPlacement(fixture.MeshIdentities[1], 1));
            Assert.Equal(new GeometryBounds(new(2, 3, 4), new(4, 5, 6)), snapshot.Region);
            Assert.True(fixture.Budget.UsedBytes > before);
            fixture.Container.EnsureUnchanged();
        }
        finally
        {
            snapshot.Dispose();
            Assert.Equal(before, fixture.Budget.UsedBytes);
            fixture.Container.EnsureUnchanged();
        }
    }

    [Fact]
    public async Task MetadataLeaseLoadsSelectedGeometryFromGpuRangesWithoutHydratingTheRemainder()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var baseline = fixture.Budget.UsedBytes;
        using (var metadata = await NativeContainerLease.OpenMetadataAsync(
                   fixture.Snapshot, fixture.Container.Identity, fixture.Budget))
        {
            Assert.True(metadata.IsMetadataOnly);
            Assert.True(metadata.Directory.RemainderBytes.IsEmpty);
            Assert.NotNull(metadata.Directory.DeclaredRemainderExtent);

            using (var full = await fixture.Models.LoadSnapshotAsync(fixture.Container, fixture.CatalogIdentity,
                       fixture.Selections))
            using (var selected = await fixture.Models.LoadSnapshotAsync(metadata, fixture.CatalogIdentity,
                       fixture.Selections))
            {
                Assert.Equal(full.Meshes.Keys.OrderBy(key => key, StringComparer.Ordinal),
                    selected.Meshes.Keys.OrderBy(key => key, StringComparer.Ordinal));
                foreach (var (key, expected) in full.Meshes)
                {
                    var actual = selected.Meshes[key];
                    Assert.Equal(expected.Geometry.Primitives.Count, actual.Geometry.Primitives.Count);
                    for (var index = 0; index < expected.Geometry.Primitives.Count; index++)
                    {
                        Assert.Equal(expected.Geometry.Primitives[index].Positions, actual.Geometry.Primitives[index].Positions);
                        Assert.Equal(expected.Geometry.Primitives[index].Indices, actual.Geometry.Primitives[index].Indices);
                    }
                }
                Assert.Contains(selected.SourceExtents, extent => extent.Length > 0 && extent.PayloadSha256 is { Length: 64 });
            }
            metadata.EnsureUnchanged();
        }
        Assert.Equal(baseline, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task MetadataLeaseLoadsSelectedGeometryFromCompressedContainerSuffix()
    {
        await using var fixture = await ModelArchive.CreateAsync(outerCompressed: true);
        var baseline = fixture.Budget.UsedBytes;
        var budget = fixture.Budget;
        using (var metadata = await NativeContainerLease.OpenMetadataAsync(
                   fixture.Snapshot, fixture.Container.Identity, fixture.Budget))
        {
            Assert.True(metadata.IsMetadataOnly);
            Assert.True(metadata.Directory.RemainderBytes.IsEmpty);
            Assert.NotNull(metadata.Directory.DeclaredRemainderExtent);

            using var expected = await fixture.Models.LoadSnapshotAsync(fixture.Container,
                fixture.CatalogIdentity, fixture.Selections);
            using var actual = await fixture.Models.LoadSnapshotAsync(metadata,
                fixture.CatalogIdentity, fixture.Selections);
            Assert.Equal(expected.Meshes.Keys.OrderBy(key => key, StringComparer.Ordinal),
                actual.Meshes.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (var (key, expectedMesh) in expected.Meshes)
                for (var index = 0; index < expectedMesh.Geometry.Primitives.Count; index++)
                    Assert.Equal(expectedMesh.Geometry.Primitives[index].Positions,
                        actual.Meshes[key].Geometry.Primitives[index].Positions);
            metadata.EnsureUnchanged();
        }
        Assert.True(budget.UsedBytes <= baseline);
        await fixture.DisposeAsync();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MetadataLeaseReadsSelectedGpuRangesFromLargeSparseRemainder()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = await ModelArchive.CreateAsync(largeSparseUnusedSuffix: true);
        var row = fixture.Snapshot.RequireRecord(fixture.Container.Identity.Ordinal);
        Assert.True(row.StoredSize >= 200L * 1024 * 1024);
        using (var metadata = fixture.Container)
        {
            var beforeLoad = fixture.Budget.UsedBytes;
            Assert.True(metadata.IsMetadataOnly);
            Assert.True(metadata.Directory.RemainderBytes.IsEmpty);
            using (var selected = await fixture.Models.LoadSnapshotAsync(metadata, fixture.CatalogIdentity,
                       [fixture.Selections[0]]))
            {
                Assert.Single(selected.Meshes);
                Assert.Contains(selected.SourceExtents, extent => extent.Offset > row.Offset && extent.Length < row.StoredSize);
            }
            Assert.Equal(beforeLoad, fixture.Budget.UsedBytes);
            metadata.EnsureUnchanged();
        }
    }

    [Fact]
    public async Task CancelledMetadataGpuReadDoesNotRetainRangeMemory()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(
            fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var before = fixture.Budget.UsedBytes;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeGpuReader.ReadAsync(
            metadata, fixture.MeshIdentities[0], cancelled.Token));
        Assert.Equal(before, fixture.Budget.UsedBytes);
        metadata.EnsureUnchanged();
    }

    [Fact]
    public async Task MetadataGpuReadRejectsChangedSelectedSourceRangeAfterClosingSourcePins()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(
            fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var snapshot = fixture.Snapshot;
        var identity = fixture.Container.Identity;
        var row = snapshot.RequireRecord(identity.Ordinal);
        var remainder = Assert.IsType<DecodedExtent>(metadata.Directory.DeclaredRemainderExtent);
        long changedOffset = checked(row.Offset + 5 + remainder.Offset + 40);
        var volume = snapshot.Volumes.Single(item => item.Package == row.Package);
        var changedBytes = await File.ReadAllBytesAsync(volume.FilePath);
        changedBytes[checked((int)changedOffset)] ^= 0x5a;
        metadata.Dispose();
        fixture.Container.Dispose();
        await fixture.Textures.DisposeAsync();
        await fixture.Catalogs.DisposeAsync();
        await File.WriteAllBytesAsync(volume.FilePath, changedBytes);
        File.SetLastWriteTimeUtc(volume.FilePath, new DateTime(volume.Stamp.LastWriteUtcTicks,
            DateTimeKind.Utc).AddSeconds(4));

        var error = await Assert.ThrowsAsync<ArchiveException>(() => NativeContainerLease.OpenMetadataAsync(
            snapshot, identity, fixture.Budget));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task CancelledAndBudgetLimitedLoadsReleaseTemporaryReservations()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var before = fixture.Budget.UsedBytes;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Models.LoadSnapshotAsync(
            fixture.Container, fixture.CatalogIdentity, fixture.Selections, token: cancelled.Token));
        Assert.Equal(before, fixture.Budget.UsedBytes);

        using var hold = fixture.Budget.Reserve(fixture.Budget.MaximumBytes - fixture.Budget.UsedBytes - 1,
            "generated budget guard");
        var heldBytes = fixture.Budget.UsedBytes;
        var error = await Assert.ThrowsAsync<ArchiveException>(() => fixture.Models.LoadSnapshotAsync(
            fixture.Container, fixture.CatalogIdentity, fixture.Selections));
        Assert.Equal("AssetMemoryLimit", error.Code);
        Assert.Equal(heldBytes, fixture.Budget.UsedBytes);
        fixture.Container.EnsureUnchanged();
    }

    [Fact]
    public async Task CancellationAfterGeometryDecodeReleasesAllBatchReservations()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var before = fixture.Budget.UsedBytes;
        using var cancelled = new CancellationTokenSource();
        var decodedGeometry = false;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage != "Geometry" || value.Completed != 1) return;
            decodedGeometry = true;
            Assert.True(fixture.Budget.UsedBytes > before);
            cancelled.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Models.LoadSnapshotAsync(
            fixture.Container, fixture.CatalogIdentity, fixture.Selections, progress, cancelled.Token));
        Assert.True(decodedGeometry);
        Assert.Equal(before, fixture.Budget.UsedBytes);
        fixture.Container.EnsureUnchanged();
    }

    private sealed class InlineProgress(Action<RenderLoadProgress> report) : IProgress<RenderLoadProgress>
    {
        public void Report(RenderLoadProgress value) => report(value);
    }

    [Fact]
    public async Task SnapshotLoadRejectsAnotherArchiveRevisionWithoutTakingOwnership()
    {
        var budget = new AssetMemoryBudget();
        await using var current = await ModelArchive.CreateAsync(budget);
        var beforeForeign = budget.UsedBytes;
        await using var foreign = await ModelArchive.CreateAsync(budget);
        var withForeignContainer = budget.UsedBytes;
        var error = await Assert.ThrowsAsync<ArchiveException>(() => current.Models.LoadSnapshotAsync(
            foreign.Container, current.CatalogIdentity, current.Selections));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(withForeignContainer, budget.UsedBytes);
        foreign.Container.EnsureUnchanged(); current.Container.EnsureUnchanged();
        await foreign.DisposeAsync();
        Assert.Equal(beforeForeign, budget.UsedBytes);
    }

    [Fact]
    public async Task SnapshotLoadKeepsTheExistingSelectionLimit()
    {
        await using var fixture = await ModelArchive.CreateAsync();
        var repeated = Enumerable.Repeat(fixture.Selections[0], 2).ToArray();
        var error = await Assert.ThrowsAsync<ArchiveException>(() => fixture.Models.LoadSnapshotAsync(
            fixture.Container, fixture.CatalogIdentity, repeated, maximumSelections: 1));
        Assert.Equal("ModelSelectionLimit", error.Code);
        fixture.Container.EnsureUnchanged();
    }

    private sealed class ModelArchive : IAsyncDisposable
    {
        private readonly ModelArchiveSource sourceFiles;
        private bool disposed;
        public string IndexPath => sourceFiles.IndexPath;
        public AssetMemoryBudget Budget { get; }
        public ArchiveSnapshot Snapshot { get; }
        public TextureCatalogSession Catalogs { get; }
        public NativeTextureSession Textures { get; }
        public NativeModelSession Models { get; }
        public NativeContainerLease Container { get; }
        public ResourceIdentity CatalogIdentity { get; }
        public ResourceIdentity[] MeshIdentities { get; }
        public NativeModelSelection[] Selections { get; }

        private ModelArchive(ModelArchiveSource sourceFiles, AssetMemoryBudget budget, ArchiveSnapshot snapshot,
            TextureCatalogSession catalogs, NativeTextureSession textures, NativeContainerLease container)
        {
            this.sourceFiles = sourceFiles;
            Budget = budget; Snapshot = snapshot; Catalogs = catalogs; Textures = textures; Container = container;
            Models = new(textures, budget);
            CatalogIdentity = new(snapshot.IndexContentId, 0);
            MeshIdentities = container.Directory.Objects.Where(item => item.NativeType == 2130)
                .Select(item => item.Identity).ToArray();
            Selections = [new(MeshIdentities[0], 0), new(MeshIdentities[0], 1), new(MeshIdentities[1], 1)];
        }

        public static async Task<ModelArchive> CreateAsync(AssetMemoryBudget? sharedBudget = null,
            bool outerCompressed = false, bool largeSparseUnusedSuffix = false)
        {
            var fixture = new ModelArchiveSource(outerCompressed, largeSparseUnusedSuffix);
            var budget = sharedBudget ?? new AssetMemoryBudget();
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var catalogs = new TextureCatalogSession(snapshot,
                new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
            var textures = new NativeTextureSession(catalogs);
            try
            {
                await catalogs.EnrichmentTask;
                var identity = new ResourceIdentity(snapshot.IndexContentId, 1);
                var container = largeSparseUnusedSuffix
                    ? await NativeContainerLease.OpenMetadataAsync(snapshot, identity, budget)
                    : await NativeContainerLease.OpenAsync(snapshot, identity, budget);
                return new(fixture, budget, snapshot, catalogs, textures, container);
            }
            catch
            {
                await textures.DisposeAsync();
                await catalogs.DisposeAsync();
                fixture.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            Container.Dispose();
            await Textures.DisposeAsync();
            await Catalogs.DisposeAsync();
            sourceFiles.Dispose();
        }
    }

    private sealed class ModelArchiveSource : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantNativeModelSnapshotSource", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");

        public ModelArchiveSource(bool outerCompressed, bool largeSparseUnusedSuffix)
        {
            Directory.CreateDirectory(root);
            var catalog = new byte[20];
            "COM_CONT"u8.CopyTo(catalog);
            Put(catalog, 8, 6); Put(catalog, 12, 4); Put(catalog, 16, 0);
            var container = CreateContainer(outerCompressed);
            long containerStoredSize = container.Length;
            if (largeSparseUnusedSuffix)
            {
                if (outerCompressed) throw new ArgumentException("The sparse-tail fixture uses direct QZIP metadata.", nameof(outerCompressed));
                const int largeContainerSize = 200 * 1024 * 1024;
                containerStoredSize = largeContainerSize;
                uint oldBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(5 + 52));
                uint addedTail = checked((uint)(containerStoredSize - container.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(5 + 52), checked(oldBodyLength + addedTail));
            }
            var volumePath = Path.ChangeExtension(IndexPath, ".dat");
            using (var volume = new FileStream(volumePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (largeSparseUnusedSuffix && OperatingSystem.IsWindows()) TryMarkSparse(volume.SafeFileHandle);
                volume.SetLength(checked(catalog.Length + containerStoredSize));
                volume.Write(catalog);
                volume.Write(container);
            }

            var index = new byte[IndexLayout.HeaderLength + 2 * IndexLayout.RecordLength];
            IndexLayout.Magic.CopyTo(index);
            WriteIndexWord(index, 20, IndexLayout.SupportedVersion); WriteIndexWord(index, 101, 2);
            WriteIndexRecord(0, 3, 1, 0, (uint)catalog.Length);
            WriteIndexRecord(1, 29, 50, (uint)catalog.Length, checked((uint)containerStoredSize));
            File.WriteAllBytes(IndexPath, index);

            void WriteIndexRecord(int ordinal, uint type, uint id, uint offset, uint length)
            {
                var at = IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength;
                WriteIndexWord(index, at, type); WriteIndexWord(index, at + 8, id);
                WriteIndexWord(index, at + 12, offset); WriteIndexWord(index, at + 16, length);
            }
        }

        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        private static void TryMarkSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            _ = DeviceIoControl(handle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device,
            uint controlCode, IntPtr input, uint inputLength, IntPtr output, uint outputLength,
            out uint bytesReturned, IntPtr overlapped);

        private static byte[] CreateContainer(bool outerCompressed)
        {
            var samples = new[] { MeshSample.Create(2), MeshSample.Create(2) };
            var packages = samples.Select(sample => WrapGpu(sample.Gpu)).ToArray();
            var body = new MemoryStream();
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = samples[index];
                var frame = new byte[52];
                Put(frame, 0, (uint)sample.Metadata.Length); frame[4] = 1; Put(frame, 5, 1); Put(frame, 9, (uint)index);
                Put(frame, 36, (uint)packages[index].Length); Put(frame, 40, (uint)sample.Gpu.Length);
                Put(frame, 44, (uint)(index == 0 ? 0 : packages[0].Length));
                body.Write(frame); body.Write(sample.Metadata);
            }
            int frameBytesLength = checked((int)body.Length);
            foreach (var package in packages) body.Write(package);
            // Keep a source-backed unused tail after the selected GPU packages. Metadata loading
            // should fetch only a selected package range, while the full-container path remains compatible.
            body.Write(new byte[32 * 1024]);

            using var logical = new MemoryStream();
            long logicalBodyOffset;
            using (var writer = new BinaryWriter(logical, Encoding.UTF8, true))
            {
                writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(24u); writer.Write(3u); writer.Write(2u);
                writer.Write(2130u); writer.Write(100u); writer.Write(2130u); writer.Write(101u);
                writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length);
                logicalBodyOffset = logical.Position;
                writer.Write(body.ToArray());
            }
            if (outerCompressed)
            {
                var logicalBytes = logical.ToArray();
                int prefixLength = checked((int)(logicalBodyOffset + frameBytesLength));
                var outerPackage = WrapGpu(logicalBytes.AsSpan(0, prefixLength).ToArray());
                using var compressedContainer = new MemoryStream(checked(outerPackage.Length +
                    checked((int)body.Length) - frameBytesLength));
                compressedContainer.Write(outerPackage);
                compressedContainer.Write(body.ToArray().AsSpan(frameBytesLength));
                return compressedContainer.ToArray();
            }
            using var output = new MemoryStream();
            output.Write("QZIP\0"u8); logical.Position = 0; logical.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] WrapGpu(byte[] decoded)
        {
            using var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(decoded);
            var compressed = packed.ToArray();
            var block = new byte[32 + compressed.Length]; "segs"u8.CopyTo(block);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(6), 1);
            Put(block, 8, (uint)decoded.Length); Put(block, 12, (uint)compressed.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(16), checked((ushort)compressed.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(18), checked((ushort)decoded.Length));
            Put(block, 20, 1); compressed.CopyTo(block, 32);
            return block;
        }

        private static void WriteIndexWord(byte[] bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

        private static void Put(byte[] bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private sealed record MeshSample(byte[] Metadata, byte[] Gpu)
    {
        public static MeshSample Create(int groupCount)
        {
            const int baseVertex = 2;
            var elements = baseVertex + 3;
            using var metadata = new MemoryStream(); using var writer = new BinaryWriter(metadata);
            writer.Write(3u); Tag(writer, "MESHDATA", 41);
            writer.Write(1u); writer.Write(5u); writer.Write((uint)elements); writer.Write((byte)0);
            writer.Write(1u); writer.Write(3u); writer.Write((uint)groupCount);
            for (var group = 0; group < groupCount; group++)
            {
                writer.Write(2u); writer.Write(0u); writer.Write(0u);
                for (var part = 0; part < 2; part++)
                {
                    writer.Write(0xaabbccddu); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write(0u); writer.Write((uint)baseVertex); writer.Write(3u);
                    writer.Write(0u); writer.Write(0u); writer.Write(3u); writer.Write(0u);
                    writer.Write(0u); writer.Write(2133u); writer.Write((uint)(30 + part));
                    writer.Write(0u); writer.Write(0u); Bounds(writer);
                }
                writer.Write((byte)0); writer.Write((byte)0); Bounds(writer);
            }
            writer.Write(0u); writer.Write(0u);
            writer.Write(Descriptor(0, 0, 2, 0)); writer.Write(Descriptor(1, 0, 13, 1));
            writer.Write(Descriptor(1, 4, 13, 10)); writer.Write(Descriptor(1, 8, 4, 2)); writer.Write(Descriptor(1, 12, 5, 4));
            writer.Write(12u); writer.Write(16u); writer.Write(0u); writer.Write(0u);
            writer.Write(0u); writer.Write(uint.MaxValue); writer.Write(0u); writer.Write(0u);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
            Tag(writer, "CLUPSKME", 2); writer.Write((byte)0); Tag(writer, "BLSHAPES", 3);
            for (var group = 0; group < groupCount; group++) writer.Write(0u);

            using var gpu = new MemoryStream(); using var gpuWriter = new BinaryWriter(gpu);
            gpuWriter.Write((ushort)0); gpuWriter.Write((ushort)1); gpuWriter.Write((ushort)2); gpuWriter.Write(new byte[10]);
            for (var index = 0; index < elements; index++)
            { gpuWriter.Write((float)index); gpuWriter.Write((float)index + 1); gpuWriter.Write((float)index + 2); }
            for (var index = 0; index < elements; index++)
            {
                gpuWriter.Write(511u); gpuWriter.Write(511u << 10 | 1u << 30);
                gpuWriter.Write(new byte[] { 255, 128, 0, 64 });
                gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).25));
                gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).5));
            }
            while (gpu.Length % 16 != 0) gpuWriter.Write((byte)0);
            return new(metadata.ToArray(), gpu.ToArray());
        }

        private static uint Descriptor(byte stream, byte offset, byte format, byte semantic) =>
            (uint)stream | (uint)offset << 8 | (uint)format << 16 | (uint)semantic << 24;
        private static void Tag(BinaryWriter writer, string tag, uint version)
        { writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(version); }
        private static void Bounds(BinaryWriter writer) { for (var index = 0; index < 19; index++) writer.Write(0f); }
    }
}
