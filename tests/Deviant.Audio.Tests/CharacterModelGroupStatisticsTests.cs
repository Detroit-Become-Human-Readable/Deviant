using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterModelGroupStatisticsTests
{
    [Fact]
    public async Task GeneratedMeshGroupRetainsRawCountsSelectorsAndBoundsAcrossWarmCache()
    {
        using var generated = await GeneratedFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget, store);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);

            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                var discovered = Assert.Single(cold.Models);
                AssertDeferredStatistics(discovered);
                using var selected = await session.HaveModelEntryForSelectionAsync(discovered.Descriptor.Identity, provider.Revision);
                var model = selected.Entry;
                Assert.Equal(CharacterModelStatisticsAvailability.Available, model.SourceSummary.StatisticsAvailability);
                var stats = Assert.Single(model.SourceSummary.Lods).GroupStatistics;
                Assert.NotNull(stats);
                Assert.NotNull(Assert.Single(Assert.Single(model.Bindings).Lods).GroupStatistics);
                Assert.Equal(6, stats!.SummedVertexWindowCount);
                Assert.Equal(6, stats.SummedIndexWindowCount);
                Assert.Equal(2, stats.PartCount);
                Assert.Equal(new uint[] { 9 }, stats.VertexBufferElementCounts);
                Assert.Equal(new uint[] { 6 }, stats.IndexBufferElementCounts);
                Assert.Equal(new Dictionary<uint, long> { [5] = 1, [7] = 1 }, stats.RawSelectorHistogram);
                Assert.Equal(15, stats.Bounds.Obb.Count);
                Assert.Equal(stats.BoundsExtent, stats.Bounds.Extent);
            }

            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, warm.OwnersOpened);
                var discovered = Assert.Single(warm.Models);
                AssertDeferredStatistics(discovered);
                using var selected = await session.HaveModelEntryForSelectionAsync(discovered.Descriptor.Identity, provider.Revision);
                var model = selected.Entry;
                var stats = Assert.Single(model.SourceSummary.Lods).GroupStatistics;
                Assert.NotNull(stats);
                Assert.Equal(6, stats!.SummedVertexWindowCount);
                Assert.Equal(6, stats.SummedIndexWindowCount);
                Assert.Equal(2, stats.PartCount);
                Assert.Equal(new uint[] { 9 }, stats.VertexBufferElementCounts);
                Assert.Equal(new uint[] { 6 }, stats.IndexBufferElementCounts);
                Assert.Equal(new Dictionary<uint, long> { [5] = 1, [7] = 1 }, stats.RawSelectorHistogram);
                Assert.Equal(15, stats.Bounds.Obb.Count);
                Assert.Equal(stats.BoundsExtent, stats.Bounds.Extent);
                Assert.NotNull(Assert.Single(Assert.Single(model.Bindings).Lods).GroupStatistics);
                using var cached = session.GetModelEntry(generated.ModelIdentity, provider.Revision);
                Assert.NotNull(cached);
                var cachedStats = Assert.Single(cached!.Entry.SourceSummary.Lods).GroupStatistics;
                Assert.NotNull(cachedStats);
                Assert.Equal(stats.RawSelectorHistogram, cachedStats!.RawSelectorHistogram);
                Assert.Equal(stats.Bounds.Obb, cachedStats.Bounds.Obb);
                Assert.Equal(stats.Bounds.Extra, cachedStats.Bounds.Extra);
                Assert.Equal(stats.Bounds.Extent, cachedStats.Bounds.Extent);
            }
        }
        finally { DeleteCacheRoot(cacheRoot); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(true, 0, "NativeMeshClassCountUnsupported")]
    [InlineData(false, 2, "CharacterMeshGroupUnavailable")]
    public async Task InvalidOrUnavailableGroupHasNoStatistics(bool malformedMesh, uint groupIndex, string expectedDiagnostic)
    {
        using var generated = await GeneratedFixture.CreateAsync(malformedMesh, groupIndex);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget);
        await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget);

        using var result = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity);

        var lod = Assert.Single(Assert.Single(result.Models).SourceSummary.Lods);
        Assert.Null(lod.GroupStatistics);
        Assert.Null(lod.Selection);
        Assert.Equal(expectedDiagnostic, lod.Diagnostic);
    }

    [Fact]
    public async Task LegacyEntryLookupKeepsMetadataUntilSelectionReprocessesCurrentProfile()
    {
        using var generated = await GeneratedFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget, store);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            CharacterCatalogModelEntry seeded;
            CharacterCatalogModelEntry names;
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                names = Assert.Single(cold.Models);
                AssertDeferredStatistics(names);
                using var selected = await session.HaveModelEntryForSelectionAsync(names.Descriptor.Identity, provider.Revision);
                seeded = selected.Entry;
                Assert.NotNull(Assert.Single(seeded.SourceSummary.Lods).GroupStatistics);
            }

            var legacySource = seeded.SourceSummary with
            {
                StatisticsAvailability = CharacterModelStatisticsAvailability.UnavailableInLegacyCache,
                Lods = Array.AsReadOnly(seeded.SourceSummary.Lods.Select(lod => lod with { GroupStatistics = null }).ToArray())
            };
            var legacyBindings = Array.AsReadOnly(seeded.Bindings.Select(binding => binding with
            {
                Lods = Array.AsReadOnly(binding.Lods.Select(lod => lod with { GroupStatistics = null }).ToArray())
            }).ToArray());
            var legacyEntry = seeded with { SourceSummary = legacySource, Bindings = legacyBindings };
            var legacyVersion = "character-model-entry-v2-meshdata41-clupskme2-blshapes3-binding2/labels-512-4096-4096-262144";
            store.SaveBounded(ModelEntryKey(generated, seeded.Descriptor.Identity), legacyVersion,
                legacyEntry, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));

            using (var details = session.GetModelEntry(generated.ModelIdentity, provider.Revision))
            {
                Assert.NotNull(details);
                Assert.Equal(CharacterModelStatisticsAvailability.UnavailableInLegacyCache,
                    details!.Entry.SourceSummary.StatisticsAvailability);
                Assert.Null(Assert.Single(details.Entry.SourceSummary.Lods).GroupStatistics);
                Assert.Equal(seeded.Bindings.Count, details.Entry.Bindings.Count);
            }

            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, warm.OwnersOpened);
                var model = Assert.Single(warm.Models);
                AssertDeferredStatistics(model);
                Assert.Equal(seeded.Descriptor.Label, model.Descriptor.Label);
                Assert.Equal(seeded.Bindings.Count, model.Bindings.Count);
                Assert.Equal(seeded.CatalogLinks.Count, model.CatalogLinks.Count);
                Assert.Equal(names.SourceSummary.Lods.Select(lod => lod.Diagnostic),
                    model.SourceSummary.Lods.Select(lod => lod.Diagnostic));
            }
            Assert.NotNull(store.LoadBounded<CharacterCatalogModelEntry>(ModelEntryKey(generated, seeded.Descriptor.Identity),
                legacyVersion, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes)));

            using (var selected = await session.HaveModelEntryForSelectionAsync(generated.ModelIdentity, provider.Revision))
            {
                var model = selected.Entry;
                Assert.Equal(seeded.Descriptor.Label, model.Descriptor.Label);
                Assert.Equal(seeded.Bindings.Count, model.Bindings.Count);
                Assert.Equal(seeded.CatalogLinks.Count, model.CatalogLinks.Count);
                Assert.Equal(seeded.SourceSummary.Lods.Select(lod => lod.Diagnostic), model.SourceSummary.Lods.Select(lod => lod.Diagnostic));
                Assert.Equal(CharacterModelStatisticsAvailability.Available,
                    model.SourceSummary.StatisticsAvailability);
                Assert.NotNull(Assert.Single(model.SourceSummary.Lods).GroupStatistics);
                var statistics = Assert.Single(model.SourceSummary.Lods).GroupStatistics!;
                var expected = Assert.Single(seeded.SourceSummary.Lods).GroupStatistics!;
                Assert.Equal(expected.SummedVertexWindowCount, statistics.SummedVertexWindowCount);
                Assert.Equal(expected.SummedIndexWindowCount, statistics.SummedIndexWindowCount);
                Assert.Equal(expected.PartCount, statistics.PartCount);
                Assert.Equal(expected.VertexBufferElementCounts, statistics.VertexBufferElementCounts);
                Assert.Equal(expected.IndexBufferElementCounts, statistics.IndexBufferElementCounts);
                Assert.Equal(expected.RawSelectorHistogram, statistics.RawSelectorHistogram);
                Assert.Equal(expected.Bounds.Obb, statistics.Bounds.Obb);
                Assert.Equal(expected.Bounds.Extra, statistics.Bounds.Extra);
                Assert.Equal(expected.Bounds.Extent, statistics.Bounds.Extent);
                Assert.Equal(ModelCatalogClassification.Profile, model.Descriptor.Metadata["model.classification-profile"]);
            }

            Assert.Null(store.LoadBounded<CharacterCatalogModelEntry>(
                ModelEntryKey(generated, seeded.Descriptor.Identity), legacyVersion,
                checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes)));
        }
        finally { DeleteCacheRoot(cacheRoot); }
        Assert.Equal(0, budget.UsedBytes);
    }

    private static void AssertDeferredStatistics(CharacterCatalogModelEntry entry)
    {
        Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection, entry.SourceSummary.StatisticsAvailability);
        Assert.All(entry.SourceSummary.Lods, lod => Assert.Null(lod.GroupStatistics));
        Assert.All(entry.Bindings.SelectMany(binding => binding.Lods), lod => Assert.Null(lod.GroupStatistics));
    }

    private static string ModelEntryKey(GeneratedFixture generated, ResourceIdentity identity) =>
        $"{CharacterCatalogSession.ModelEntryCollection}/{generated.Snapshot.IndexContentId}/{generated.Snapshot.PhysicalSourceStamp}/item/{identity.Key}";

    private static string NewCacheRoot() => Path.Combine(Path.GetTempPath(), "deviant-model-statistics-" + Guid.NewGuid().ToString("N"));

    private static void DeleteCacheRoot(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class GeneratedFixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private GeneratedFixture(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot,
            ResourceIdentity catalogIdentity, ResourceIdentity modelIdentity)
        { this.builder = builder; Snapshot = snapshot; CatalogIdentity = catalogIdentity; ModelIdentity = modelIdentity; }

        public ArchiveSnapshot Snapshot { get; }
        public ResourceIdentity CatalogIdentity { get; }
        public ResourceIdentity ModelIdentity { get; }

        public static async Task<GeneratedFixture> CreateAsync(bool malformedMesh = false, uint groupIndex = 0)
        {
            const uint containerId = 731;
            const uint catalogId = 732;
            const uint ownerId = 733;
            const uint meshId = 734;
            var builder = FormatsFixtureBuilder.CreateEmpty();
            builder.AddResource(29, containerId, NativeContainer(
                (2009, ownerId, MeshOwner(meshId, groupIndex)),
                (2130, meshId, malformedMesh ? new byte[8] : MeshData())));
            builder.AddResource(3, catalogId, CatalogContainer(containerId));
            builder.WriteIndex();
            var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
            var catalog = Assert.Single(snapshot.Records, row => row.Type == 3);
            var owner = Assert.Single(snapshot.Records, row => row.Type == 29);
            return new(builder, snapshot,
                new(snapshot.IndexContentId, catalog.Ordinal),
                new(snapshot.IndexContentId, owner.Ordinal, "dc-object/0/2009/733"));
        }

        private static byte[] MeshOwner(uint meshId, uint groupIndex)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(4U); writer.Write("COM_CONT"u8); writer.Write(6U); writer.Write(0U);
            writer.Write("LOADCONT"u8); writer.Write(2U);
            writer.Write("ENTITY  "u8); writer.Write(7U);
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write((byte)0);
            writer.Write("MESH____"u8); writer.Write(29U); writer.Write(1U);
            writer.Write(2130U); writer.Write(meshId); writer.Write(1.0f);
            writer.Write(new byte[24]); writer.Write(0U); writer.Write(groupIndex);
            return stream.ToArray();
        }

        private static byte[] MeshData()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(3U); writer.Write("MESHDATA"u8); writer.Write(41U);
            writer.Write(1U); writer.Write(0U); writer.Write(9U); writer.Write((byte)0);
            writer.Write(1U); writer.Write(6U);
            writer.Write(1U); writer.Write(2U); writer.Write(0U); writer.Write(0U);
            WritePart(writer, 0, 5);
            WritePart(writer, 3, 7);
            writer.Write((byte)0); writer.Write((byte)0); WriteBounds(writer, 100);
            writer.Write(0U); writer.Write(0U);
            for (var index = 0; index < 4; index++) writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(0U);
            writer.Write("CLUPSKME"u8); writer.Write(2U); writer.Write((byte)0);
            writer.Write("BLSHAPES"u8); writer.Write(3U); writer.Write(0U);
            return stream.ToArray();
        }

        private static void WritePart(BinaryWriter writer, uint firstIndex, uint selector)
        {
            writer.Write(0U); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(0U); writer.Write(0U); writer.Write(3U);
            writer.Write(0U); writer.Write(firstIndex); writer.Write(3U); writer.Write(selector);
            writer.Write(0U);
            writer.Write(2133U); writer.Write(selector + 9000U);
            writer.Write(0U); writer.Write(0U); WriteBounds(writer, selector);
        }

        private static void WriteBounds(BinaryWriter writer, float start)
        { for (var index = 0; index < 19; index++) writer.Write(start + index); }

        private static byte[] NativeContainer(params (uint Type, uint Id, byte[] Payload)[] objects)
        {
            var frames = new List<byte[]>();
            foreach (var item in objects)
            {
                var frame = new byte[13 + item.Payload.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)item.Payload.Length));
                item.Payload.CopyTo(frame, 13); frames.Add(frame);
            }
            using var stream = new MemoryStream();
            stream.Write("QZIP\0DC_INFO "u8); WriteUInt32(stream, 3);
            WriteUInt32(stream, checked(8U + (uint)objects.Length * 8U));
            WriteUInt32(stream, 0); WriteUInt32(stream, (uint)objects.Length);
            foreach (var item in objects) { WriteUInt32(stream, item.Type); WriteUInt32(stream, item.Id); }
            stream.Write("DC_DATA "u8); WriteUInt32(stream, 23);
            WriteUInt32(stream, (uint)frames.Sum(frame => frame.Length));
            foreach (var frame in frames) stream.Write(frame);
            return stream.ToArray();
        }

        private static byte[] CatalogContainer(uint containerId)
        {
            using var stream = new MemoryStream();
            stream.Write("COM_CONT"u8); WriteUInt32(stream, 6); WriteUInt32(stream, 13);
            WriteUInt32(stream, 1); WriteUInt32(stream, 29); WriteUInt32(stream, containerId);
            stream.WriteByte(0);
            return stream.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes);
        }

        public void Dispose() => builder.Dispose();
    }
}
