using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeSkeletonBindingSessionTests
{
    [Fact]
    public async Task ConsumerLeaseRetainsParsedDocumentsAfterOwnerSessionDisposes()
    {
        using var fixture = await Fixture.CreateAsync();
        var session = await NativeSkeletonBindingSession.OpenAsync(fixture.Snapshot, fixture.Binding, fixture.Budget);
        var consumer = session.AcquireLease();
        Assert.Equal(NativeSkeletonBindingSessionStatus.Bound, consumer.Result.Status);
        Assert.Equal(15u, consumer.Skeleton.Version);
        Assert.NotNull(consumer.NodeArray);
        Assert.Single(consumer.Binding!.Entries);
        Assert.True(fixture.Budget.UsedBytes > 0);

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.AcquireLease());
        consumer.EnsureUnchanged();
        Assert.Equal(15u, consumer.Skeleton.Version);
        Assert.Single(consumer.Binding!.Entries);
        Assert.True(fixture.Budget.UsedBytes > 0);

        consumer.Dispose();
        Assert.Equal(0, fixture.Budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => _ = consumer.Skeleton);
    }

    [Fact]
    public async Task MultipleConsumersDrainBeforeBorrowedContainerAndProjectionRelease()
    {
        using var fixture = await Fixture.CreateAsync();
        var session = await NativeSkeletonBindingSession.OpenAsync(fixture.Snapshot, fixture.Binding, fixture.Budget);
        var first = session.AcquireLease();
        var second = session.AcquireLease();
        session.Dispose();

        first.Dispose();
        Assert.Equal(15u, second.Skeleton.Version);
        second.EnsureUnchanged();
        Assert.True(fixture.Budget.UsedBytes > 0);

        second.Dispose();
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task FromContainerDoesNotDisposeTheCallerOwnedLease()
    {
        using var fixture = await Fixture.CreateAsync();
        using var container = await NativeContainerLease.OpenAsync(fixture.Snapshot,
            fixture.Binding.ContainerIdentity, fixture.Budget);
        var session = await NativeSkeletonBindingSession.FromContainerAsync(container, fixture.Binding, fixture.Binding.Revision);
        var consumer = session.AcquireLease();
        session.Dispose();
        Assert.Equal(fixture.Binding.ContainerIdentity, container.Identity);
        Assert.Equal(15u, consumer.Skeleton.Version);
        consumer.Dispose();
        Assert.Equal(fixture.Binding.ContainerIdentity, container.Identity);
        Assert.True(fixture.Budget.UsedBytes > 0); // caller still owns the native container
    }

    [Fact]
    public async Task PreservesMissingAndAmbiguousEntityNodeCandidatesWithoutSelectingOne()
    {
        using (var missing = await Fixture.CreateAsync(includeNodeArray: false))
        {
            var session = await NativeSkeletonBindingSession.OpenAsync(missing.Snapshot, missing.Binding, missing.Budget);
            using var consumer = session.AcquireLease();
            Assert.Equal(NativeSkeletonBindingSessionStatus.EntityNodeMissing, consumer.Result.Status);
            Assert.Equal(ReferenceStatus.Missing, consumer.Result.EntityNodeResolution!.Status);
            Assert.Empty(consumer.Result.EntityNodeResolution.Candidates);
            Assert.Null(consumer.NodeArray);
            Assert.Null(consumer.Binding);
            session.Dispose();
            Assert.True(missing.Budget.UsedBytes > 0);
            consumer.Dispose();
            Assert.Equal(0, missing.Budget.UsedBytes);
        }

        using (var ambiguous = await Fixture.CreateAsync(duplicateNodeArray: true))
        {
            var session = await NativeSkeletonBindingSession.OpenAsync(ambiguous.Snapshot, ambiguous.Binding, ambiguous.Budget);
            using var consumer = session.AcquireLease();
            Assert.Equal(NativeSkeletonBindingSessionStatus.EntityNodeAmbiguous, consumer.Result.Status);
            Assert.Equal(ReferenceStatus.Ambiguous, consumer.Result.EntityNodeResolution!.Status);
            Assert.Equal(2, consumer.Result.EntityNodeResolution.Candidates.Count);
            Assert.Null(consumer.NodeArray);
            Assert.Null(consumer.Binding);
            session.Dispose();
            Assert.True(ambiguous.Budget.UsedBytes > 0);
            consumer.Dispose();
            Assert.Equal(0, ambiguous.Budget.UsedBytes);
        }
    }

    [Fact]
    public async Task RejectsCachedRevisionAndGroupMismatchWithoutLeakingWorkingReservation()
    {
        using var fixture = await Fixture.CreateAsync();
        var stale = fixture.Binding with { Revision = fixture.Binding.Revision with { PhysicalSourceStamp = "stale" } };
        await Assert.ThrowsAsync<ArchiveException>(() => NativeSkeletonBindingSession.OpenAsync(
            fixture.Snapshot, stale, fixture.Budget));
        Assert.Equal(0, fixture.Budget.UsedBytes);

        var wrongGroup = fixture.Binding with { GroupIndex = 9 };
        await Assert.ThrowsAsync<ArchiveException>(() => NativeSkeletonBindingSession.OpenAsync(
            fixture.Snapshot, wrongGroup, fixture.Budget));
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndChangedSourceEvidenceReleaseAllNewReservations()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeSkeletonBindingSession.OpenAsync(
                fixture.Snapshot, fixture.Binding, fixture.Budget, cancelled.Token));
            Assert.Equal(0, fixture.Budget.UsedBytes);
        }

        using var container = await NativeContainerLease.OpenAsync(fixture.Snapshot,
            fixture.Binding.ContainerIdentity, fixture.Budget);
        var before = fixture.Budget.UsedBytes;
        var changedExtents = fixture.Binding.SourceExtents.ToArray();
        var externalIndex = Array.FindIndex(changedExtents, extent => !container.SourceExtents.Contains(extent));
        Assert.NotEqual(-1, externalIndex);
        changedExtents[externalIndex] = changedExtents[externalIndex] with { PayloadSha256 = new string('0', 64) };
        var changed = fixture.Binding with { SourceExtents = changedExtents };
        await Assert.ThrowsAsync<ArchiveException>(() => NativeSkeletonBindingSession.FromContainerAsync(
            container, changed, changed.Revision));
        Assert.Equal(before, fixture.Budget.UsedBytes);
    }

    [Fact]
    public async Task BudgetPressureRejectsProjectionBeforeParsingAndPreservesCallerContainer()
    {
        using var fixture = await Fixture.CreateAsync();
        var budget = new AssetMemoryBudget(8L * 1024 * 1024);
        using var container = await NativeContainerLease.OpenAsync(fixture.Snapshot,
            fixture.Binding.ContainerIdentity, budget);
        var baseline = budget.UsedBytes;

        var error = await Assert.ThrowsAsync<ArchiveException>(() => NativeSkeletonBindingSession.FromContainerAsync(
            container, fixture.Binding, fixture.Binding.Revision));

        Assert.Equal("AssetMemoryLimit", error.Code);
        Assert.Equal(baseline, budget.UsedBytes);
        Assert.Equal(fixture.Binding.ContainerIdentity, container.Identity);
    }

    [Fact]
    public async Task SmallBindingCompletesWithSharedBudgetUnderCatalogPressure()
    {
        using var fixture = await Fixture.CreateAsync();
        var budget = new AssetMemoryBudget(256L * 1024 * 1024);
        using var container = await NativeContainerLease.OpenAsync(fixture.Snapshot,
            fixture.Binding.ContainerIdentity, budget);
        var containerBytes = budget.UsedBytes;

        using (budget.Reserve(160L * 1024 * 1024, "simulated catalog working set"))
        {
            var session = await NativeSkeletonBindingSession.FromContainerAsync(
                container, fixture.Binding, fixture.Binding.Revision);
            var consumer = session.AcquireLease();
            Assert.Equal(NativeSkeletonBindingSessionStatus.Bound, consumer.Result.Status);
            Assert.Single(consumer.Binding!.Entries);
            Assert.InRange(budget.PeakBytes, 160L * 1024 * 1024, budget.MaximumBytes);

            session.Dispose();
            consumer.Dispose();
            Assert.Equal(containerBytes + 160L * 1024 * 1024, budget.UsedBytes);
        }

        Assert.Equal(containerBytes, budget.UsedBytes);
        container.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task SourceChangedSinceSnapshotCannotOpenBindingSession()
    {
        using var fixture = await Fixture.CreateAsync();
        var volume = Assert.Single(fixture.Snapshot.Volumes);
        using (var stream = new FileStream(volume.FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.WriteByte(0x5A);

        var error = await Assert.ThrowsAsync<ArchiveException>(() => NativeSkeletonBindingSession.OpenAsync(
            fixture.Snapshot, fixture.Binding, fixture.Budget));

        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private Fixture(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot,
            CharacterModelBindingResult binding, AssetMemoryBudget budget)
        { this.builder = builder; Snapshot = snapshot; Binding = binding; Budget = budget; }

        public ArchiveSnapshot Snapshot { get; }
        public CharacterModelBindingResult Binding { get; }
        public AssetMemoryBudget Budget { get; }

        public static async Task<Fixture> CreateAsync(bool includeNodeArray = true, bool duplicateNodeArray = false)
        {
            const uint containerId = 101, skeletonId = 17, nodeId = 22, meshId = 50, catalogId = 77;
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var skeletonPayload = SkeletonOwner(nodeId, meshId);
            var objects = new List<(uint Type, uint Id, byte[] Payload)> { (2013, skeletonId, skeletonPayload) };
            if (includeNodeArray) objects.Add((2138, nodeId, NodeArrayBody()));
            if (duplicateNodeArray) objects.Add((2138, nodeId, NodeArrayBody()));
            objects.Add((2130, meshId, MeshData()));
            builder.AddResource(29, containerId, NativeContainer(objects.ToArray()));
            builder.AddResource(3, catalogId, CatalogContainer(containerId));
            builder.WriteIndex();
            try
            {
                var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
                var budget = new AssetMemoryBudget();
                var containerIdentity = new ResourceIdentity(snapshot.IndexContentId,
                    Assert.Single(snapshot.Records, row => row.Type == 29).Ordinal);
                var container = await NativeContainerLease.OpenAsync(snapshot, containerIdentity, budget);
                try
                {
                    var owner = Assert.Single(container.Directory.Objects, frame => frame.NativeType == 2013);
                    var descriptor = new AssetDescriptor(owner.Identity, container.Revision, "synthetic-model", "", null,
                        AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, [], []);
                    var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId,
                        Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);
                    var catalog = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);
                    var binding = new CharacterModelBindingReader().Read(container, descriptor, catalogIdentity, catalog);
                    Assert.Null(binding.Diagnostic);
                    return new(builder, snapshot, binding, budget);
                }
                finally { container.Dispose(); }
            }
            catch { builder.Dispose(); throw; }
        }

        public void Dispose() => builder.Dispose();
    }

    private static byte[] SkeletonOwner(uint nodeArrayId, uint meshDataId)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(5u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u);
        writer.Write("ENTITY  "u8); writer.Write(7u); writer.Write(2138u); writer.Write(nodeArrayId);
        writer.Write(new byte[24]); writer.Write(0u); writer.Write((byte)0);
        writer.Write("MESH____"u8); writer.Write(29u); writer.Write(1u);
        writer.Write(2130u); writer.Write(meshDataId); writer.Write(1.0f);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); // context control and reference count
        writer.Write(0u); writer.Write((byte)0); writer.Write(new byte[6]); writer.Write(new byte[20]);
        writer.Write("SKELETON"u8); writer.Write(15u); writer.Write(0u); writer.Write(1u);
        for (var index = 0; index < 16; index++) writer.Write(index % 5 == 0 ? 1.0f : 0.0f);
        writer.Write(0u); writer.Write(0u); // nested records and trailing word
        WriteNameTable(writer, 0x101u, "synthetic-root"); WriteNameTable(writer, 0x102u, "synthetic-secondary");
        return stream.ToArray();
    }

    private static byte[] NodeArrayBody()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var prefix = new byte[208];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 2u); "NODE    "u8.CopyTo(prefix.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(12), 5u);
        "NODEAR  "u8.CopyTo(prefix.AsSpan(196)); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(204), 4u);
        writer.Write(prefix); writer.Write(14u); writer.Write(1u); writer.Write(0u);
        writer.Write(0u); for (var i = 0; i < 10; i++) writer.Write((float)i);
        writer.Write(0u); writer.Write(uint.MaxValue); writer.Write(0u); writer.Write(0u);
        writer.Write(0x101u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(0u); writer.Write(0u); // optional bytes and compact records
        writer.Write(1u); writer.Write((byte)0); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        return stream.ToArray();
    }

    private static byte[] MeshData()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(3u); writer.Write("MESHDATA"u8); writer.Write(41u); writer.Write(0u); writer.Write(0u);
        writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0); writer.Write((byte)0);
        for (var index = 0; index < 19; index++) writer.Write(0.0f);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
        writer.Write("CLUPSKME"u8); writer.Write(2u); writer.Write((byte)0);
        writer.Write("BLSHAPES"u8); writer.Write(3u); writer.Write(0u);
        return stream.ToArray();
    }

    private static byte[] NativeContainer((uint Type, uint Id, byte[] Payload)[] objects)
    {
        using var stream = new MemoryStream(); stream.Write("QZIP\0DC_INFO "u8); WriteUInt32(stream, 3);
        WriteUInt32(stream, checked(8u + (uint)objects.Length * 8)); WriteUInt32(stream, 0);
        WriteUInt32(stream, (uint)objects.Length);
        foreach (var item in objects) { WriteUInt32(stream, item.Type); WriteUInt32(stream, item.Id); }
        using var body = new MemoryStream();
        foreach (var item in objects)
        {
            WriteUInt32(body, (uint)item.Payload.Length); body.WriteByte(0); WriteUInt32(body, 0); WriteUInt32(body, 0);
            body.Write(item.Payload);
        }
        stream.Write("DC_DATA "u8); WriteUInt32(stream, 23); WriteUInt32(stream, (uint)body.Length);
        body.Position = 0; body.CopyTo(stream); return stream.ToArray();
    }

    private static byte[] CatalogContainer(uint containerId)
    {
        using var stream = new MemoryStream(); stream.Write("COM_CONT"u8); WriteUInt32(stream, 6);
        WriteUInt32(stream, 13); WriteUInt32(stream, 1); WriteUInt32(stream, 29); WriteUInt32(stream, containerId);
        stream.WriteByte(0); return stream.ToArray();
    }

    private static void WriteNameTable(BinaryWriter writer, uint key, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name); WriteUInt32(writer.BaseStream, (uint)(6 + bytes.Length));
        WriteUInt32(writer.BaseStream, key); writer.Write((ushort)bytes.Length); writer.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes);
    }
}
