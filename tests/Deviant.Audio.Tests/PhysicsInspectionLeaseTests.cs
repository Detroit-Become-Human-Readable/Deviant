using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class PhysicsInspectionLeaseTests
{
    private const uint ContainerResourceId = 701;
    private const uint NativeObjectId = 37;

    [Fact]
    public async Task OpenOwnsBorrowedContainerAndReleasesAllBudgetOnDispose()
    {
        using var fixture = CreateFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();

        var lease = await PhysicsInspectionLease.OpenAsync(snapshot, Member(snapshot), budget);

        Assert.Equal(new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), lease.Revision);
        Assert.NotNull(lease.Result.Envelope.Value);
        Assert.NotNull(lease.Result.IndexedMetadata?.Value);
        Assert.Contains(lease.Result.Envelope.Steps, step => step.Decoder == "NativeObjectDirectoryLoader");
        Assert.Contains(lease.Result.Envelope.Steps, step => step.Decoder == "NativeObjectDirectoryReader");
        Assert.Equal(ContainerRow(snapshot).VolumeName, Assert.Single(lease.Result.Envelope.Extents).RelativeSource);
        Assert.True(budget.UsedBytes >= 8L * 1024 * 1024);
        lease.EnsureUnchanged();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Result);
        Assert.Throws<ObjectDisposedException>(() => lease.EnsureUnchanged());
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task BorrowedLeaseKeepsResultAliveAfterOriginalOwnerDisposes()
    {
        using var fixture = CreateFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var row = ContainerRow(snapshot);
        var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var lease = PhysicsInspectionLease.FromContainer(owner, Member(snapshot));

        owner.Dispose();
        Assert.NotNull(lease.Result.IndexedMetadata?.Value);
        lease.EnsureUnchanged();
        lease.Dispose();

        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task RejectsFabricatedWrongTypeAndOtherRevisionMembersWithoutLeaking()
    {
        using var fixture = CreateFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var row = ContainerRow(snapshot);
        using var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var member = Member(snapshot);
        var baseline = budget.UsedBytes;

        Assert.Throws<ArchiveException>(() => PhysicsInspectionLease.FromContainer(owner,
            member with { MemberPath = "dc-object/0/2201/999" }));
        Assert.Throws<ArchiveException>(() => PhysicsInspectionLease.FromContainer(owner,
            member with { MemberPath = "dc-object/0/2229/37" }));
        Assert.Throws<ArchiveException>(() => PhysicsInspectionLease.FromContainer(owner,
            member with { IndexContentId = "another-source-revision" }));

        Assert.Equal(baseline, budget.UsedBytes);
        owner.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndBudgetRejectionReleaseBorrowedOwnership()
    {
        using var fixture = CreateFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var row = ContainerRow(snapshot);
        var owner = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, row.Ordinal), budget);
        var member = Member(snapshot);
        var ownerBytes = budget.UsedBytes;

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => PhysicsInspectionLease.FromContainer(owner, member, cancelled.Token));
        }
        Assert.Equal(ownerBytes, budget.UsedBytes);

        var blocker = budget.Reserve(budget.MaximumBytes - budget.UsedBytes, "synthetic budget pressure");
        try
        {
            Assert.Throws<ArchiveException>(() => PhysicsInspectionLease.FromContainer(owner, member));
        }
        finally { blocker.Dispose(); }

        owner.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    private static ResourceRecord ContainerRow(ArchiveSnapshot snapshot) =>
        Assert.Single(snapshot.Records, record => record.Type == 29);

    private static ResourceIdentity Member(ArchiveSnapshot snapshot) =>
        new(snapshot.IndexContentId, ContainerRow(snapshot).Ordinal, $"dc-object/0/2201/{NativeObjectId}");

    private static FormatsFixtureBuilder CreateFixture()
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(29, ContainerResourceId, QzipContainer(2201, NativeObjectId, PhysicsPayload(CompleteEmbedded())));
        fixture.WriteIndex();
        return fixture;
    }

    private static byte[] QzipContainer(uint nativeType, uint nativeId, byte[] payload)
    {
        var frame = new byte[13 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 13);

        using var stream = new MemoryStream();
        stream.Write("QZIP\0"u8);
        stream.Write("DC_INFO "u8);
        WriteUInt32(stream, 3);
        WriteUInt32(stream, 16);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, nativeType);
        WriteUInt32(stream, nativeId);
        stream.Write("DC_DATA "u8);
        WriteUInt32(stream, 23);
        WriteUInt32(stream, (uint)frame.Length);
        stream.Write(frame);
        return stream.ToArray();
    }

    private static byte[] PhysicsPayload(byte[] embedded)
    {
        var payload = new byte[20 + embedded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        "PHYSYS__"u8.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), (uint)embedded.Length);
        embedded.CopyTo(payload, 20);
        return payload;
    }

    private static byte[] CompleteEmbedded()
    {
        var type = Chunk("TYPE", 0, Join(
            Chunk("TSTR", 1, Encoding.ASCII.GetBytes("Root\0Child\0Leaf\0")),
            Chunk("TNA1", 1, [4, 0, 0, 1, 0, 2, 0]),
            Chunk("FSTR", 1, Encoding.ASCII.GetBytes("member\0")),
            Chunk("TBDY", 1, [1, 0, 0, 2, 0, 0, 3, 0, 0])));
        var index = Chunk("INDX", 0, Join(
            Chunk("ITEM", 1, Join(Row(0x2a000003, 17, 1), Row(0x1b000001, 12, uint.MaxValue),
                Row(0x20000002, 99, 0), Row(0x0f000005, 64, 1))),
            Chunk("PTCH", 1, [2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0])));
        return Chunk("TAG0", 0, Join(Chunk("DATA", 1, new byte[128]), type, index));
    }

    private static byte[] Chunk(string tag, byte flags, byte[] body)
    {
        var bytes = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)flags << 30) | (uint)bytes.Length);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 4);
        body.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Row(uint word0, uint word1, uint word2)
    {
        var row = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(row, word0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), word1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), word2);
        return row;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
