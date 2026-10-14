using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterModelBindingReaderTests
{
    [Fact]
    public async Task SelectsEachExactOwnerFromOneCatalogAndPreservesBothLinks()
    {
        using var fixture = Fixture.Create(2009, true, additionalCatalogCompanion: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var catalog = new ResourceIdentity(snapshot.IndexContentId, Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalog);
        foreach (var row in snapshot.Records.Where(row => row.Type == 29))
        {
            using var lease = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
            var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
            var baseline = budget.UsedBytes;
            var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalog, references);
            Assert.Null(result.Diagnostic);
            Assert.Equal(lease.Identity, Assert.Single(result.CatalogContainerResolution.Candidates));
            Assert.Equal(references.Relationships, result.CatalogCompanionLinks);
            Assert.NotNull(Assert.Single(result.Lods).Selection);
            Assert.Equal(baseline, budget.UsedBytes);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task UnrelatedAmbiguousLinkDoesNotHideTheSelectedUniqueOwner()
    {
        using var fixture = Fixture.Create(2009, true, additionalCatalogCompanion: true, ambiguousAdditionalCompanion: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var selected = Assert.Single(snapshot.Records, row => row.Type == 29 && row.Id == fixture.ContainerResourceId);
        using var lease = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, selected.Ordinal), budget);
        var catalog = new ResourceIdentity(snapshot.IndexContentId, Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalog);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalog, references);
        Assert.Null(result.Diagnostic);
        Assert.Equal(ReferenceStatus.Unique, result.CatalogContainerResolution.Status);
        Assert.Contains(result.CatalogCompanionLinks, link => link.Resolution.Status == ReferenceStatus.Ambiguous);
        Assert.NotNull(Assert.Single(result.Lods).Selection);
    }

    [Fact]
    public async Task DuplicateExactCatalogLinksRetainTheirEvidenceWithoutAmbiguity()
    {
        using var fixture = Fixture.Create(2009, true, duplicateSelectedCatalogLink: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var selected = Assert.Single(snapshot.Records, row => row.Type == 29);
        using var lease = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, selected.Ordinal), budget);
        var catalog = new ResourceIdentity(snapshot.IndexContentId, Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalog);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalog, references);
        Assert.Null(result.Diagnostic);
        Assert.Single(result.CatalogContainerResolution.Candidates);
        Assert.Equal(references.Relationships, result.CatalogCompanionLinks);
        Assert.NotNull(Assert.Single(result.Lods).Selection);
    }

    [Fact]
    public async Task ExactMeshOwnerResolvesTypedLodAndReturnsCompactSelection()
    {
        using var fixture = Fixture.Create(ownerType: 2009, includeMeshData: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
        using var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);
        var descriptor = Descriptor(owner, lease.Revision);
        var budgetBeforeProjection = budget.UsedBytes;

        var result = new CharacterModelBindingReader().Read(lease, descriptor, catalogIdentity, references);

        Assert.Null(result.Diagnostic);
        Assert.Equal(owner.Identity, result.SelectedIdentity);
        Assert.Equal(containerRow.Ordinal, result.ContainerIdentity.Ordinal);
        var lod = Assert.Single(result.Lods);
        Assert.Equal(ReferenceStatus.Unique, lod.MeshDataResolution.Status);
        Assert.NotNull(lod.Selection);
        Assert.Equal(0, lod.Selection!.GroupIndex);
        Assert.Equal(1, lod.GroupCount);
        Assert.Equal(string.Empty, lod.StoredMeshName);
        Assert.Null(lod.Diagnostic);
        Assert.Contains(result.SourceExtents, extent => extent.Offset >= 0 && extent.Length == 9);
        Assert.Equal(ReferenceStatus.Unresolved, result.EntityNodeResolution!.Status);
        Assert.Equal(budgetBeforeProjection, budget.UsedBytes);
    }

    [Fact]
    public async Task ExactSkeletonOwnerProjectsLodsAndOptionalStoredNameEvidence()
    {
        using var fixture = Fixture.Create(ownerType: 2013, includeMeshData: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
        using var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2013);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity,
            references, new(IncludeSkeletonNameEvidence: true));

        Assert.Equal(2013u, result.OwnerNativeType);
        Assert.NotNull(result.Rig);
        Assert.Equal(15u, result.Rig!.Version);
        Assert.Empty(result.Rig.NameEvidence);
        var lod = Assert.Single(result.Lods);
        Assert.NotNull(lod.Selection);
        Assert.Equal(0, lod.Selection!.GroupIndex);
    }

    [Fact]
    public async Task MissingAndAmbiguousTypedLodsRemainExplicitWithoutSelection()
    {
        using (var missingFixture = Fixture.Create(ownerType: 2009, includeMeshData: false))
        {
            var snapshot = await new ArchiveReader().OpenAsync(missingFixture.IndexPath);
            var budget = new AssetMemoryBudget();
            var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var lease = await NativeContainerLease.OpenAsync(snapshot,
                new(snapshot.IndexContentId, containerRow.Ordinal), budget);
            var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
            var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
            var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
            var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

            var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);

            var lod = Assert.Single(result.Lods);
            Assert.Equal(ReferenceStatus.Missing, lod.MeshDataResolution.Status);
            Assert.Equal("CharacterMeshDataMissing", lod.Diagnostic);
            Assert.Null(lod.Selection);
        }

        using (var ambiguousFixture = Fixture.Create(ownerType: 2009, includeMeshData: true, duplicateMeshDataId: true))
        {
            var snapshot = await new ArchiveReader().OpenAsync(ambiguousFixture.IndexPath);
            var budget = new AssetMemoryBudget();
            var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var lease = await NativeContainerLease.OpenAsync(snapshot,
                new(snapshot.IndexContentId, containerRow.Ordinal), budget);
            var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
            var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
            var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
            var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

            var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);

            var lod = Assert.Single(result.Lods);
            Assert.Equal(ReferenceStatus.Ambiguous, lod.MeshDataResolution.Status);
            Assert.Equal(2, lod.MeshDataResolution.Candidates.Count);
            Assert.Equal("CharacterMeshDataAmbiguous", lod.Diagnostic);
            Assert.Null(lod.Selection);
        }
    }

    [Fact]
    public async Task CatalogMustIdentifyTheExactLeasedContainer()
    {
        using var fixture = Fixture.Create(ownerType: 2009, includeMeshData: true, catalogTargetsOtherContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29 && row.Id == fixture.ContainerResourceId);
        using var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);
        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);

        Assert.Equal("CharacterCatalogContainerMismatch", result.Diagnostic);
        Assert.Empty(result.Lods);
        Assert.Equal(ReferenceStatus.Unique, result.CatalogContainerResolution.Status);
    }

    [Fact]
    public async Task MissingAndAmbiguousCatalogCompanionsRemainStructured()
    {
        foreach (var ambiguous in new[] { false, true })
        {
            using var fixture = Fixture.Create(ownerType: 2009, includeMeshData: true,
                catalogTargetMissing: !ambiguous, duplicateCatalogTarget: ambiguous);
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var budget = new AssetMemoryBudget();
            var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29 && row.Id == fixture.ContainerResourceId);
            using var lease = await NativeContainerLease.OpenAsync(snapshot,
                new(snapshot.IndexContentId, containerRow.Ordinal), budget);
            var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
            var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
            var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
            var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

            var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);

            Assert.Empty(result.Lods);
            Assert.Equal(ambiguous ? ReferenceStatus.Ambiguous : ReferenceStatus.Missing,
                result.CatalogContainerResolution.Status);
            Assert.Equal(ambiguous ? 2 : 0, result.CatalogContainerResolution.Candidates.Count);
        }
    }

    [Fact]
    public async Task RejectsRevisionMismatchesAndReportsInvalidSelectedGroups()
    {
        using var fixture = Fixture.Create(ownerType: 2009, includeMeshData: true, ownerGroupIndex: 4);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
        using var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);
        var lod = Assert.Single(result.Lods);
        Assert.Equal(1, lod.GroupCount);
        Assert.Equal("CharacterMeshGroupUnavailable", lod.Diagnostic);
        Assert.Null(lod.Selection);
        Assert.Throws<ArchiveException>(() => new CharacterModelBindingReader().Read(lease,
            Descriptor(owner, lease.Revision with { PhysicalSourceStamp = "different-source-stamp" }), catalogIdentity, references));
        Assert.Throws<ArchiveException>(() => new CharacterModelBindingReader().Read(lease,
            Descriptor(owner with { Identity = owner.Identity with { MemberPath = "dc-object/0/2009/1" } }, lease.Revision),
            catalogIdentity, references));
        var staleReferences = references with { Revision = lease.Revision with { PhysicalSourceStamp = "different-source-stamp" } };
        var stale = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, staleReferences);
        Assert.Equal("CharacterCatalogReferenceUnverified", stale.Diagnostic);
        var otherSource = references with { Source = new ResourceIdentity("different-index", catalogIdentity.Ordinal) };
        var mismatched = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, otherSource);
        Assert.Equal("CharacterCatalogReferenceUnverified", mismatched.Diagnostic);
        Assert.Equal(budget.UsedBytes, lease.MemoryBudget.UsedBytes);
    }

    [Fact]
    public async Task MalformedMeshDataIsReportedOnItsLod()
    {
        using var fixture = Fixture.Create(ownerType: 2009, includeMeshData: true, malformedMeshData: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
        using var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2009);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);

        var result = new CharacterModelBindingReader().Read(lease, Descriptor(owner, lease.Revision), catalogIdentity, references);

        var lod = Assert.Single(result.Lods);
        Assert.Equal(ReferenceStatus.Unique, lod.MeshDataResolution.Status);
        Assert.Equal("NativeMeshClassCountUnsupported", lod.Diagnostic);
        Assert.Null(lod.Selection);
    }

    [Fact]
    public async Task CancellationAndDisposedLeaseDoNotRetainParserReservations()
    {
        using var fixture = Fixture.Create(ownerType: 2013, includeMeshData: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
        var lease = await NativeContainerLease.OpenAsync(snapshot,
            new(snapshot.IndexContentId, containerRow.Ordinal), budget);
        var owner = Assert.Single(lease.Directory.Objects, frame => frame.NativeType == 2013);
        var catalogRow = Assert.Single(snapshot.Records, row => row.Type == 3);
        var catalogIdentity = new ResourceIdentity(snapshot.IndexContentId, catalogRow.Ordinal);
        var references = await new LocalizationReferenceReader().ReadAsync(snapshot, catalogIdentity);
        var baseline = budget.UsedBytes;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => new CharacterModelBindingReader().Read(
                lease, Descriptor(owner, lease.Revision), catalogIdentity, references, token: cancelled.Token));
        }
        Assert.Equal(baseline, budget.UsedBytes);
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => new CharacterModelBindingReader().Read(
            lease, Descriptor(owner, lease.Revision), catalogIdentity, references));
        Assert.Equal(0, budget.UsedBytes);
    }

    private static AssetDescriptor Descriptor(NativeObjectFrame frame, SourceRevision revision) =>
        new(frame.Identity, revision, "native-model-member", "", null, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.None, [], []);

    private sealed class Fixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private readonly uint containerId = NextId();
        private readonly uint ownerId = NextId();
        private readonly uint meshId = NextId();
        private readonly uint catalogId = NextId();
        private readonly uint alternateContainerId = NextId();
        private readonly uint missingContainerId = NextId();

        private Fixture(FormatsFixtureBuilder builder) => this.builder = builder;
        public string IndexPath => builder.IndexPath;
        public uint ContainerResourceId => containerId;

        public static Fixture Create(uint ownerType, bool includeMeshData, bool duplicateMeshDataId = false,
            uint ownerGroupIndex = 0, bool catalogTargetsOtherContainer = false,
            bool catalogTargetMissing = false, bool duplicateCatalogTarget = false,
            bool malformedMeshData = false, bool additionalCatalogCompanion = false,
            bool ambiguousAdditionalCompanion = false, bool duplicateSelectedCatalogLink = false)
        {
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var fixture = new Fixture(builder);
            var objects = new List<(uint Type, uint Id, byte[] Payload)>
            {
                (ownerType, fixture.ownerId, ownerType switch
                {
                    2009 => MeshOwner(fixture.meshId, ownerGroupIndex),
                    2013 => SkeletonOwner(fixture.meshId, ownerGroupIndex),
                    _ => throw new NotSupportedException()
                })
            };
            if (includeMeshData)
            {
                var meshPayload = malformedMeshData ? new byte[8] : MeshData();
                objects.Add((2130, fixture.meshId, meshPayload));
                if (duplicateMeshDataId) objects.Add((2130, fixture.meshId, meshPayload));
            }
            builder.AddResource(29, fixture.containerId, NativeContainer(objects.ToArray()));
            var catalogContainerId = catalogTargetMissing ? fixture.missingContainerId :
                catalogTargetsOtherContainer || duplicateCatalogTarget ? fixture.alternateContainerId : fixture.containerId;
            if (catalogTargetsOtherContainer || duplicateCatalogTarget)
            {
                builder.AddResource(29, catalogContainerId, NativeContainer(objects.ToArray()));
                if (duplicateCatalogTarget) builder.AddResource(29, catalogContainerId, NativeContainer(objects.ToArray()));
            }
            if (additionalCatalogCompanion)
            {
                builder.AddResource(29, fixture.alternateContainerId, NativeContainer(objects.ToArray()));
                if (ambiguousAdditionalCompanion)
                    builder.AddResource(29, fixture.alternateContainerId, NativeContainer(objects.ToArray()));
            }
            var catalogLinks = new List<uint> { catalogContainerId };
            if (additionalCatalogCompanion) catalogLinks.Add(fixture.alternateContainerId);
            if (duplicateSelectedCatalogLink) catalogLinks.Add(catalogContainerId);
            builder.AddResource(3, fixture.catalogId, CatalogContainer(catalogLinks.ToArray()));
            builder.WriteIndex();
            return fixture;
        }

        public void Dispose() => builder.Dispose();

        private static byte[] MeshOwner(uint meshId, uint groupIndex)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteCommonPrefix(writer, 4);
            writer.Write("ENTITY  "u8); writer.Write(7U);
            writer.Write(0U); writer.Write(0U); // no entity-node link
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write((byte)0);
            writer.Write("MESH____"u8); writer.Write(29U); writer.Write(1U);
            writer.Write(2130U); writer.Write(meshId); writer.Write(1.0f);
            writer.Write(new byte[24]); writer.Write(0U); writer.Write(groupIndex); // flags and selected group
            return stream.ToArray();
        }

        private static byte[] SkeletonOwner(uint meshId, uint groupIndex)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteCommonPrefix(writer, 5);
            writer.Write("ENTITY  "u8); writer.Write(7U);
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write((byte)0);
            writer.Write("MESH____"u8); writer.Write(29U); writer.Write(1U);
            writer.Write(2130U); writer.Write(meshId); writer.Write(1.0f);
            writer.Write(0U); writer.Write(0U); // fixed words
            writer.Write(0U); writer.Write(0U); // fixed typed references
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(groupIndex); // flags and group selector
            writer.Write(0U); writer.Write(0U); // context control and reference count
            writer.Write(0U); writer.Write((byte)0); writer.Write(new byte[6]); writer.Write(new byte[20]);
            writer.Write("SKELETON"u8); writer.Write(15U); writer.Write(0U); // leading scalar
            writer.Write(0U); writer.Write(0U); writer.Write(0U); // matrix count, nested count, trailing scalar
            writer.Write(0U); writer.Write(0U); // two empty name tables
            return stream.ToArray();
        }

        private static byte[] MeshData()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(3U); writer.Write("MESHDATA"u8); writer.Write(41U);
            writer.Write(0U); // vertex buffers
            writer.Write(0U); // index buffers
            writer.Write(1U); // groups
            writer.Write(0U); writer.Write(0U); writer.Write(0U); // parts and group words
            writer.Write((byte)0); writer.Write((byte)0);
            for (var index = 0; index < 19; index++) writer.Write(0.0f);
            writer.Write(0U); // references and factor control
            writer.Write(0U); // stored word
            writer.Write(0U); // name bytes
            writer.Write(0U); writer.Write(0U); writer.Write(0U); // tail word and null native reference
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(0U);
            writer.Write("CLUPSKME"u8); writer.Write(2U); writer.Write((byte)0);
            writer.Write("BLSHAPES"u8); writer.Write(3U); writer.Write(0U);
            return stream.ToArray();
        }

        private static byte[] NativeContainer((uint Type, uint Id, byte[] Payload)[] objects)
        {
            var frames = new List<byte[]>();
            foreach (var item in objects)
            {
                var frame = new byte[13 + item.Payload.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)item.Payload.Length);
                item.Payload.CopyTo(frame, 13);
                frames.Add(frame);
            }
            using var stream = new MemoryStream();
            stream.Write("QZIP\0DC_INFO "u8); WriteUInt32(stream, 3); WriteUInt32(stream, checked(8U + (uint)objects.Length * 8U));
            WriteUInt32(stream, 0); WriteUInt32(stream, (uint)objects.Length);
            foreach (var item in objects) { WriteUInt32(stream, item.Type); WriteUInt32(stream, item.Id); }
            stream.Write("DC_DATA "u8); WriteUInt32(stream, 23);
            WriteUInt32(stream, (uint)frames.Sum(frame => frame.Length));
            foreach (var frame in frames) stream.Write(frame);
            return stream.ToArray();
        }

        private static byte[] CatalogContainer(params uint[] containerIds)
        {
            using var stream = new MemoryStream();
            stream.Write("COM_CONT"u8); WriteUInt32(stream, 6);
            WriteUInt32(stream, checked(4U + (uint)containerIds.Length * 9U));
            WriteUInt32(stream, (uint)containerIds.Length);
            foreach (var containerId in containerIds)
            {
                WriteUInt32(stream, 29); WriteUInt32(stream, containerId); stream.WriteByte(0);
            }
            return stream.ToArray();
        }

        private static void WriteCommonPrefix(BinaryWriter writer, uint classCount)
        {
            writer.Write(classCount); writer.Write("COM_CONT"u8); writer.Write(6U); writer.Write(0U);
            writer.Write("LOADCONT"u8); writer.Write(2U);
        }

        private static uint NextId() => unchecked((uint)Random.Shared.Next(1, int.MaxValue));

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
