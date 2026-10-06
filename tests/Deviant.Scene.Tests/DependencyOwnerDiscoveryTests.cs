using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class DependencyOwnerDiscoveryTests
{
    [Fact]
    public void FindsPagedOwnersAndPreservesSourceBackedMatches()
    {
        var archive = Archive((29, 7), (40, 8), (29, 9));
        var bytes = Dependencies(archive,
        [
            [new(3, 895, 1, 2, 3), new(5, 42, 4, 5, 6)],
            [new(3, 1910, 7, 8, 9)],
            [new(3, 1920, 10, 11, 12)]
        ]);
        var reader = new SceneDependencyReader();

        var first = reader.FindOwners(bytes, archive, "sample.dep", new HashSet<uint> { 3 }, maximumOwners: 1);
        Assert.Single(first.Owners);
        Assert.Equal(0, first.Owners[0].Owner.Ordinal);
        Assert.Equal((uint)29, first.Owners[0].OwnerType);
        Assert.Equal((uint)7, first.Owners[0].OwnerId);
        Assert.Equal(1, first.Owners[0].TotalMatchingReferences);
        Assert.False(first.Owners[0].MatchingReferencesTruncated);
        Assert.Equal(new DependencyWords(3, 895, 1, 2, 3), first.Owners[0].References[0].Words);
        Assert.Equal(28, first.Owners[0].OwnerExtent.Offset);
        Assert.Equal(56, first.Owners[0].OwnerExtent.Length);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.NextOrdinal);
        Assert.Equal(bytes.Length, first.DependencyExtent.Length);
        Assert.NotNull(first.DependencyExtent.PayloadSha256);

        var second = reader.FindOwners(bytes, archive, "sample.dep", new HashSet<uint> { 3 },
            offsetOrdinal: first.NextOrdinal!.Value, maximumOwners: 1);
        Assert.Equal(1, second.Owners[0].Owner.Ordinal);
        Assert.True(second.HasMore);
        Assert.Equal(2, second.NextOrdinal);
    }

    [Fact]
    public void ValidatesOwnersAfterTheRequestedPage()
    {
        var archive = Archive((29, 7), (40, 8), (29, 9));
        var bytes = Dependencies(archive, [
            [new(3, 895, 1, 2, 3)],
            [],
            [new(3, 1920, 10, 11, 12)]
        ]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28 + 16 + 20 + 16), 999);

        var error = Assert.Throws<ArchiveException>(() => new SceneDependencyReader().FindOwners(
            bytes, archive, "sample.dep", new HashSet<uint> { 3 }, maximumOwners: 1));
        Assert.Equal("DependencyOwnerKeyMismatch", error.Code);
    }

    [Fact]
    public void CapsPerOwnerMatchesAndReportsTruncation()
    {
        var archive = Archive((29, 7));
        var bytes = Dependencies(archive, [[
            new(3, 1, 0, 0, 0), new(3, 2, 0, 0, 0), new(3, 3, 0, 0, 0)
        ]]);

        var page = new SceneDependencyReader().FindOwners(bytes, archive, "sample.dep",
            new HashSet<uint> { 3 }, maximumMatchingReferencesPerOwner: 2);

        var owner = Assert.Single(page.Owners);
        Assert.Equal(3, owner.TotalMatchingReferences);
        Assert.True(owner.MatchingReferencesTruncated);
        Assert.Equal(2, owner.References.Count);
    }

    [Fact]
    public void HonorsCancellationDuringOwnerWalk()
    {
        var archive = Archive((29, 7));
        var bytes = Dependencies(archive, [[]]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SceneDependencyReader().FindOwners(
            bytes, archive, "sample.dep", new HashSet<uint> { 3 }, token: cancellation.Token));
    }

    [Fact]
    public void RejectsOversizedInputBeforeOwnerDiscovery()
    {
        var archive = Archive((29, 7));
        var bytes = new byte[29];
        IndexLayout.Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 18);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 1);
        var error = Assert.Throws<ArchiveException>(() => new SceneDependencyReader().FindOwners(
            bytes, archive, "sample.dep", new HashSet<uint> { 3 }, limits: new(MaximumBytes: 28)));
        Assert.Equal("DependencyByteLimit", error.Code);
    }

    private static ArchiveSnapshot Archive(params (uint Type, uint Id)[] keys)
    {
        var stamp = new FileStamp(true, "file-a", 0, 0, 0, true);
        return new("sample.idx", "index-a", 18, 0, [], (uint)keys.Length,
            keys.Select((key, ordinal) => new ResourceRecord(ordinal, key.Type, 1, key.Id, 0, 0, 0, 0, "sample.dat")).ToArray(),
            [], stamp, "revision-a", []);
    }

    private static byte[] Dependencies(ArchiveSnapshot archive, IReadOnlyList<IReadOnlyList<DependencyWords>> references)
    {
        using var stream = new MemoryStream();
        stream.Write(IndexLayout.Magic); Write(stream, 18); Write(stream, (uint)archive.Records.Count);
        foreach (var row in archive.Records)
        {
            Write(stream, row.Type); Write(stream, row.Flags); Write(stream, row.Id);
            var ownerReferences = references[(int)row.Ordinal]; Write(stream, (uint)ownerReferences.Count);
            foreach (var reference in ownerReferences)
            {
                Write(stream, reference.Word0); Write(stream, reference.Word1); Write(stream, reference.Word2);
                Write(stream, reference.Word3); Write(stream, reference.Word4);
            }
        }
        return stream.ToArray();
    }

    private static void Write(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes);
    }
}
