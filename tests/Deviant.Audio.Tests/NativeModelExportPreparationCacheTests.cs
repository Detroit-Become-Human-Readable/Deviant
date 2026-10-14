using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeModelExportPreparationCacheTests
{
    [Fact]
    public void HighlySharedGraphsHashInBoundedMemoryAndKeepSemanticChangesDistinct()
    {
        using var fixture = new Fixture();
        var textures = new Dictionary<int, MaterialBakeTexture>();
        string Key(MaterialExpression expression) => NativeModelExportPreparationCache.MaterialKey(
            fixture.Material with { SurfaceGraph = fixture.Material.SurfaceGraph! with { BaseColor = expression } },
            fixture.Primitive, ["generated-source"], textures, 2, 2, 4096);
        // Forty-nine unique nodes represent a tree with over 2^48 leaves if sharing is expanded.
        var shared = SharedGraph(48, .25f); var equivalent = SharedGraph(48, .25f);
        _ = Key(shared); // Exclude first-use hashing/encoding setup from the allocation observation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        string first = Key(shared);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 2 * 1024 * 1024);
        Assert.Equal(first, Key(equivalent));
        Assert.NotEqual(first, Key(SharedGraph(48, .5f)));
    }

    [Fact]
    public void GraphKeyCancellationIsCheckedBeforeMetadataAndDuringNodeTraversal()
    {
        using var fixture = new Fixture(); var textures = new Dictionary<int, MaterialBakeTexture>();
        using var alreadyCanceled = new CancellationTokenSource(); alreadyCanceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeModelExportPreparationCache.MaterialKey(fixture.Material,
            fixture.Primitive, ["generated-source"], textures, 2, 2, 4096, alreadyCanceled.Token));
        using var duringTraversal = new CancellationTokenSource();
        var child = SharedGraph(1, .25f);
        var root = new MaterialExpression(MaterialExpressionOperation.Add, MaterialValueType.Vector3,
            new CancelingArguments(child, duringTraversal));
        var material = fixture.Material with { SurfaceGraph = fixture.Material.SurfaceGraph! with { BaseColor = root } };
        Assert.Throws<OperationCanceledException>(() => NativeModelExportPreparationCache.MaterialKey(material,
            fixture.Primitive, ["generated-source"], textures, 2, 2, 4096, duringTraversal.Token));
    }

    [Fact]
    public void GraphKeyRejectsCyclesAndExcessiveDepthWithoutRecursiveJsonExpansion()
    {
        using var fixture = new Fixture(); var textures = new Dictionary<int, MaterialBakeTexture>();
        var arguments = new List<MaterialExpression>();
        var cycle = new MaterialExpression(MaterialExpressionOperation.Add, MaterialValueType.Vector3, arguments);
        arguments.Add(cycle); arguments.Add(cycle);
        string Key(MaterialExpression root) => NativeModelExportPreparationCache.MaterialKey(fixture.Material with
            { SurfaceGraph = fixture.Material.SurfaceGraph! with { BaseColor = root } }, fixture.Primitive, [], textures, 2, 2, 4096);
        Assert.Throws<InvalidDataException>(() => Key(cycle));
        Assert.Throws<InvalidDataException>(() => Key(SharedGraph(64, .25f)));
    }

    [Fact]
    public async Task WarmPlannedTextureVerifiesPersistedExtentsWithoutReadingProducerPayloads()
    {
        using var fixture = new Fixture(); var generated = await CreateSourcePlanAsync(fixture);
        using var sources = ArchiveSourceSession.Acquire(generated.Snapshot);
        int reads = 0; int validations = 0;
        Task<(Deviant.Formats.Textures.DdsTextureDocument, Deviant.Formats.Textures.TextureSurface, NativeTextureExportSourceEvidence)> Produce(CancellationToken token)
        { token.ThrowIfCancellationRequested(); reads++; return Task.FromResult((generated.Map.Document, generated.Map.Surface, generated.Evidence)); }
        Task<SourceExtentVerificationLease> Verify(NativeTextureExportSourceEvidence evidence, CancellationToken token)
        {
            validations++;
            Assert.Equal(generated.Evidence.Payloads[0].Extent.PayloadSha256, Assert.Single(evidence.Payloads).Extent.PayloadSha256);
            return new SourceExtentVerifier().AcquireAsync(generated.Snapshot,
                evidence.Plan.MetadataSources.Concat(evidence.Payloads.Select(payload => payload.Extent)).ToArray(), token);
        }
        var cold = await fixture.Cache.AcquirePlannedTextureAsync(generated.Plan, null, Produce, Verify, Validate, fixture.Budget);
        await cold.Lease.DisposeAsync();
        var warm = await fixture.Cache.AcquirePlannedTextureAsync(generated.Plan, null,
            _ => throw new InvalidOperationException("Warm preparation must not load native payloads."), Verify, Validate, fixture.Budget);
        Assert.Equal(1, reads); Assert.Equal(2, validations);
        Assert.Equal(cold.Surface.Rgba8.ToArray(), warm.Surface.Rgba8.ToArray());
        Assert.Equal(cold.Document.ContentSha256, warm.Document.ContentSha256);
        await warm.Lease.DisposeAsync(); Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
    }

    [Fact]
    public async Task PlannedSourceMismatchRejectsWarmContentBeforeProducerAndExtentChangesChangeKey()
    {
        using var fixture = new Fixture(); var generated = await CreateSourcePlanAsync(fixture);
        using var sources = ArchiveSourceSession.Acquire(generated.Snapshot);
        Task<SourceExtentVerificationLease> Verify(NativeTextureExportSourceEvidence evidence, CancellationToken token) =>
            new SourceExtentVerifier().AcquireAsync(generated.Snapshot,
                evidence.Plan.MetadataSources.Concat(evidence.Payloads.Select(payload => payload.Extent)).ToArray(), token);
        var cold = await fixture.Cache.AcquirePlannedTextureAsync(generated.Plan, null,
            _ => Task.FromResult((generated.Map.Document, generated.Map.Surface, generated.Evidence)), Verify, Validate, fixture.Budget);
        await cold.Lease.DisposeAsync();
        var error = await Assert.ThrowsAsync<ArchiveException>(() => fixture.Cache.AcquirePlannedTextureAsync(generated.Plan, null,
            _ => throw new InvalidOperationException("Changed sources must be rejected before payload hydration."),
            (evidence, token) => new SourceExtentVerifier().AcquireAsync(generated.Snapshot,
                [evidence.Payloads[0].Extent with { PayloadSha256 = new string('0', 64) }], token), Validate, fixture.Budget));
        Assert.Equal("SourceChanged", error.Code); Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
        var changedPayload = generated.Plan.Payloads[0] with { Extent = generated.Plan.Payloads[0].Extent with { Offset = 53 } };
        Assert.NotEqual(NativeModelExportPreparationCache.PlannedTextureKey(generated.Plan),
            NativeModelExportPreparationCache.PlannedTextureKey(generated.Plan with { Payloads = [changedPayload] }));
        Assert.NotEqual(NativeModelExportPreparationCache.PlannedTextureKey(generated.Plan),
            NativeModelExportPreparationCache.PlannedTextureKey(generated.Plan with { CatalogSha256 = new string('1', 64) }));
    }

    [Fact]
    public async Task WarmOriginalPixelsAvoidProducerAndCorruptArtifactsRebuild()
    {
        using var fixture = new Fixture(); int productions = 0;
        Task<(Deviant.Formats.Textures.DdsTextureDocument, Deviant.Formats.Textures.TextureSurface)> Produce(CancellationToken token)
        { token.ThrowIfCancellationRequested(); productions++; return Task.FromResult((fixture.Map.Document, fixture.Map.Surface)); }
        var cold = await fixture.Cache.AcquireTextureAsync("generated-texture", Produce, Validate, fixture.Budget);
        await cold.Lease.DisposeAsync();
        var warm = await fixture.Cache.AcquireTextureAsync("generated-texture", Produce, Validate, fixture.Budget);
        Assert.Equal(cold.Document.ContentSha256, warm.Document.ContentSha256);
        Assert.Equal(cold.Surface.Rgba8.ToArray(), warm.Surface.Rgba8.ToArray());
        Assert.Equal(1, productions); await warm.Lease.DisposeAsync();
        string path = Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.data", SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var recovered = await fixture.Cache.AcquireTextureAsync("generated-texture", Produce, Validate, fixture.Budget);
        Assert.Equal(2, productions); Assert.Equal(cold.Surface.Rgba8.ToArray(), recovered.Surface.Rgba8.ToArray());
        await recovered.Lease.DisposeAsync(); Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
    }

    [Fact]
    public async Task WinningCoordinatesAndPngRestoreWithoutRasterOrAtlasProducer()
    {
        using var fixture = new Fixture(); int productions = 0;
        var expected = await fixture.StageAsync();
        var corners = new[] { new Vector2(.125f, .25f), new Vector2(.75f, .25f), new Vector2(.125f, .875f) };
        expected = expected with { TextureCoordinateSet = 1, PolygonCornerCoordinates = corners };
        Task<IReadOnlyList<FbxStagedMaterialMapInput>> Produce() { productions++; return Task.FromResult<IReadOnlyList<FbxStagedMaterialMapInput>>([expected]); }
        var cold = await fixture.Cache.AcquireMapsAsync("generated-map", Produce, Validate, fixture.Budget,
            diagnostics: () => ["Atlas faces separated to preserve distinct surface samples."]);
        Assert.False(cold.CacheHit); await cold.Lease.DisposeAsync();
        File.Delete(expected.Path);
        var warm = await fixture.Cache.AcquireMapsAsync("generated-map", Produce, Validate, fixture.Budget);
        Assert.True(warm.CacheHit); Assert.Equal(1, productions);
        var restored = Assert.Single(warm.Maps);
        Assert.Equal(1, restored.TextureCoordinateSet); Assert.Equal(corners, restored.PolygonCornerCoordinates);
        Assert.Single(warm.Diagnostics);
        await using (var input = await restored.OpenPreparedContent!(default))
        {
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes);
            Assert.Equal(expected.Length, bytes.Length);
            Assert.Equal(expected.PngSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
        await warm.Lease.DisposeAsync(); Assert.Equal(0, fixture.Budget.UsedBytes);
        Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public async Task OutOfDomainAtlasMapsRestoreAndExportWithSourceUvsPreserved(float offset)
    {
        using var fixture = new Fixture();
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var u = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [uv], Components: "x");
        var expression = new MaterialExpression(MaterialExpressionOperation.Subtract, MaterialValueType.Scalar,
            [u, new(MaterialExpressionOperation.Constant, MaterialValueType.Scalar, [], [offset])]);
        var material = fixture.Material with { SurfaceGraph = new(expression, null, null, [MaterialInput.Uv0], [], []) };
        var reference = new ResourceReference(IdentifierNamespace.Archive, "material", ResourceType: 11);
        var primitive = fixture.Primitive with
        {
            TextureCoordinates = [[new(offset, 0), new(offset + 1, 0), new(offset, 1)]],
            Submeshes = [new("surface-material", 0, 3, [reference])]
        };
        var textures = new Dictionary<int, MaterialBakeTexture>();
        Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive,
            textures, 2, 2, fixture.Budget.Reserve));
        Vector2[] corners = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, fixture.Budget.Reserve,
            textureCoordinateSet: 1, polygonCornerCoordinates: corners);
        Assert.Equal(new byte[] { 137, 137, 137, 255 }, map.Surface.Rgba8.Span[..4].ToArray());
        var expected = await fixture.StageAsync(map);
        var key = NativeModelExportPreparationCache.MaterialKey(material, primitive, [], textures, 2, 2, 4096);
        int productions = 0;
        Task<IReadOnlyList<FbxStagedMaterialMapInput>> Produce()
        { productions++; return Task.FromResult<IReadOnlyList<FbxStagedMaterialMapInput>>([expected]); }
        var cold = await fixture.Cache.AcquireMapsAsync(key, Produce, Validate, fixture.Budget);
        Assert.False(cold.CacheHit);
        await ExportAsync(Assert.Single(cold.Maps), "cold");
        await cold.Lease.DisposeAsync();
        File.Delete(expected.Path);
        var warm = await fixture.Cache.AcquireMapsAsync(key, Produce, Validate, fixture.Budget);
        Assert.True(warm.CacheHit);
        var restored = Assert.Single(warm.Maps);
        Assert.Equal(corners, restored.PolygonCornerCoordinates);
        Assert.Equal(expected.PngSha256, restored.PngSha256);
        await ExportAsync(restored, "warm");
        await warm.Lease.DisposeAsync();
        Assert.Equal(1, productions);
        Assert.Equal(0, fixture.Budget.UsedBytes);
        Assert.Equal(0, fixture.Manager.ActivePreviewLeases);

        async Task ExportAsync(FbxStagedMaterialMapInput staged, string name)
        {
            var identity = new ResourceIdentity(material.Identity.IndexContentId, 99);
            var geometry = new GeometryAsset(new("source", "1", MatrixLayout: MatrixLayout.RowMajor,
                VectorSide: VectorMultiplicationSide.Row), [], [primitive], new Dictionary<string, string>());
            var decoded = new DecodeResult<GeometryAsset>(geometry, identity, material.Revision, [], [], [], [], new Dictionary<string, string>());
            var inputMaterial = new FbxMaterialExportInput(material,
                [new(reference, new(ReferenceStatus.Unique, [material.Identity]))], [], Validate) { StagedMaps = [staged] };
            var path = Path.Combine(fixture.Root, name + ".fbx");
            await new FbxGeometryExporter().ExportAsync(new(decoded, new(identity, "detail"), fixture.Budget.Reserve,
                [inputMaterial], CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers), fixture.SourceRoot, path);
            var text = await File.ReadAllTextAsync(path);
            var sourceCoordinates = string.Join(",", primitive.TextureCoordinates![0].SelectMany(point => new[] { point.X, point.Y })
                .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Matches(@"UV: \*6 \{ a:\s*" + System.Text.RegularExpressions.Regex.Escape(sourceCoordinates), text);
            Assert.Matches(@"UV: \*6 \{ a:\s*0,0,1,0,0,1", text);
            Assert.Contains("ByPolygonVertex", text);
            Assert.Contains("P: \"UVSet\",\"KString\",\"\",\"\",\"UVSet1\"", text);
        }
    }

    [Fact]
    public async Task PreparedDirectMediaPublishesIdenticalPngAndRejectsChangedSource()
    {
        using var fixture = new Fixture(); var directory = Path.Combine(fixture.Root, "output");
        var cold = await fixture.Cache.PublishTextureMediaAsync("generated-texture", fixture.Map.Document, fixture.Map.Surface,
            fixture.SourceRoot, directory, "cold.png", Validate, fixture.Budget);
        var warm = await fixture.Cache.PublishTextureMediaAsync("generated-texture", fixture.Map.Document, fixture.Map.Surface,
            fixture.SourceRoot, directory, "warm.png", Validate, fixture.Budget);
        Assert.Equal(cold.Sha256, warm.Sha256);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(directory, "cold.png")), await File.ReadAllBytesAsync(Path.Combine(directory, "warm.png")));
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.encoded", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<ArchiveException>(() => fixture.Cache.PublishTextureMediaAsync("generated-texture", fixture.Map.Document,
            fixture.Map.Surface, fixture.SourceRoot, directory, "changed.png", _ => throw new ArchiveException("SourceChanged", "Changed source."), fixture.Budget));
        Assert.False(File.Exists(Path.Combine(directory, "changed.png"))); Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
    }

    [Fact]
    public async Task SourceGraphAndUvChangesInvalidateDecisionAndCancellationLeavesNoLeases()
    {
        using var fixture = new Fixture(); var textures = new Dictionary<int, MaterialBakeTexture>();
        string Key(MaterialAsset material, MeshPrimitive primitive, string source) =>
            NativeModelExportPreparationCache.MaterialKey(material, primitive, [source], textures, 2, 2, 4096);
        string original = Key(fixture.Material, fixture.Primitive, "source-one");
        Assert.NotEqual(original, Key(fixture.Material, fixture.Primitive, "source-two"));
        Assert.NotEqual(original, Key(fixture.Material, fixture.Primitive with { TextureCoordinates = [[Vector2.Zero, Vector2.UnitY, Vector2.UnitX]] }, "source-one"));
        Assert.NotEqual(original, Key(fixture.Material with { SurfaceGraph = fixture.Material.SurfaceGraph! with
            { BaseColor = new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [1f, 0f, 0f]) } }, fixture.Primitive, "source-one"));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.AcquireMapsAsync(original,
            () => { cancellation.Cancel(); return Task.FromResult<IReadOnlyList<FbxStagedMaterialMapInput>>([]); },
            Validate, fixture.Budget, cancellation.Token));
        Assert.Equal(0, fixture.Manager.ActivePreviewLeases); Assert.Equal(0, fixture.Budget.UsedBytes);
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.maps", SearchOption.AllDirectories));
    }

    private static Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private static MaterialExpression SharedGraph(int depth, float value)
    {
        var current = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [value, value, value]);
        for (int index = 0; index < depth; index++) current = new(MaterialExpressionOperation.Add, MaterialValueType.Vector3, [current, current]);
        return current;
    }

    private sealed class CancelingArguments(MaterialExpression child, CancellationTokenSource cancellation) : IReadOnlyList<MaterialExpression>
    {
        public int Count => 2;
        public MaterialExpression this[int index] => index is 0 or 1 ? child : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<MaterialExpression> GetEnumerator() { cancellation.Cancel(); yield return child; yield return child; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task<(ArchiveSnapshot Snapshot, NativeTextureExportFingerprint Plan,
        NativeTextureExportSourceEvidence Evidence, MaterialSurfaceMap Map)> CreateSourcePlanAsync(Fixture fixture)
    {
        byte[] index = [1, 2, 3, 4]; byte[] volume = Enumerable.Range(0, 116).Select(value => (byte)value).ToArray();
        string indexPath = Path.Combine(fixture.SourceRoot, "index.idx"), volumePath = Path.Combine(fixture.SourceRoot, "archive.dat");
        await File.WriteAllBytesAsync(indexPath, index); await File.WriteAllBytesAsync(volumePath, volume);
        string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var stamps = new FileRevisionProvider(); string indexId = Hash(index);
        var snapshot = new ArchiveSnapshot(indexPath, indexId, 18, 0, [], 3,
            [new(0, 29, 0, 10, 0, 0, 32, 0, "archive.dat"), new(1, 3, 0, 11, 0, 32, 20, 0, "archive.dat"),
                new(2, 2229, 0, 12, 0, 52, 64, 0, "archive.dat")],
            [new(0, "archive.dat", volumePath, stamps.Capture(volumePath))], stamps.Capture(indexPath), "generated-revision", []);
        var revision = new SourceRevision(indexId, snapshot.PhysicalSourceStamp);
        var metadata = new ResourceIdentity(indexId, 0, "texture-metadata");
        SourceExtent Extent(int offset, int length) => new("archive.dat", offset, length, Hash(volume.AsSpan(offset, length).ToArray()));
        var payload = new NativeTextureExportPayloadPlan(new(indexId, 2), 2229, 12, new("archive.dat", 52, 64));
        var plan = new NativeTextureExportFingerprint(new(indexId, 0, "texture"), payload.Identity, revision,
            new(indexId, 1), Hash(volume.AsSpan(32, 20).ToArray()), metadata, Hash(volume.AsSpan(0, 32).ToArray()),
            [Extent(0, 32), Extent(32, 20)], [payload]);
        var evidence = new NativeTextureExportSourceEvidence(plan, [payload with { Extent = Extent(52, 64) }]);
        var map = MaterialSurfaceMapBaker.Bake(fixture.Material with { Identity = metadata, Revision = revision }, fixture.Primitive,
            new Dictionary<int, MaterialBakeTexture>(), 2, 2, fixture.Budget.Reserve);
        return (snapshot, plan, evidence, map);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Deviant-test-" + Guid.NewGuid().ToString("N"));
        public string CacheRoot => Path.Combine(Root, "cache");
        public string SourceRoot => Path.Combine(Root, "source");
        public CacheManager Manager { get; }
        public NativeModelExportPreparationCache Cache { get; }
        public AssetMemoryBudget Budget { get; } = new();
        public MaterialAsset Material { get; }
        public MeshPrimitive Primitive { get; }
        public MaterialSurfaceMap Map { get; }
        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Manager = new(Path.Combine(Root, "settings"), new(CacheRoot, 4 * 1024 * 1024)); Cache = new(Manager);
            var identity = new ResourceIdentity("generated-export", 7); var revision = new SourceRevision(identity.IndexContentId, "revision");
            Material = new(identity, revision, "generated", [], [], []) { SurfaceGraph = new(
                new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.25f, .5f, .75f]), null, null, [], [], []) };
            Primitive = new("surface", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [0, 1, 2], IndexElementFormat.UInt16, [], TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
            Map = MaterialSurfaceMapBaker.Bake(Material, Primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Budget.Reserve);
        }
        public async Task<FbxStagedMaterialMapInput> StageAsync(MaterialSurfaceMap? map = null)
        {
            map ??= Map;
            string directory = Path.Combine(Root, "maps");
            var encoded = await new TextureExportService().ExportAsync(map.Document, map.Surface, AssetExportFormat.Png,
                SourceRoot, directory, "map.png", expectedRevision: map.Document.Revision, preCommitValidation: Validate);
            return new(MaterialTextureRole.BaseColor, map.TextureCoordinateSet, Material.Identity, Material.Revision, map.Document.ContentSha256,
                map.Surface.Width, map.Surface.Height, map.Surface.IsSrgb, Path.Combine(directory, encoded.RelativePath),
                encoded.Length, encoded.Sha256, map.GraphSha256, [Primitive.Key], [], Validate)
                { PolygonCornerCoordinates = map.PolygonCornerCoordinates };
        }
        public void Dispose() { Manager.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
