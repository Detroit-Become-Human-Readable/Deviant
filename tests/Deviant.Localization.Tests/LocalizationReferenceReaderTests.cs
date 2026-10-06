using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationReferenceReaderTests
{
    [Fact]
    public async Task ReadsBoundedRowsWithHashesExtentsTrailerAndDuplicateOccurrences()
    {
        using var fixture = new LocalizationFixture();
        var sourceBytes = MakeContainer((1033, 2, (byte)0xA5), (1033, 2, (byte)0xA6));
        fixture.AddResource(sourceBytes);
        fixture.AddResource(MakeContainer());
        var original = await fixture.BuildAsync();
        var snapshot = WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 2 } : row);
        var source = new ResourceIdentity(snapshot.IndexContentId, 0);

        var result = await new LocalizationReferenceReader().ReadAsync(snapshot, source);

        Assert.Equal(LocalizationReferenceReadStatus.Verified, result.Status);
        Assert.Equal(new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), result.Revision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), result.ReferenceTableSha256);
        Assert.Equal(2, result.Relationships.Count);
        Assert.All(result.Relationships, relationship =>
        {
            Assert.Equal(ReferenceStatus.Unique, relationship.Resolution.Status);
            Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 1), relationship.NavigationTarget);
            Assert.Equal(9, relationship.Evidence.PhysicalExtent?.Length);
            Assert.NotNull(relationship.Evidence.PhysicalExtent?.PayloadSha256);
        });
        Assert.Equal((byte)0xA5, result.Relationships[0].Evidence.SourceReferenceTrailer);
        Assert.Equal((byte)0xA6, result.Relationships[1].Evidence.SourceReferenceTrailer);
        Assert.Equal(20, result.Relationships[0].Evidence.PhysicalExtent!.Offset);
        Assert.Equal(20 + 9, result.Relationships[1].Evidence.PhysicalExtent!.Offset);
        Assert.NotEqual(result.Relationships[0].Evidence.PhysicalExtent!.PayloadSha256,
            result.Relationships[1].Evidence.PhysicalExtent!.PayloadSha256);
    }

    [Fact]
    public async Task ConcurrentReadsReuseOneSnapshotLookupAndANewSnapshotBuildsItsOwnLookup()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((1033, 2, (byte)1)));
        fixture.AddResource(MakeContainer());
        fixture.AddResource(MakeContainer());
        var original = await fixture.BuildAsync();
        var snapshot = WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 2 } : row);
        var countedRows = new CountingResourceList(snapshot.Records);
        var countedSnapshot = snapshot with { Records = countedRows };
        var reader = new LocalizationReferenceReader();
        var source = new ResourceIdentity(snapshot.IndexContentId, 0);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => reader.ReadAsync(countedSnapshot, source)));
        Assert.All(concurrent, result => Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 1),
            Assert.Single(result.Relationships).NavigationTarget));
        Assert.Equal(1, countedRows.EnumerationCount);
        await reader.ReadAsync(countedSnapshot, source);
        Assert.Equal(1, countedRows.EnumerationCount);

        var changedRows = snapshot.Records.Select(row => row.Ordinal == 1 ? row with { Type = 1031, Id = 2 } : row).ToArray();
        var newSnapshotRows = new CountingResourceList(changedRows);
        var newSnapshot = snapshot with { Records = newSnapshotRows };
        var afterSnapshotChange = await reader.ReadAsync(newSnapshot, new ResourceIdentity(newSnapshot.IndexContentId, 0));
        Assert.Equal(ReferenceStatus.Missing, Assert.Single(afterSnapshotChange.Relationships).Resolution.Status);
        Assert.Equal(1, newSnapshotRows.EnumerationCount);

        var foreign = await Assert.ThrowsAsync<ArchiveException>(() => reader.ReadAsync(newSnapshot,
            new ResourceIdentity("another-index", 0)));
        Assert.Equal("InvalidReferenceSource", foreign.Code);
    }

    [Fact]
    public async Task CancelledCandidateLookupBuildCanBeRetried()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((1033, 2, (byte)1)));
        fixture.AddResource(MakeContainer());
        var original = await fixture.BuildAsync();
        var snapshot = WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 2 } : row);
        using var cancellation = new CancellationTokenSource();
        var rows = new CancelOnFirstEnumeration(snapshot.Records, cancellation);
        var cancellableSnapshot = snapshot with { Records = rows };
        var reader = new LocalizationReferenceReader();
        var source = new ResourceIdentity(snapshot.IndexContentId, 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellableSnapshot, source, cancellation.Token));
        var retried = await reader.ReadAsync(cancellableSnapshot, source);

        Assert.Equal(ReferenceStatus.Unique, Assert.Single(retried.Relationships).Resolution.Status);
        Assert.Equal(2, rows.EnumerationCount);
    }

    [Fact]
    public async Task ReadsSmallReferenceTableFromResourceLargerThanSixteenMiB()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((1033, 2, (byte)1)));
        fixture.AddResource(MakeContainer());
        _ = await fixture.BuildAsync();
        var largeSize = 16 * 1024 * 1024 + 128;
        using (var volume = new FileStream(fixture.VolumePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            volume.SetLength(largeSize + MakeContainer().Length);
        var indexBytes = await File.ReadAllBytesAsync(fixture.IndexPath);
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes.AsSpan(IndexLayout.HeaderLength + 16, 4), checked((uint)largeSize));
        await File.WriteAllBytesAsync(fixture.IndexPath, indexBytes);
        var indexed = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var snapshot = WithTypes(indexed, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 2 } : row);

        var result = await new LocalizationReferenceReader().ReadAsync(snapshot,
            new ResourceIdentity(snapshot.IndexContentId, 0));

        Assert.Equal(LocalizationReferenceReadStatus.Verified, result.Status);
        Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 1), Assert.Single(result.Relationships).NavigationTarget);
    }

    [Fact]
    public async Task ResourceOrdinalLookupFallsBackWhenSnapshotRowsAreNotOrdinalIndexed()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((1033, 2, (byte)1)));
        fixture.AddResource(MakeContainer());
        var original = await fixture.BuildAsync();
        var typed = WithTypes(original, row => row.Ordinal == 1 ? row with { Type = 1033, Id = 2 } : row);
        var reordered = typed with { Records = [typed.Records[1], typed.Records[0]] };
        var reader = new LocalizationReferenceReader();
        var result = await reader.ReadAsync(reordered, new ResourceIdentity(reordered.IndexContentId, 0));

        Assert.Equal(new ResourceIdentity(reordered.IndexContentId, 1), Assert.Single(result.Relationships).NavigationTarget);
        await reader.ValidateReferenceTableHashAsync(reordered, result);
    }

    [Fact]
    public async Task ReportsMissingAmbiguousAndUniqueTargetsSeparately()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((1033, 77, (byte)1), (1033, 88, (byte)2), (1033, 99, (byte)3)));
        fixture.AddResource(MakeContainer());
        fixture.AddResource(MakeContainer());
        fixture.AddResource(MakeContainer());
        var original = await fixture.BuildAsync();
        var snapshot = WithTypes(original, row => row.Ordinal switch
        {
            1 or 2 => row with { Type = 1033, Id = 88 },
            3 => row with { Type = 1033, Id = 99 },
            _ => row
        });

        var reader = new LocalizationReferenceReader();
        var result = await reader.ReadAsync(snapshot, new ResourceIdentity(snapshot.IndexContentId, 0));

        Assert.Collection(result.Relationships,
            missing => Assert.Equal(ReferenceStatus.Missing, missing.Resolution.Status),
            ambiguous =>
            {
                Assert.Equal(ReferenceStatus.Ambiguous, ambiguous.Resolution.Status);
                Assert.Equal(2, ambiguous.Resolution.Candidates.Count);
                Assert.Null(ambiguous.NavigationTarget);
            },
            unique => Assert.Equal(new ResourceIdentity(snapshot.IndexContentId, 3), unique.NavigationTarget));

        var foreign = await Assert.ThrowsAsync<ArchiveException>(() => reader.ReadAsync(snapshot, new ResourceIdentity("other-index", 0)));
        Assert.Equal("InvalidReferenceSource", foreign.Code);
    }

    [Fact]
    public async Task RejectsChangedSourceAndOverLimitContainers()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(MakeContainer((4091, 3, (byte)1)));
        var snapshot = await fixture.BuildAsync();
        var changed = await File.ReadAllBytesAsync(fixture.VolumePath);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.VolumePath, changed);

        var sourceChanged = await Assert.ThrowsAsync<ArchiveException>(() =>
            new LocalizationReferenceReader().ReadAsync(snapshot, new ResourceIdentity(snapshot.IndexContentId, 0)));
        Assert.Equal("SourceChanged", sourceChanged.Code);

        using var limitFixture = new LocalizationFixture();
        limitFixture.AddResource(MakeContainerHeader(4097, 4 + 4097 * 9));
        var limitSnapshot = await limitFixture.BuildAsync();
        var limitError = await Assert.ThrowsAsync<ArchiveException>(() =>
            new LocalizationReferenceReader().ReadAsync(limitSnapshot, new ResourceIdentity(limitSnapshot.IndexContentId, 0)));
        Assert.Equal("ReferenceCountLimit", limitError.Code);
    }

    [Fact]
    public async Task NonContainerResourcesReturnAnEmptyReferenceResult()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(new byte[24]);
        var snapshot = await fixture.BuildAsync();

        var result = await new LocalizationReferenceReader().ReadAsync(snapshot,
            new ResourceIdentity(snapshot.IndexContentId, 0));

        Assert.Equal(LocalizationReferenceReadStatus.NotContainer, result.Status);
        Assert.Empty(result.Relationships);
        Assert.Null(result.ReferenceTableSha256);
    }

    private static ArchiveSnapshot WithTypes(ArchiveSnapshot snapshot, Func<ResourceRecord, ResourceRecord> update) =>
        snapshot with { Records = snapshot.Records.Select(update).ToArray() };

    private static byte[] MakeContainer(params (uint Type, uint Id, byte Trailer)[] references) =>
        MakeContainerHeader((uint)references.Length, checked((uint)(4 + references.Length * 9)), references);

    private static byte[] MakeContainerHeader(uint count, uint payloadLength,
        params (uint Type, uint Id, byte Trailer)[] references)
    {
        using var stream = new MemoryStream();
        stream.Write("COM_CONT"u8);
        WriteLittleEndian(stream, 6);
        WriteLittleEndian(stream, payloadLength);
        WriteLittleEndian(stream, count);
        foreach (var (type, id, trailer) in references)
        {
            WriteLittleEndian(stream, type);
            WriteLittleEndian(stream, id);
            stream.WriteByte(trailer);
        }
        return stream.ToArray();
    }

    private static void WriteLittleEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class CountingResourceList(IReadOnlyList<ResourceRecord> rows) : IReadOnlyList<ResourceRecord>
    {
        private int enumerationCount;
        public int EnumerationCount => Volatile.Read(ref enumerationCount);
        public int Count => rows.Count;
        public ResourceRecord this[int index] => rows[index];
        public IEnumerator<ResourceRecord> GetEnumerator()
        {
            Interlocked.Increment(ref enumerationCount);
            return rows.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelOnFirstEnumeration(IReadOnlyList<ResourceRecord> rows, CancellationTokenSource cancellation)
        : IReadOnlyList<ResourceRecord>
    {
        private int enumerationCount;
        public int EnumerationCount => Volatile.Read(ref enumerationCount);
        public int Count => rows.Count;
        public ResourceRecord this[int index] => rows[index];
        public IEnumerator<ResourceRecord> GetEnumerator()
        {
            if (Interlocked.Increment(ref enumerationCount) == 1) cancellation.Cancel();
            return rows.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
