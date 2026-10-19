using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

[Collection("Scene metadata diagnostics")]
public sealed class SceneRegionMetadataReuseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionDiscoveryOpensRepeatedCompanionMetadataOnce(bool compressed)
    {
        using var fixture = new SceneMetadataFixture(compressed: compressed);
        var snapshot = fixture.Snapshot;
        var budget = new AssetMemoryBudget(32 * 1024 * 1024);
        using var measurements = new MeasurementObserver();
        await using (var catalogs = new TextureCatalogSession(snapshot, fixture.Revision))
        await using (var textures = new NativeTextureSession(catalogs))
        await using (var renderer = new SceneRenderSession(snapshot, new NativeModelSession(textures, budget), budget))
        {
            var section = fixture.Section();
            Assert.NotEqual(section.Result.Value!.Hierarchy.Nodes[0].WorldTransform,
                section.Result.Value.Hierarchy.Nodes[1].WorldTransform);
            var result = await renderer.DescribeRegionsAsync(section);
            Assert.Empty(result.Issues);
            Assert.Equal(2, result.Regions.Count);
            Assert.All(result.Regions, region =>
            {
                Assert.Equal(1, region.OccurrenceCount);
                Assert.Equal(1, region.VisibleOccurrenceCount);
                var center = (region.Bounds.Minimum + region.Bounds.Maximum) * .5f;
                var previewCenter = (region.PreviewBounds.Minimum + region.PreviewBounds.Maximum) * .5f;
                Assert.InRange(Vector3.Distance(center, previewCenter), 0, 0.0001f);
                Assert.InRange(Vector3.Distance(new Vector3(20), region.PreviewBounds.Maximum - region.PreviewBounds.Minimum), 0, 0.0001f);
                Assert.True(Vector3.Min(region.PreviewBounds.Minimum, region.Bounds.Minimum) == region.PreviewBounds.Minimum);
                Assert.True(Vector3.Max(region.PreviewBounds.Maximum, region.Bounds.Maximum) == region.PreviewBounds.Maximum);
            });
            AssertBounds(result.Regions[0].Bounds, new(0, 0, 0), new(2, 4, 6));
            AssertBounds(result.Regions[1].Bounds, new(6, 0, 0), new(10, 2, 6));
            Assert.Equal(1, measurements.Count("SourceVerification"));
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    private static void AssertBounds(GeometryBounds actual, Vector3 minimum, Vector3 maximum)
    {
        Assert.InRange(Vector3.Distance(actual.Minimum, minimum), 0, 0.0001f);
        Assert.InRange(Vector3.Distance(actual.Maximum, maximum), 0, 0.0001f);
    }

    [Fact]
    public async Task CancellationBetweenBranchesReleasesDiscoveryOwnershipAndAllowsReopening()
    {
        using var fixture = new SceneMetadataFixture(compressed: true);
        var budget = new AssetMemoryBudget(32 * 1024 * 1024);
        await using (var catalogs = new TextureCatalogSession(fixture.Snapshot, fixture.Revision))
        await using (var textures = new NativeTextureSession(catalogs))
        await using (var renderer = new SceneRenderSession(fixture.Snapshot, new NativeModelSession(textures, budget), budget))
        {
            using var canceled = new CancellationTokenSource();
            var progress = new InlineProgress(item => { if (item.Stage == "Regions" && item.Completed == 1) canceled.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.DescribeRegionsAsync(fixture.Section(), progress, canceled.Token));
            var reopened = await renderer.DescribeRegionsAsync(fixture.Section());
            Assert.Empty(reopened.Issues); Assert.Equal(2, reopened.Regions.Count);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class InlineProgress(Action<RenderLoadProgress> callback) : IProgress<RenderLoadProgress>
    { public void Report(RenderLoadProgress value) => callback(value); }

    internal sealed class MeasurementObserver : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        private readonly List<AssetLoadMeasurement> measurements = [];
        public MeasurementObserver() => subscription = AssetLoadDiagnostics.Listener.Subscribe(this);
        public int Count(string stage) { lock (measurements) return measurements.Count(item => item.Stage == stage); }
        public int CountReads(string relativeSource)
        { lock (measurements) return measurements.Count(item => item.Stage == "VerificationRead" && item.RelativeSource == relativeSource); }
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadMeasurement measurement) lock (measurements) measurements.Add(measurement); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() => subscription.Dispose();
    }
}

[CollectionDefinition("Scene metadata diagnostics", DisableParallelization = true)]
public sealed class SceneMetadataDiagnosticsCollection;

internal sealed class SceneMetadataFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-scene-fixtures", Guid.NewGuid().ToString("N"));
    public ArchiveSnapshot Snapshot { get; }
    public SourceRevision Revision => new(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
    public ResourceIdentity Container => new(Snapshot.IndexContentId, 1);
    public SceneMetadataFixture(int companionCount = 1, bool compressed = false, bool malformed = false)
    {
        Directory.CreateDirectory(root);
        var index = Path.Combine(root, "generated.idx");
        var volume = Path.Combine(root, "generated.dat");
        var indexBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(index, indexBytes);
        using var table = new MemoryStream();
        using (var writer = new BinaryWriter(table, System.Text.Encoding.UTF8, true))
        {
            writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(13u); writer.Write(1u);
            writer.Write(29u); writer.Write(7u); writer.Write((byte)0);
        }
        var metadata = ContainerBytes();
        if (malformed) metadata[0] = 0;
        if (compressed) metadata = Compress(metadata.AsSpan(5));
        var rows = new List<ResourceRecord> { new(0, 3, 1, 6, 0, 0, table.Length, 0, "generated.dat") };
        using (var output = File.Create(volume))
        {
            output.Write(table.ToArray());
            for (int indexNumber = 0; indexNumber < companionCount; indexNumber++)
            {
                rows.Add(new(indexNumber + 1, 29, 1, (uint)(7 + indexNumber), 0, output.Position,
                    metadata.Length, 0, "generated.dat"));
                output.Write(metadata);
            }
        }
        var provider = new FileRevisionProvider();
        var source = new VolumeSource(0, "generated.dat", volume, provider.Capture(volume));
        Snapshot = new(index, Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant(), 18, 0, [], (uint)rows.Count,
            rows,
            [source], provider.Capture(index), SourceIdentity.PhysicalStamp([source]), []);
    }

    public SceneLoadedSection Section()
    {
        var container = new ResourceIdentity(Snapshot.IndexContentId, 0);
        var nodes = new List<SceneHierarchyNode>();
        var entities = new List<SceneEntityRecord>();
        var instances = new List<SceneCatalogInstance>();
        var byNode = new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>>();
        var relationships = new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>();
        for (int index = 0; index < 2; index++)
        {
            var world = Matrix4x4.CreateRotationZ(index * MathF.PI / 2) * Matrix4x4.CreateTranslation(index * 10, 0, 0);
            var frame = SceneFixture.Frame(index, (uint)(11 + index), world);
            frame = frame with { Identity = new(Snapshot.IndexContentId, 0, frame.Identity.MemberPath),
                DirectoryEntryExtent = frame.DirectoryEntryExtent with { Container = container },
                HeaderExtent = frame.HeaderExtent with { Container = container },
                PayloadExtent = frame.PayloadExtent with { Container = container } };
            var record = new NodeRecordReader().Read(frame, Revision, []).Value!;
            nodes.Add(new(record, null, null, null, null, [], SceneTransformMath.ToSource(world),
                SceneTransformMath.ToSource(world), SceneTransformStatus.Available));
            var identity = new ResourceIdentity(Snapshot.IndexContentId, 0, $"instance/{index}");
            var extent = new DecodedExtent(container, 0, 1);
            var entity = new SceneEntityRecord(identity, 2207, (uint)index, null, [], 0, 0, 4, 0, extent,
                new Dictionary<string, DecodedExtent>());
            entities.Add(entity);
            instances.Add(new(identity, entity, $"placement {index}", "generated catalog",
                new(IdentifierNamespace.Archive, "6", identity, 3), new Dictionary<string, DecodedExtent>()));
            byNode[frame.Identity] = [entity];
            relationships[frame.Identity] = [new(frame.Identity, AssetRelationshipKind.Container, "Catalog",
                new(IdentifierNamespace.Archive, "6", frame.Identity, 3), new(ReferenceStatus.Unique, [container]),
                new(true, "generated", "generated fixture"))];
        }
        var sectionIdentity = container with { MemberPath = "scene-section" };
        var hierarchy = new SceneHierarchy(container, Revision, nodes, nodes.Select(item => item.Identity).ToArray(), []);
        var document = new SceneSectionDocument(sectionIdentity, container, Revision, hierarchy, entities, instances,
            new(sectionIdentity, 17, new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty), [], []);
        return new(new(sectionIdentity, Revision, "scene", "Generated scene", null, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview, [], []),
            new(document, sectionIdentity, Revision, [], [], [], [], new Dictionary<string, string>()),
            new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(), byNode, relationships);
    }

    private static byte[] ContainerBytes()
    {
        var node = SceneFixture.Frame(0, 11, Matrix4x4.CreateTranslation(1, 2, 3)).RawPayload.ToArray();
        var frames = new (uint Type, uint Id, byte[] Bytes)[] { (2002, 11, node), (2009, 21, MeshEntity()), (2130, 31, MeshData()) };
        using var body = new MemoryStream(); using (var bodyWriter = new BinaryWriter(body, System.Text.Encoding.UTF8, true))
        foreach (var frame in frames)
        {
            bodyWriter.Write((uint)frame.Bytes.Length); bodyWriter.Write((byte)0);
            bodyWriter.Write(0u); bodyWriter.Write(0u); bodyWriter.Write(frame.Bytes);
        }
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("QZIP\0DC_INFO "u8); writer.Write(3u); writer.Write((uint)(8 + frames.Length * 8));
        writer.Write(0u); writer.Write((uint)frames.Length);
        foreach (var frame in frames) { writer.Write(frame.Type); writer.Write(frame.Id); }
        writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length); writer.Write(body.ToArray());
        return stream.ToArray();
    }

    private static byte[] MeshEntity()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(4u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u);
        writer.Write("ENTITY  "u8); writer.Write(7u); writer.Write(2002u); writer.Write(11u); writer.Write(new byte[29]);
        writer.Write("MESH____"u8); writer.Write(29u); writer.Write(1u);
        writer.Write(2130u); writer.Write(31u); writer.Write(0f); writer.Write(new byte[24]);
        writer.Write(1u); writer.Write(0u);
        return stream.ToArray();
    }

    private static byte[] MeshData()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(3u); writer.Write("MESHDATA"u8); writer.Write(41u);
        writer.Write(1u); writer.Write(0u); writer.Write(3u); writer.Write((byte)0);
        writer.Write(1u); writer.Write(3u); writer.Write(1u);
        writer.Write(1u); writer.Write(0u); writer.Write(0u);
        writer.Write(0u); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(0u); writer.Write(0u); writer.Write(3u); writer.Write(0u); writer.Write(0u); writer.Write(3u);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        Bounds(writer); writer.Write((byte)0); writer.Write((byte)0); Bounds(writer);
        writer.Write(0u); writer.Write(0u);
        writer.Write(12u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
        writer.Write("CLUPSKME"u8); writer.Write(2u); writer.Write((byte)0);
        writer.Write("BLSHAPES"u8); writer.Write(3u); writer.Write(0u);
        return stream.ToArray();
    }

    private static void Bounds(BinaryWriter writer)
    {
        foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 2, 3, 0, 0, 0, 0 }) writer.Write(value);
    }

    private static byte[] Compress(ReadOnlySpan<byte> decoded)
    {
        using var packed = new MemoryStream();
        using (var compressor = new ZLibStream(packed, CompressionLevel.SmallestSize, true)) compressor.Write(decoded);
        var compressed = packed.ToArray();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("segs"u8); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((uint)decoded.Length); writer.Write((uint)compressed.Length);
        writer.Write((ushort)compressed.Length); writer.Write((ushort)decoded.Length); writer.Write(1u);
        writer.Write(new byte[8]); writer.Write(compressed);
        return stream.ToArray();
    }
    public void Dispose() => Directory.Delete(root, true);
}
