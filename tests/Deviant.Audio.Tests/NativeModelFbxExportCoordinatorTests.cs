using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeModelFbxExportCoordinatorTests
{
    [Fact]
    public async Task AnimationWithoutSourceSkeletonFailsBeforeMaterialPreparation()
    {
        var root = NewRoot(); var revision = Revision(); var mesh = Mesh(revision, 1, 0, "generated");
        var budget = new AssetMemoryBudget(); using var snapshot = Snapshot(revision, budget, mesh); bool called = false;
        try
        {
            var error = await Assert.ThrowsAsync<NativeModelFbxExportException>(() => Coordinator().ExportAsync(snapshot, revision,
                Selection(mesh, 0), (_, _, _) => { called = true; throw new InvalidOperationException(); }, _ => Task.CompletedTask,
                Path.Combine(root, "source"), Path.Combine(root, "model.fbx"),
                animationInputProvider: (_, _, _) => throw new InvalidOperationException()));
            Assert.Equal("AnimationSkeletonRequired", error.Code); Assert.False(called); Assert.Equal(0, budget.UsedBytes);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ExportsOnlyTheExactMeshKeyIdentityGroupAndLod()
    {
        var root = NewRoot();
        var revision = Revision();
        var first = Mesh(revision, 1, 0, "unselected");
        var selected = Mesh(revision, 2, 3, "selected");
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(revision, budget, first, selected);
        var materialCalls = 0;
        var output = Path.Combine(root, "selected.fbx");
        Directory.CreateDirectory(Path.Combine(root, "source"));

        try
        {
            var report = await Coordinator().ExportAsync(snapshot, revision,
                Selection(selected, lod: 5),
                (_, sharedBudget, _) =>
                {
                    Interlocked.Increment(ref materialCalls);
                    return Task.FromResult(new NativeModelFbxMaterialInputs(sharedBudget, []));
                }, _ => Task.CompletedTask,
                Path.Combine(root, "source"), output);

            Assert.Equal(selected.Identity, report.Identity);
            Assert.Equal(selected.Key, report.Selection.Key);
            Assert.Equal(5, report.Selection.LodIndex);
            Assert.Equal(3, report.Selection.GroupIndex);
            Assert.Equal(1, materialCalls);
            Assert.Contains("selected", await File.ReadAllTextAsync(output));
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task HoldsSnapshotAndMaterialLeasesUntilWriterValidationCompletes()
    {
        var root = NewRoot();
        var revision = Revision();
        var mesh = Mesh(revision, 4, 1, "borrowed");
        var budget = new AssetMemoryBudget();
        var snapshotResource = new TrackedResource();
        var materialResource = new TrackedResource();
        var snapshot = Snapshot(revision, budget, [mesh], [snapshotResource]);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredFinalValidation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalValidation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        Func<CancellationToken, Task> validate = async _ =>
        {
            if (Interlocked.Increment(ref validations) != 4) return;
            Assert.Single(Directory.GetFiles(root, ".borrowed.fbx.*.tmp"));
            Assert.Single(Directory.GetDirectories(root, ".borrowed.fbx.media-stage-*"));
            enteredFinalValidation.TrySetResult(true);
            await releaseFinalValidation.Task;
            throw new ArchiveException("SourceChanged", "Generated source changed during export.");
        };
        Directory.CreateDirectory(Path.Combine(root, "source"));

        try
        {
            var pending = Coordinator().ExportAsync(snapshot, revision, Selection(mesh, lod: 2),
                async (_, sharedBudget, token) =>
                {
                    entered.TrySetResult(true);
                    await releasePreparation.Task.WaitAsync(token);
                    return new NativeModelFbxMaterialInputs(sharedBudget, [], [materialResource]);
                }, validate,
                Path.Combine(root, "source"), Path.Combine(root, "borrowed.fbx"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            snapshot.Dispose();
            Assert.Equal(0, snapshotResource.DisposeCount);
            Assert.Equal(0, materialResource.DisposeCount);

            releasePreparation.TrySetResult(true);
            await enteredFinalValidation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, snapshotResource.DisposeCount);
            Assert.Equal(0, materialResource.DisposeCount);
            Assert.True(budget.UsedBytes > 0);

            releaseFinalValidation.TrySetResult(true);
            var error = await Assert.ThrowsAsync<ArchiveException>(() => pending);
            Assert.Equal("SourceChanged", error.Code);
            Assert.Equal(1, snapshotResource.DisposeCount);
            Assert.Equal(1, materialResource.DisposeCount);
            Assert.Equal(0, budget.UsedBytes);
            Assert.Empty(Directory.GetFiles(root, ".borrowed.fbx.*.tmp"));
            Assert.Empty(Directory.GetDirectories(root, ".borrowed.fbx.media-stage-*"));
        }
        finally
        {
            releasePreparation.TrySetResult(true);
            releaseFinalValidation.TrySetResult(true);
            snapshot.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsMaterialInputsFromAnotherBudgetAndDisposesTheirRetention()
    {
        var root = NewRoot();
        var revision = Revision();
        var mesh = Mesh(revision, 9, 0, "wrong-budget");
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(revision, budget, mesh);
        var retained = new TrackedResource();
        Directory.CreateDirectory(Path.Combine(root, "source"));
        try
        {
            var error = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, Selection(mesh, lod: 0),
                    (_, _, _) => Task.FromResult(new NativeModelFbxMaterialInputs(new AssetMemoryBudget(), [], [retained])),
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "wrong-budget.fbx")));
            Assert.Equal("MaterialBudgetMismatch", error.Code);
            Assert.Equal(1, retained.DisposeCount);
            Assert.Equal(0, budget.UsedBytes);
            Assert.False(File.Exists(Path.Combine(root, "wrong-budget.fbx")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task UnsupportedBasisAndMissingExactSelectionAreRejectedBeforeMaterialResolution()
    {
        var root = NewRoot();
        var revision = Revision();
        var valid = Mesh(revision, 5, 0, "valid");
        var unverified = valid with
        {
            Identity = Identity(revision, 6),
            Geometry = valid.Geometry with
            {
                Coordinates = Profile() with { Evidence = new RelationshipEvidence(false, "generated", "unverified basis") }
            }
        };
        var skinned = valid with
        {
            Identity = Identity(revision, 10),
            Geometry = valid.Geometry with
            {
                Primitives =
                [
                    valid.Geometry.Primitives[0] with
                    {
                        Skin = new SkinBinding(Identity(revision, 99),
                            [new PaletteJoint(0, new ResourceReference(IdentifierNamespace.Native, "generated-joint"))],
                            [[new VertexInfluence(0, 1)], [new VertexInfluence(0, 1)], [new VertexInfluence(0, 1)]])
                    }
                ]
            }
        };
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(revision, budget, valid, unverified, skinned);
        var materialCalls = 0;
        Directory.CreateDirectory(Path.Combine(root, "source"));
        Func<RenderMesh, AssetMemoryBudget, CancellationToken, Task<NativeModelFbxMaterialInputs>> materialProvider =
            (_, sharedBudget, _) =>
            {
                Interlocked.Increment(ref materialCalls);
                return Task.FromResult(new NativeModelFbxMaterialInputs(sharedBudget, []));
            };

        try
        {
            var staleRevision = revision with { PhysicalSourceStamp = "generated-stale-revision" };
            var staleError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, staleRevision, Selection(valid, lod: 0), materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "stale.fbx")));
            Assert.Equal("SourceRevisionMismatch", staleError.Code);

            var unverifiedError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, Selection(unverified, lod: 0), materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "unverified.fbx")));
            Assert.Equal("CoordinateProfileUnverified", unverifiedError.Code);

            var skinError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, Selection(skinned, lod: 0), materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "skinned.fbx")));
            Assert.Equal("SkinExportUnsupported", skinError.Code);

            var missing = Selection(valid, lod: 0) with { GeometryKey = "missing-generated-geometry" };
            var missingError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, missing, materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "missing.fbx")));
            Assert.Equal("GeometrySelectionMissing", missingError.Code);

            var wrongGroup = Selection(valid, lod: 0) with
            {
                GroupIndex = 1,
                WriterSelection = Selection(valid, lod: 0).WriterSelection with { GroupIndex = 1 }
            };
            var groupError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, wrongGroup, materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "wrong-group.fbx")));
            Assert.Equal("GeometrySelectionMismatch", groupError.Code);

            var wrongIdentity = Selection(valid, lod: 0) with
            {
                Identity = Identity(revision, 15),
                WriterSelection = Selection(valid, lod: 0).WriterSelection with { Identity = Identity(revision, 15) }
            };
            var identityError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, wrongIdentity, materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "wrong-identity.fbx")));
            Assert.Equal("GeometrySelectionMismatch", identityError.Code);

            var wrongLod = Selection(valid, lod: 1) with
            {
                WriterSelection = Selection(valid, lod: 0).WriterSelection
            };
            var lodError = await Assert.ThrowsAsync<NativeModelFbxExportException>(() =>
                Coordinator().ExportAsync(snapshot, revision, wrongLod, materialProvider,
                    _ => Task.CompletedTask, Path.Combine(root, "source"), Path.Combine(root, "wrong-lod.fbx")));
            Assert.Equal("WriterSelectionMismatch", lodError.Code);
            Assert.Equal(0, materialCalls);
            Assert.False(File.Exists(Path.Combine(root, "stale.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "unverified.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "skinned.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "missing.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "wrong-group.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "wrong-identity.fbx")));
            Assert.False(File.Exists(Path.Combine(root, "wrong-lod.fbx")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task SourceChangeBeforeCommitPreservesExistingOutputAndReleasesSharedBudget()
    {
        var root = NewRoot();
        var revision = Revision();
        var mesh = Mesh(revision, 7, 2, "source-change");
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(revision, budget, mesh);
        var validations = 0;
        var sourceRoot = Path.Combine(root, "source");
        var output = Path.Combine(root, "existing.fbx");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(output, "preserve existing output");
        Func<CancellationToken, Task> validate = _ =>
        {
            if (Interlocked.Increment(ref validations) == 4)
            {
                Assert.True(budget.UsedBytes > 0);
                Assert.Single(Directory.GetFiles(root, ".existing.fbx.*.tmp"));
                Assert.Single(Directory.GetDirectories(root, ".existing.fbx.media-stage-*"));
                throw new ArchiveException("SourceChanged", "Generated source revision changed before commit.");
            }
            return Task.CompletedTask;
        };

        try
        {
            var error = await Assert.ThrowsAsync<ArchiveException>(() => Coordinator().ExportAsync(
                snapshot, revision, Selection(mesh, lod: 1), EmptyMaterials, validate,
                sourceRoot, output, new(Overwrite: true)));
            Assert.Equal("SourceChanged", error.Code);
            Assert.Equal("preserve existing output", await File.ReadAllTextAsync(output));
            Assert.Equal(4, validations);
            Assert.Equal(0, budget.UsedBytes);
            Assert.Empty(Directory.GetFiles(root, ".existing.fbx.*.tmp"));
            Assert.Empty(Directory.GetDirectories(root, ".existing.fbx.media-stage-*"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CancellationDuringFinalSourceValidationReleasesSharedBudget()
    {
        var root = NewRoot();
        var revision = Revision();
        var mesh = Mesh(revision, 8, 0, "cancelled");
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(revision, budget, mesh);
        using var cancellation = new CancellationTokenSource();
        var materialResource = new TrackedResource();
        var validations = 0;
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        Func<CancellationToken, Task> validate = token =>
        {
            if (Interlocked.Increment(ref validations) == 4)
            {
                Assert.True(budget.UsedBytes > 0);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator().ExportAsync(
                snapshot, revision, Selection(mesh, lod: 0),
                (_, sharedBudget, _) => Task.FromResult(new NativeModelFbxMaterialInputs(sharedBudget, [], [materialResource])), validate,
                sourceRoot, Path.Combine(root, "cancelled.fbx"), cancellationToken: cancellation.Token));
            Assert.Equal(1, materialResource.DisposeCount);
            Assert.Equal(0, budget.UsedBytes);
            Assert.False(File.Exists(Path.Combine(root, "cancelled.fbx")));
        }
        finally { DeleteRoot(root); }
    }

    private static NativeModelFbxExportCoordinator Coordinator() => new();

    private static Task<NativeModelFbxMaterialInputs> EmptyMaterials(RenderMesh _, AssetMemoryBudget budget,
        CancellationToken __) => Task.FromResult(new NativeModelFbxMaterialInputs(budget, []));

    private static NativeModelFbxExportSelection Selection(RenderMesh mesh, int lod) =>
        new(mesh.Key, mesh.Identity, mesh.GroupIndex, lod,
            new(mesh.Identity, mesh.Key, lod, mesh.GroupIndex));

    private static RenderAssetSnapshot Snapshot(SourceRevision revision, AssetMemoryBudget budget,
        params RenderMesh[] meshes) => Snapshot(revision, budget, (IEnumerable<RenderMesh>)meshes, []);

    private static RenderAssetSnapshot Snapshot(SourceRevision revision, AssetMemoryBudget budget,
        IEnumerable<RenderMesh> meshes, IEnumerable<IDisposable> resources) =>
        new(revision, meshes.ToDictionary(mesh => mesh.Key, StringComparer.Ordinal), [],
            new Dictionary<ResourceIdentity, RenderMaterial>(), new(Vector3.Zero, Vector3.One), [], [],
            new(0, 0, 0, 0, 1, 0), [], budget, resources);

    private static RenderMesh Mesh(SourceRevision revision, long ordinal, int group, string primitiveKey) =>
        new(Identity(revision, ordinal), group, Geometry(primitiveKey));

    private static ResourceIdentity Identity(SourceRevision revision, long ordinal) =>
        new(revision.IndexContentId, ordinal);

    private static SourceRevision Revision() => new("generated-index", "generated-source-revision");

    private static GeometryAsset Geometry(string primitiveKey) => new(Profile(), [],
        [new(primitiveKey, PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt32, [])],
        new Dictionary<string, string>());

    private static CoordinateProfile Profile() => new("generated-coordinate-basis", "1",
        new(CoordinateAxis.PositiveX, CoordinateAxis.PositiveY, CoordinateAxis.PositiveZ),
        CoordinateHandedness.Right, 1, MatrixLayout.RowMajor, VectorMultiplicationSide.Row,
        Evidence: new RelationshipEvidence(true, "generated-test", "synthetic right-handed unit basis"));

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "deviant-model-fbx-coordinator-" + Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string root) { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class TrackedResource : IDisposable
    {
        public int DisposeCount => Volatile.Read(ref disposed);
        private int disposed;
        public void Dispose() => Interlocked.Increment(ref disposed);
    }
}
