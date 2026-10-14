using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeModelSceneRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeometryBatchReleasesMetadataBuffersButRetainsDecodedGeometryAndSourceReceipts(bool compressed)
    {
        await using var fixture = await GeneratedModel.CreateAsync(compressed);
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var metadataExtent = Assert.Single(metadata.SourceExtents);
        var batch = await GeometryAsync(fixture, metadata);
        try
        {
            var sourceExtents = batch.SourceExtents.ToArray();
            metadata.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = metadata.Directory);
            Assert.Contains(metadataExtent, sourceExtents);
            Assert.Contains(sourceExtents, extent => extent.Offset > metadataExtent.Offset && extent.Length > 0);
            Assert.All(batch.Meshes.Values, mesh =>
            {
                Assert.Null(mesh.SkinSourceMetadata); Assert.Null(mesh.SkinSourceGeometry); Assert.Null(mesh.Pose);
                Assert.Equal(3, mesh.Geometry.Primitives[0].Positions.Count);
                Assert.Equal(new System.Numerics.Vector3(2, 3, 4), mesh.Geometry.Primitives[0].Positions[0]);
                Assert.Equal(new uint[] { 0, 1, 2 }, mesh.Geometry.Primitives[0].Indices);
            });
            Assert.NotEmpty(Receipts(batch));
            foreach (var receipt in Receipts(batch)) receipt.EnsureUnchanged();
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("container metadata source"));
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("native object metadata"));
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("native metadata directory frames"));
            Assert.True(fixture.Budget.CategoryUsage["decoded geometry"] > 0);
        }
        finally { batch.Dispose(); }
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task MissingMaterialBatchOwnsItsReceiptAfterBothGeometryOwnerAndContainerOwnerClose()
    {
        await using var fixture = await GeneratedModel.CreateAsync();
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var geometry = await GeometryAsync(fixture, metadata);
        var mesh = Assert.Single(geometry.Meshes.Values);
        Assert.Empty(mesh.MaterialsByPrimitive!);
        var missingMaterial = new ResourceIdentity(mesh.Identity.IndexContentId, mesh.Identity.Ordinal, "generated/missing-material");
        Assert.DoesNotContain(metadata.Directory.Objects, frame => frame.Identity == missingMaterial);
        // The mesh fixture has no shader frames. An explicit missing binding exercises neutral-material preparation.
        Assert.IsAssignableFrom<IDictionary<string, RenderMesh>>(geometry.Meshes)[mesh.Key] = mesh with
        {
            MaterialsByPrimitive = new Dictionary<string, ResourceIdentity>
            { [mesh.Geometry.Primitives[0].Key] = missingMaterial }
        };
        var materials = await MaterialsAsync(fixture, metadata, geometry);
        try
        {
            var consumed = metadata.SourceExtents.ToArray();
            geometry.Dispose(); metadata.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = metadata.Directory);
            Assert.NotEmpty(materials.Materials);
            var material = Assert.Single(materials.Materials).Value;
            Assert.Equal(missingMaterial, material.Asset.Identity);
            Assert.Equal("neutral", material.Asset.Profile);
            Assert.Contains("MaterialUnavailable", material.Asset.Diagnostics);
            Assert.Empty(material.TexturesBySlot);
            Assert.NotEmpty(Receipts(materials));
            Assert.All(consumed, extent => Assert.Contains(extent, materials.SourceExtents));
            foreach (var receipt in Receipts(materials)) receipt.EnsureUnchanged();
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("native object metadata"));
            Assert.Equal(new uint[] { 0, 1, 2 }, Assert.Single(materials.Meshes.Values).Geometry.Primitives[0].Indices);
        }
        finally { materials.Dispose(); geometry.Dispose(); }
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task SnapshotRetainsSourcePinsWithoutRetainingContainerMetadataAndReleasesThemOnClose()
    {
        await using var fixture = await GeneratedModel.CreateAsync();
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var geometry = await GeometryAsync(fixture, metadata);
        using var snapshot = new RenderAssetSnapshot(fixture.Models.Revision, geometry.Meshes, [], geometry.Materials,
            new(System.Numerics.Vector3.Zero, System.Numerics.Vector3.One), geometry.Issues, [], new(0, 0, 0, 0, 1, 0),
            geometry.SourceExtents, fixture.Budget, [geometry.AcquireLease()]);
        metadata.Dispose(); geometry.Dispose(); await fixture.CloseOtherReadersAsync();
        Assert.True(fixture.Budget.UsedBytes > 0);
        var volume = fixture.Snapshot.Volumes.Single().FilePath;
        var blocked = false;
        try
        {
            using var writer = new FileStream(volume, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            writer.Position = geometry.SourceExtents[0].Offset; writer.WriteByte(0xff); writer.Flush();
            File.SetLastWriteTimeUtc(volume, DateTime.UtcNow.AddSeconds(4));
        }
        catch (IOException) { blocked = true; }
        if (OperatingSystem.IsWindows()) Assert.True(blocked);
        if (!blocked)
        {
            var error = Assert.Throws<ArchiveException>(() => Receipts(geometry).Single().EnsureUnchanged());
            Assert.Equal("SourceChanged", error.Code);
        }
        snapshot.Dispose();
        Assert.Equal(0, fixture.Budget.UsedBytes);
        using var writable = new FileStream(volume, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullModelAndRetainedSkinPathsKeepTheirContainerOwnership(bool retainSkinSource)
    {
        await using var fixture = await GeneratedModel.CreateAsync();
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var batch = await fixture.Models.LoadAsync(metadata, fixture.CatalogIdentity, [fixture.Selections[0]],
            retainSkinSource: retainSkinSource);
        metadata.Dispose();
        Assert.NotEmpty(metadata.Directory.Objects);
        Assert.True(fixture.Budget.CategoryUsage["native object metadata"] > 0);
        Assert.Equal(retainSkinSource, Assert.Single(batch.Meshes.Values).SkinSourceMetadata is not null);
        batch.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = metadata.Directory);
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task CancelledGeometryPublicationDoesNotLeaveIndependentReceiptsOrDecodedBuffers()
    {
        await using var fixture = await GeneratedModel.CreateAsync();
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var baseline = fixture.Budget.UsedBytes;
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.Stage == "Geometry") cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeometryAsync(fixture, metadata, progress, cancellation.Token));

        Assert.Equal(baseline, fixture.Budget.UsedBytes);
        Assert.False(fixture.Budget.CategoryUsage.ContainsKey("decoded geometry"));
        Assert.False(fixture.Budget.CategoryUsage.ContainsKey("model source receipts"));
        metadata.EnsureUnchanged();
    }

    [Fact]
    public async Task ReceiptAdmissionFailureReleasesDecodedBuffersAndKeepsTheCallerContainerValid()
    {
        await using var fixture = await GeneratedModel.CreateAsync();
        fixture.Container.Dispose();
        using var metadata = await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container.Identity, fixture.Budget);
        var baseline = fixture.Budget.UsedBytes;
        AssetMemoryBudget.Reservation? guard = null;
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == "Geometry") guard = fixture.Budget.Reserve(
                fixture.Budget.MaximumBytes - fixture.Budget.UsedBytes - 1024, "generated receipt admission guard");
        });
        try
        {
            var error = await Assert.ThrowsAsync<ArchiveException>(() => GeometryAsync(fixture, metadata, progress));
            Assert.Equal("AssetMemoryLimit", error.Code);
            Assert.NotNull(guard);
            Assert.Equal(baseline + guard!.Bytes, fixture.Budget.UsedBytes);
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("decoded geometry"));
            Assert.False(fixture.Budget.CategoryUsage.ContainsKey("model source receipts"));
            metadata.EnsureUnchanged();
        }
        finally { guard?.Dispose(); }
        Assert.Equal(baseline, fixture.Budget.UsedBytes);
    }

    private static Task<NativeModelBatch> GeometryAsync(GeneratedModel fixture, NativeContainerLease metadata,
        IProgress<RenderLoadProgress>? progress = null, CancellationToken token = default) =>
        (Task<NativeModelBatch>)typeof(NativeModelSession).GetMethod("LoadGeometryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Models, [metadata, fixture.CatalogIdentity, new[] { fixture.Selections[0] }, progress, token, 1024, NativeModelSession.MaximumSelections])!;

    private static Task<NativeModelBatch> MaterialsAsync(GeneratedModel fixture, NativeContainerLease metadata, NativeModelBatch geometry) =>
        (Task<NativeModelBatch>)typeof(NativeModelSession).GetMethod("LoadMaterialsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Models, [metadata, fixture.CatalogIdentity, geometry, null, CancellationToken.None, 1024])!;

    private static IReadOnlyList<SourceExtentVerificationLease> Receipts(NativeModelBatch batch) =>
        ((IEnumerable<IDisposable>)typeof(NativeModelBatch).GetField("resources", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(batch)!).OfType<SourceExtentVerificationLease>().ToArray();

    private sealed class InlineProgress(Action<RenderLoadProgress> report) : IProgress<RenderLoadProgress>
    { public void Report(RenderLoadProgress value) => report(value); }

    private sealed class GeneratedModel(object fixture) : IAsyncDisposable
    {
        public AssetMemoryBudget Budget => Property<AssetMemoryBudget>("Budget");
        public ArchiveSnapshot Snapshot => Property<ArchiveSnapshot>("Snapshot");
        public NativeModelSession Models => Property<NativeModelSession>("Models");
        public NativeContainerLease Container => Property<NativeContainerLease>("Container");
        public ResourceIdentity CatalogIdentity => Property<ResourceIdentity>("CatalogIdentity");
        public NativeModelSelection[] Selections => Property<NativeModelSelection[]>("Selections");
        private T Property<T>(string name) => (T)fixture.GetType().GetProperty(name)!.GetValue(fixture)!;
        public static async Task<GeneratedModel> CreateAsync(bool compressed = false)
        {
            var type = typeof(NativeModelSnapshotTests).GetNestedType("ModelArchive", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [null, compressed, false])!;
            await task;
            return new(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }
        public async Task CloseOtherReadersAsync()
        {
            await Property<NativeTextureSession>("Textures").DisposeAsync();
            await Property<TextureCatalogSession>("Catalogs").DisposeAsync();
        }
        public ValueTask DisposeAsync() => ((IAsyncDisposable)fixture).DisposeAsync();
    }
}
