using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class SceneTextureDetailPolicyTests
{
    [Theory]
    [InlineData(SceneRenderScope.Scene)]
    [InlineData(SceneRenderScope.Branch)]
    [InlineData(SceneRenderScope.Region)]
    public void RequestedDetailSelectsTheSameSourceMipForEveryScope(SceneRenderScope scope)
    {
        var (source, expectedMip) = NativeMipFixture.CreateLargeInlinePackage();
        var request = Request(source.Revision, scope, 1024);
        var dimension = MaterialDimension(request);
        var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, dimension);
        var decoded = new NativeTextureDecoder().ReadPreview(source, dimension);

        Assert.Equal(1024, decoded.Document.Info.Width);
        Assert.Equal(1024, decoded.Document.Info.Height);
        Assert.Equal(2, plan.MipIndex);
        Assert.Equal(expectedMip, decoded.Document.OriginalBytes.Span[148..].ToArray());
        // The old scene-only cap selected a different source mip, not merely a
        // smaller presentation of the same prepared image.
        Assert.NotEqual(NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, 256).MipIndex, plan.MipIndex);

        var overridePlan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] },
            MaterialDimension(request with { MaximumPreviewDimension = 512 }));
        Assert.Equal(512, overridePlan.Width);
        Assert.Equal(3, overridePlan.MipIndex);
    }

    [Fact]
    public async Task OldLowDetailPreparedSceneCannotSatisfyTheSameRequestedDetail()
    {
        using var fixture = new GeneratedArchive();
        var archive = await new ArchiveReader().OpenAsync(fixture.Index);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var request = Request(revision, SceneRenderScope.Scene, 1024);
        var selection = JsonSerializer.Serialize(new
        {
            Section = request.Section.Descriptor.Identity.Key,
            Branch = request.BranchInstance.Key,
            request.Scope, request.Region, request.MaximumPreviewDimension
        }, new JsonSerializerOptions { IncludeFields = true });
        var legacyKey = CacheManager.PreparedKey("scenes", "prepared-scene-v1/" + NativeTextureDecoder.PreviewVersion,
            revision.IndexContentId + "/" + revision.PhysicalSourceStamp, selection);
        var currentKey = PreparedKey(request, revision);
        var otherDetailKey = PreparedKey(request with { MaximumPreviewDimension = 256 }, revision);

        Assert.NotEqual(legacyKey, currentKey);
        Assert.NotEqual(otherDetailKey, currentKey);

        var textureId = new ResourceIdentity(archive.IndexContentId, 0, "generated/texture");
        var materialId = textureId with { MemberPath = "generated/material" };
        var pixels = new byte[256 * 256 * 4];
        Array.Fill(pixels, (byte)127);
        var digest = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var texture = new RenderTexture(textureId, TextureSurface.RestoreRgba8(256, 256, pixels,
            false, true, 4, TextureStorageFormat.Rgba8, "RGBA8", digest, new(MipIndex: 4), textureId, revision), digest);
        var material = new RenderMaterial(new(materialId, revision, "generated", [], [], []),
            new Dictionary<int, RenderTexture> { [0] = texture });
        var budget = new AssetMemoryBudget();
        using var scene = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial> { [materialId] = material }, request.Region,
            [], [], new(0, 0, 0, 0, 1, 1), [], budget);
        var path = Path.Combine(fixture.Root, "legacy.scene");
        await PreparedSceneCodec.WriteAsync(path, legacyKey, scene);

        Assert.True(await PreparedSceneCodec.ValidateAsync(path, legacyKey, revision));
        Assert.False(await PreparedSceneCodec.ValidateAsync(path, currentKey, revision));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void DetailOutsideTheSupportedRangeIsRejected(int dimension)
    {
        var request = Request(new("generated-index", "generated-revision"), SceneRenderScope.Scene, dimension);
        var error = Assert.Throws<TargetInvocationException>(() => MaterialDimension(request));
        Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
    }

    [Fact]
    public void GraphAccountingGrowsOnDemandAndTransfersWithoutDuplicatingAdmission()
    {
        var budget = new AssetMemoryBudget(100);
        using var graph = new GraphAccounting(budget, 100);
        Assert.Equal(0, budget.UsedBytes);
        graph.Grow(20);
        using (graph.Scratch(5))
        {
            graph.Grow(10);
            Assert.Equal(35, budget.UsedBytes);
            Assert.Equal(30, graph.Bytes);
        }
        Assert.Equal(30, budget.UsedBytes);
        using var retained = graph.Transfer();
        Assert.Equal(35, budget.PeakBytes);
        graph.Dispose();
        Assert.Equal(30, budget.UsedBytes);
        retained.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void GraphAndParserScratchShareTheOriginalGraphLimit()
    {
        var budget = new AssetMemoryBudget(1000);
        using var graph = new GraphAccounting(budget, 100);
        graph.Grow(30);
        using (graph.Scratch(50))
        {
            var error = Assert.Throws<TargetInvocationException>(() => graph.Grow(21));
            Assert.Equal("SceneGraphLimit", Assert.IsType<ArchiveException>(error.InnerException).Code);
            Assert.Equal(80, budget.UsedBytes);
            Assert.Equal(30, graph.Bytes);
            Assert.Throws<TargetInvocationException>(() => graph.Transfer());
        }
        graph.Grow(70);
        Assert.Equal(100, budget.UsedBytes);
        graph.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void FailedCpuAdmissionDoesNotAdvanceGraphAccounting()
    {
        var budget = new AssetMemoryBudget(50);
        using var graph = new GraphAccounting(budget, 100);
        graph.Grow(20);
        using (budget.Reserve(25, "generated competing work"))
        {
            var error = Assert.Throws<TargetInvocationException>(() => graph.Grow(10));
            Assert.Equal("AssetMemoryLimit", Assert.IsType<ArchiveException>(error.InnerException).Code);
            Assert.Equal(20, graph.Bytes);
            Assert.Equal(45, budget.UsedBytes);
        }
        graph.Grow(10);
        Assert.Equal(30, graph.Bytes);
        graph.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void AbandoningGraphWorkDrainsRetainedAndActiveScratchReservations()
    {
        var budget = new AssetMemoryBudget(100);
        using var graph = new GraphAccounting(budget, 100);
        graph.Grow(20);
        using var scratch = graph.Scratch(30);
        Assert.Equal(50, budget.UsedBytes);
        graph.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        scratch.Dispose();
        graph.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class GraphAccounting : IDisposable
    {
        private readonly object graph;
        private readonly Type type = typeof(SceneRenderSession).GetNestedType("SceneGraphMemory", BindingFlags.NonPublic)!;

        public GraphAccounting(AssetMemoryBudget budget, long limit) => graph = Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [budget, limit], null)!;

        public long Bytes => (long)type.GetProperty("Bytes")!.GetValue(graph)!;
        public void Grow(long bytes) => type.GetMethod("Grow")!.Invoke(graph, [bytes]);
        public IDisposable Scratch(long bytes) => (IDisposable)type.GetMethod("ReserveScratch")!.Invoke(graph, [bytes])!;
        public IDisposable Transfer() => (IDisposable)type.GetMethod("Transfer")!.Invoke(graph, null)!;
        public void Dispose() => ((IDisposable)graph).Dispose();
    }

    private static SceneRenderRequest Request(SourceRevision revision, SceneRenderScope scope, int dimension)
    {
        var identity = new ResourceIdentity(revision.IndexContentId, 0, "generated/section");
        var descriptor = new AssetDescriptor(identity, revision, "scene", "Generated scene", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []);
        // Detail planning and cache identity use section metadata without hydrating its graph.
        var section = new SceneLoadedSection(descriptor, null!,
            new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>());
        return new(section, identity with { MemberPath = "generated/branch" },
            new(Vector3.Zero, Vector3.One), scope) { MaximumPreviewDimension = dimension };
    }

    private static int MaterialDimension(SceneRenderRequest request) =>
        (int)typeof(SceneRenderSession).GetMethod("MaterialPreviewDimension", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request])!;

    private static string PreparedKey(SceneRenderRequest request, SourceRevision revision) =>
        (string)typeof(SceneRenderSession).GetMethod("CreatePreparedKey", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request, revision])!;

    private sealed class GeneratedArchive : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantTests", Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(Root, "sample.idx");

        public GeneratedArchive()
        {
            Directory.CreateDirectory(Root);
            var bytes = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength];
            IndexLayout.Magic.CopyTo(bytes);
            void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
            Put(20, 18); Put(101, 1); Put(105, 29); Put(113, 9); Put(121, 4);
            File.WriteAllBytes(Index, bytes);
            File.WriteAllBytes(Path.Combine(Root, IndexLayout.VolumeName("sample", 0)), [1, 2, 3, 4]);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
