using System.Buffers.Binary;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationRelationshipCoordinatorTests
{
    [Fact]
    public async Task SelectedTextUsesOnlyAnExactSameSourceKeyPointerAndKeepsTheThreeEdgeEvidencePath()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildLinkedSnapshotAsync(fixture, includeText: true);
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var source = new ResourceIdentity(snapshot.IndexContentId, 0);
            var text = Assert.Single((await session.QueryAsync(new LocalizationQuery(Source: source,
                ExactKey: "DIALOG_KEY", Kind: LocalizationRecordKind.Text, Limit: 10))).Records);
            var coordinator = new LocalizationRelationshipCoordinator(cacheCapacity: 1);

            var result = await coordinator.ResolveSelectedAsync(session, text.Identity);

            var relationship = Assert.Single(result.Relationships);
            Assert.Equal(text.Identity, relationship.SelectedOccurrenceIdentity);
            var pointerEdge = Assert.Single(relationship.EvidencePath, edge => edge.Reference.ResourceType == 4091);
            Assert.Equal(4091u, pointerEdge.Reference.ResourceType);
            Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 1), relationship.DialogueHeaderIdentity);
            Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 2), relationship.AudioResourceIdentity);
            Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 3), relationship.DialogueEventIdentity);
            Assert.Equal(3, relationship.EvidencePath.Count);
            Assert.Equal(1, coordinator.CachedResourceCount);
            Assert.Empty(result.Diagnostics);
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task ReportsMissingAndAmbiguousHeaderTargetsWithoutUsingTextNames()
    {
        using var missingFixture = new LocalizationFixture();
        var missingSnapshot = await BuildMissingSnapshotAsync(missingFixture);
        try
        {
            await using var missingSession = await OpenSessionAsync(missingSnapshot, missingFixture);
            var pointer = Assert.Single((await missingSession.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(missingSnapshot.IndexContentId, 0), ExactKey: "DIALOG_KEY",
                Kind: LocalizationRecordKind.Pointer, Limit: 10))).Records);
            var missing = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(missingSession, pointer.Identity);
            Assert.Empty(missing.Relationships);
            Assert.Equal("PointerReferenceTargetMissing", Assert.Single(missing.Diagnostics).Code);
            Assert.Equal(LocalizationReferenceLinkStatus.Unmatched, missing.Diagnostics[0].Status);
        }
        finally { DeleteCache(missingFixture); }

        using var ambiguousFixture = new LocalizationFixture();
        var ambiguousSnapshot = await BuildAmbiguousSnapshotAsync(ambiguousFixture);
        try
        {
            await using var ambiguousSession = await OpenSessionAsync(ambiguousSnapshot, ambiguousFixture);
            var pointer = Assert.Single((await ambiguousSession.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(ambiguousSnapshot.IndexContentId, 0), ExactKey: "DIALOG_KEY",
                Kind: LocalizationRecordKind.Pointer, Limit: 10))).Records);
            var ambiguous = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(ambiguousSession, pointer.Identity);
            Assert.Empty(ambiguous.Relationships);
            Assert.Equal("PointerReferenceAmbiguous", Assert.Single(ambiguous.Diagnostics).Code);
            Assert.Equal(LocalizationReferenceLinkStatus.Ambiguous, ambiguous.Diagnostics[0].Status);
            Assert.Equal(2, ambiguous.Diagnostics[0].EvidencePath.Single().Resolution.Candidates.Count);
        }
        finally { DeleteCache(ambiguousFixture); }
    }

    [Fact]
    public async Task TextWithoutAnExactSameResourcePointerGetsNoNameBasedAudioJoin()
    {
        using var fixture = new LocalizationFixture();
        var localized = LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("UI_STATUS", "A stored audio-looking label") ]));
        fixture.AddResource(MakeContainer([(4091u, 2u, (byte)0xA5)], localized));
        fixture.AddResource(MakeContainer([]));
        var original = await fixture.BuildAsync();
        var snapshot = WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 4091, Id = 2 } : row);
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var text = Assert.Single((await session.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(snapshot.IndexContentId, 0), ExactKey: "UI_STATUS",
                Kind: LocalizationRecordKind.Text, Limit: 10))).Records);
            var result = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, text.Identity);
            Assert.Empty(result.Relationships);
            Assert.Equal("NoSameSourcePointer", Assert.Single(result.Diagnostics).Code);
        }
        finally { DeleteCache(fixture); }
    }

    [Fact]
    public async Task CacheHitsRehashTheIndexAndReferenceTable()
    {
        using var indexFixture = new LocalizationFixture();
        var indexSnapshot = await BuildLinkedSnapshotAsync(indexFixture, includeText: false);
        try
        {
            await using var session = await OpenSessionAsync(indexSnapshot, indexFixture);
            var pointer = Assert.Single((await session.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(indexSnapshot.IndexContentId, 0), ExactKey: "DIALOG_KEY",
                Kind: LocalizationRecordKind.Pointer, Limit: 10))).Records);
            var coordinator = new LocalizationRelationshipCoordinator(
                new LocalizationReferenceReader(new FrozenRevisionProvider(indexSnapshot)));
            _ = await coordinator.ResolveSelectedAsync(session, pointer.Identity);
            var indexBytes = await File.ReadAllBytesAsync(indexFixture.IndexPath);
            indexBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(indexFixture.IndexPath, indexBytes);

            var indexChanged = await Assert.ThrowsAsync<ArchiveException>(() => coordinator.ResolveSelectedAsync(session, pointer.Identity));
            Assert.Equal("SourceChanged", indexChanged.Code);
        }
        finally { DeleteCache(indexFixture); }

        using var tableFixture = new LocalizationFixture();
        var tableSnapshot = await BuildLinkedSnapshotAsync(tableFixture, includeText: false);
        try
        {
            await using var session = await OpenSessionAsync(tableSnapshot, tableFixture);
            var pointer = Assert.Single((await session.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(tableSnapshot.IndexContentId, 0), ExactKey: "DIALOG_KEY",
                Kind: LocalizationRecordKind.Pointer, Limit: 10))).Records);
            var coordinator = new LocalizationRelationshipCoordinator(
                new LocalizationReferenceReader(new FrozenRevisionProvider(tableSnapshot)));
            _ = await coordinator.ResolveSelectedAsync(session, pointer.Identity);
            var sourceRow = tableSnapshot.Records.Single(row => row.Ordinal == 0);
            var bytes = await File.ReadAllBytesAsync(tableFixture.VolumePath);
            bytes[checked((int)sourceRow.Offset + 28)] ^= 1;
            await File.WriteAllBytesAsync(tableFixture.VolumePath, bytes);

            var tableChanged = await Assert.ThrowsAsync<ArchiveException>(() => coordinator.ResolveSelectedAsync(session, pointer.Identity));
            Assert.Equal("SourceChanged", tableChanged.Code);
        }
        finally { DeleteCache(tableFixture); }
    }

    [Fact]
    public async Task CancellationStopsSelectedRelationshipResolution()
    {
        using var fixture = new LocalizationFixture();
        var snapshot = await BuildLinkedSnapshotAsync(fixture, includeText: false);
        try
        {
            await using var session = await OpenSessionAsync(snapshot, fixture);
            var pointer = Assert.Single((await session.QueryAsync(new LocalizationQuery(
                Source: new ResourceIdentity(snapshot.IndexContentId, 0), ExactKey: "DIALOG_KEY",
                Kind: LocalizationRecordKind.Pointer, Limit: 10))).Records);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new LocalizationRelationshipCoordinator().ResolveSelectedAsync(session, pointer.Identity, cancellation.Token));
        }
        finally { DeleteCache(fixture); }
    }

    private static async Task<LocalizationSession> OpenSessionAsync(ArchiveSnapshot snapshot, LocalizationFixture fixture)
    {
        var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
        await session.EnrichmentTask;
        return session;
    }

    private static async Task<ArchiveSnapshot> BuildLinkedSnapshotAsync(LocalizationFixture fixture, bool includeText)
    {
        var pointerPayload = MakePointerPayload(0xA5, 4091, 2);
        var rows = new List<byte[]>();
        if (includeText)
            rows.Add(LocalizationFixture.DirectText("DIALOG_KEY", "Localized line"));
        rows.Add(LocalizationFixture.PointerRecord(("DIALOG_KEY", 1, pointerPayload)));
        var localization = LocalizationFixture.MakeLocalization(6, ("ENG", rows.ToArray()));
        fixture.AddResource(MakeContainer([(4091u, 2u, (byte)0xA5)], localization));
        fixture.AddResource(MakeContainer([(1033u, 3u, (byte)0x11), (1031u, 4u, (byte)0x12)]));
        fixture.AddResource(MakeContainer([]));
        fixture.AddResource(MakeContainer([]));
        var original = await fixture.BuildAsync();
        return WithTypes(original, row => row.Ordinal switch
        {
            1 => row with { Type = 4091, Id = 2 },
            2 => row with { Type = 1033, Id = 3 },
            3 => row with { Type = 1031, Id = 4 },
            _ => row
        });
    }

    private static async Task<ArchiveSnapshot> BuildMissingSnapshotAsync(LocalizationFixture fixture)
    {
        var localization = LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.PointerRecord(("DIALOG_KEY", 1, MakePointerPayload(0xA5, 4091, 77)))]));
        fixture.AddResource(MakeContainer([(4091u, 77u, (byte)0xA5)], localization));
        fixture.AddResource(MakeContainer([]));
        var original = await fixture.BuildAsync();
        return WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 3 } : row);
    }

    private static async Task<ArchiveSnapshot> BuildAmbiguousSnapshotAsync(LocalizationFixture fixture)
    {
        var localization = LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.PointerRecord(("DIALOG_KEY", 1, MakePointerPayload(0xA5, 4091, 77)))]));
        fixture.AddResource(MakeContainer([(4091u, 77u, (byte)0xA5)], localization));
        fixture.AddResource(MakeContainer([]));
        fixture.AddResource(MakeContainer([]));
        var original = await fixture.BuildAsync();
        return WithTypes(original, row => row.Ordinal is 1 or 2 ? row with { Type = 4091, Id = 77 } : row);
    }

    private static ArchiveSnapshot WithTypes(ArchiveSnapshot snapshot, Func<ResourceRecord, ResourceRecord> transform) =>
        snapshot with { Records = snapshot.Records.Select(transform).ToArray() };

    private static byte[] MakePointerPayload(byte trailer, uint type, uint id)
    {
        var bytes = new byte[9];
        bytes[0] = trailer;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5, 4), id);
        return bytes;
    }

    private static byte[] MakeContainer((uint Type, uint Id, byte Trailer)[] references, byte[]? tail = null)
    {
        using var stream = new MemoryStream();
        stream.Write("COM_CONT"u8);
        WriteLittleEndian(stream, 6);
        WriteLittleEndian(stream, checked((uint)(4 + references.Length * 9 + (tail?.Length ?? 0))));
        WriteLittleEndian(stream, checked((uint)references.Length));
        foreach (var (type, id, trailer) in references)
        {
            WriteLittleEndian(stream, type);
            WriteLittleEndian(stream, id);
            stream.WriteByte(trailer);
        }
        if (tail is not null) stream.Write(tail);
        return stream.ToArray();
    }

    private static void WriteLittleEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void DeleteCache(LocalizationFixture fixture)
    {
        if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
    }

    private sealed class FrozenRevisionProvider : ISourceRevisionProvider
    {
        private readonly Dictionary<string, FileStamp> stamps;

        public FrozenRevisionProvider(ArchiveSnapshot snapshot)
        {
            stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
            {
                [snapshot.IndexPath] = snapshot.IndexStamp
            };
            foreach (var volume in snapshot.Volumes) stamps[volume.FilePath] = volume.Stamp;
        }

        public FileStamp Capture(string path) => stamps.TryGetValue(path, out var stamp) ? stamp : new(false, "", 0, 0, 0, false);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }
}
