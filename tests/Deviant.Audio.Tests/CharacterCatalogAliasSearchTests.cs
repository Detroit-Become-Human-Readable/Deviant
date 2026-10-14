using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterCatalogAliasSearchTests
{
    [Fact]
    public async Task AliasAddRenameRemoveUpdatesSearchCountAndGlobalNamePagesImmediately()
    {
        using var generated = await GeneratedFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = NewCacheRoot();
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var aliases = new AliasStore(Path.Combine(cacheRoot, "library"));
            await using var references = new CatalogReferenceProvider(generated.Snapshot, budget, features);
            await using var session = new CharacterCatalogSession(generated.Snapshot, references, budget,
                aliases: aliases, features: features);
            using (var discovered = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Equal(2, discovered.Models.Count);

            var first = generated.ModelIdentities[0];
            var second = generated.ModelIdentities[1];
            await Assert.ThrowsAsync<ArchiveException>(() => session.SetAliasAsync(first, "Rejected",
                new SourceRevision(references.Revision.IndexContentId, "stale-revision")));
            Assert.Null(aliases.Get(first));
            await session.SetAliasAsync(first, "Zed", references.Revision);
            await session.SetAliasAsync(second, "Able", references.Revision);
            Assert.Equal(1, Count(features, "Able"));
            using (var firstPage = session.QueryAssets(new AssetQuery(Limit: 1, Sort: AssetSort.Name), references.Revision))
                Assert.Equal(second, Assert.Single(firstPage.Assets).Identity);

            await session.SetAliasAsync(second, "Baker", references.Revision);
            Assert.Equal(0, Count(features, "Able"));
            Assert.Equal(1, Count(features, "Baker"));
            using (var renamedPage = session.QueryAssets(new AssetQuery(Search: "Baker", Limit: 1), references.Revision))
                Assert.Equal(second, Assert.Single(renamedPage.Assets).Identity);

            await session.SetAliasAsync(first, "Aaron", references.Revision);
            using (var renamedOrder = session.QueryAssets(new AssetQuery(Limit: 1, Sort: AssetSort.Name), references.Revision))
                Assert.Equal(first, Assert.Single(renamedOrder.Assets).Identity);

            await session.SetAliasAsync(first, null, references.Revision);
            Assert.Equal(0, Count(features, "Aaron"));
            using (var removedPage = session.QueryAssets(new AssetQuery(Limit: 1, Sort: AssetSort.Name), references.Revision))
                Assert.Equal(second, Assert.Single(removedPage.Assets).Identity);

            await session.SetAliasAsync(second, null, references.Revision);
            Assert.Equal(0, Count(features, "Baker"));
        }
        finally { DeleteCacheRoot(cacheRoot); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsAliasWriteWaitingOnAliasStoreLock()
    {
        using var generated = await GeneratedFixture.CreateAsync(ownerCount: 1);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = NewCacheRoot();
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var aliases = new AliasStore(Path.Combine(cacheRoot, "library"));
            await using var references = new CatalogReferenceProvider(generated.Snapshot, budget, features);
            var session = new CharacterCatalogSession(generated.Snapshot, references, budget,
                aliases: aliases, features: features);
            using (var discovered = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Single(discovered.Models);

            var identity = generated.ModelIdentities[0];
            using var heldLock = new FileStream(Path.Combine(aliases.LibraryRoot, "aliases.json.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var write = session.SetAliasAsync(identity, "Pending", references.Revision);
            Assert.False(write.IsCompleted);
            var disposing = session.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await write);
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(aliases.Get(identity));
        }
        finally { DeleteCacheRoot(cacheRoot); }
        Assert.Equal(0, budget.UsedBytes);
    }

    private static long Count(FeatureStore features, string search) =>
        features.Count(new AssetQuery(Search: search, Family: "character-model"));

    private static string NewCacheRoot() => Path.Combine(Path.GetTempPath(), "deviant-character-alias-" + Guid.NewGuid().ToString("N"));

    private static void DeleteCacheRoot(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class GeneratedFixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private GeneratedFixture(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot,
            ResourceIdentity catalogIdentity, IReadOnlyList<ResourceIdentity> modelIdentities)
        { this.builder = builder; Snapshot = snapshot; CatalogIdentity = catalogIdentity; ModelIdentities = modelIdentities; }

        public ArchiveSnapshot Snapshot { get; }
        public ResourceIdentity CatalogIdentity { get; }
        public IReadOnlyList<ResourceIdentity> ModelIdentities { get; }

        public static async Task<GeneratedFixture> CreateAsync(int ownerCount = 2)
        {
            const uint containerId = 731;
            const uint catalogId = 732;
            var objects = new List<(uint Type, uint Id, byte[] Payload)>();
            var modelIds = new List<uint>();
            for (var index = 0; index < ownerCount; index++)
            {
                var ownerId = checked((uint)(733 + index * 2));
                var meshId = checked(ownerId + 1);
                modelIds.Add(ownerId);
                objects.Add((2009, ownerId, MeshOwner(meshId)));
                objects.Add((2130, meshId, MeshData()));
            }

            var builder = FormatsFixtureBuilder.CreateEmpty();
            builder.AddResource(29, containerId, NativeContainer(objects.ToArray()));
            builder.AddResource(3, catalogId, CatalogContainer(containerId));
            builder.WriteIndex();
            var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
            var catalog = Assert.Single(snapshot.Records, row => row.Type == 3);
            var owner = Assert.Single(snapshot.Records, row => row.Type == 29);
            var identities = modelIds.Select((id, index) => new ResourceIdentity(snapshot.IndexContentId, owner.Ordinal,
                $"dc-object/{index * 2}/2009/{id}")).ToArray();
            return new(builder, snapshot, new(snapshot.IndexContentId, catalog.Ordinal), identities);
        }

        private static byte[] MeshOwner(uint meshId)
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
            writer.Write(new byte[24]); writer.Write(0U); writer.Write(0U);
            return stream.ToArray();
        }

        private static byte[] MeshData()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(3U); writer.Write("MESHDATA"u8); writer.Write(41U);
            writer.Write(0U); writer.Write(0U); writer.Write(1U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write((byte)0);
            for (var index = 0; index < 19; index++) writer.Write(0.0f);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(0U);
            writer.Write("CLUPSKME"u8); writer.Write(2U); writer.Write((byte)0);
            writer.Write("BLSHAPES"u8); writer.Write(3U); writer.Write(0U);
            return stream.ToArray();
        }

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
