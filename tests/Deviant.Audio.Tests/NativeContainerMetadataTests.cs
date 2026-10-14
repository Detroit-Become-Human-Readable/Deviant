using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Containers;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeContainerMetadataTests
{
    private const int RemainderLength = 8 * 1024 * 1024;

    [Theory]
    [InlineData("direct")]
    [InlineData("literal")]
    [InlineData("zlib")]
    [InlineData("straddle")]
    [InlineData("wrapped-straddle")]
    public async Task MetadataReadsStopAtTheLastObjectAndPreserveFullLogicalExtents(string profile)
    {
        var packet = Packet(profile);
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(29, 1, packet.Stored); fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var identity = new ResourceIdentity(snapshot.IndexContentId, 0);
        var budget = new AssetMemoryBudget(24 * 1024 * 1024);
        using (var metadata = await NativeContainerLease.OpenMetadataAsync(snapshot, identity, budget))
        {
            Assert.True(metadata.IsMetadataOnly); Assert.True(metadata.Directory.IsMetadataOnly);
            Assert.Single(metadata.Directory.Objects);
            Assert.Equal(packet.MetadataLength - 48, metadata.Directory.RawBody.Length);
            Assert.Equal(packet.MetadataLength - 48 + RemainderLength, metadata.Directory.BodyExtent.Length);
            Assert.Empty(metadata.Directory.RemainderBytes.ToArray()); Assert.Null(metadata.Directory.RemainderExtent);
            Assert.Equal(RemainderLength, metadata.Directory.DeclaredRemainderExtent!.Length);
            Assert.Equal(packet.MetadataLength, metadata.Directory.DeclaredRemainderExtent.Offset);
            var extent = Assert.Single(metadata.PhysicalExtents);
            Assert.Equal(packet.ReadLength, extent.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(packet.Stored.AsSpan(0, packet.ReadLength))).ToLowerInvariant(), extent.PayloadSha256);
            Assert.Equal(metadata.SourceExtents, metadata.PhysicalExtents);
            Assert.True(extent.Length < packet.Stored.Length / 100);
            Assert.Equal(64, metadata.Directory.Objects[0].RawPayload.Length);
            Assert.Throws<InvalidOperationException>(() => metadata.StoredSource);
            Assert.Throws<InvalidOperationException>(metadata.RequireDataBuffers);
            Assert.Equal(0, metadata.DataLayout.RawSuffixLength);
            metadata.EnsureUnchanged();
        }
        Assert.Equal(0, budget.UsedBytes);
        Assert.True(budget.PeakBytes < RemainderLength);
        using (var full = await NativeContainerLease.OpenAsync(snapshot, identity, new AssetMemoryBudget(128 * 1024 * 1024)))
        {
            Assert.False(full.IsMetadataOnly); Assert.False(full.Directory.IsMetadataOnly);
            Assert.Equal(RemainderLength, full.Directory.RemainderBytes.Length);
            Assert.Null(full.Directory.DeclaredRemainderExtent);
            Assert.Equal(packet.Stored.Length, full.StoredSource.OriginalBytes.Length);
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("payload")]
    [InlineData("count")]
    [InlineData("wrapper")]
    [InlineData("segs-span")]
    [InlineData("segs-version")]
    public async Task InvalidDeclaredFramingFailsAndReleasesTheSharedBudget(string defect)
    {
        var packet = Packet(defect.StartsWith("segs",StringComparison.Ordinal) ? "literal" : "direct");
        switch(defect)
        {
            case "body": BinaryPrimitives.WriteUInt32LittleEndian(packet.Stored.AsSpan(49),uint.MaxValue); break;
            case "payload": BinaryPrimitives.WriteUInt32LittleEndian(packet.Stored.AsSpan(53),uint.MaxValue); break;
            case "count": BinaryPrimitives.WriteUInt32LittleEndian(packet.Stored.AsSpan(25),2); break;
            case "wrapper": packet.Stored[57]=1; BinaryPrimitives.WriteUInt32LittleEndian(packet.Stored.AsSpan(58),4097); break;
            case "segs-span": BinaryPrimitives.WriteUInt32LittleEndian(packet.Stored.AsSpan(12),uint.MaxValue); break;
            case "segs-version": BinaryPrimitives.WriteUInt16LittleEndian(packet.Stored.AsSpan(4),2); break;
        }
        using var fixture=FormatsFixtureBuilder.CreateEmpty(); fixture.AddResource(29,1,packet.Stored);fixture.WriteIndex();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);var budget=new AssetMemoryBudget();
        if (defect == "wrapper")
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => NativeContainerLease.OpenMetadataAsync(
                snapshot, new(snapshot.IndexContentId, 0), budget));
            Assert.Equal("DirectoryWrapperEntryLimit", error.Message);
        }
        else
            await Assert.ThrowsAsync<ArchiveException>(() => NativeContainerLease.OpenMetadataAsync(
                snapshot, new(snapshot.IndexContentId, 0), budget));
        Assert.Equal(0,budget.UsedBytes);
    }

    [Fact]
    public async Task BorrowedMetadataLeaseRetainsSourcePinsAndReleasesOnce()
    {
        var packet=Packet("direct");using var fixture=FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(29,1,packet.Stored);fixture.WriteIndex();var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget=new AssetMemoryBudget();var owner=await NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,0),budget);
        var borrowed=owner.AcquireLease();owner.Dispose();owner.EnsureUnchanged();Assert.Single(owner.Directory.Objects);
        Assert.True(budget.UsedBytes>0);borrowed.Dispose();borrowed.Dispose();Assert.Equal(0,budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(()=>owner.Directory);
    }

    [Fact]
    public async Task ChangedSourceAndPreCanceledLoadLeaveNoReservations()
    {
        var packet=Packet("direct");using var fixture=FormatsFixtureBuilder.CreateEmpty();fixture.AddResource(29,1,packet.Stored);fixture.WriteIndex();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);var budget=new AssetMemoryBudget();
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,0),budget,canceled.Token));
        Assert.Equal(0,budget.UsedBytes);
        File.AppendAllText(snapshot.Volumes[0].FilePath,"changed");
        await Assert.ThrowsAsync<ArchiveException>(()=>NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,0),budget));
        Assert.Equal(0,budget.UsedBytes);
    }

    [Fact]
    public void DistinctMetadataParserRejectsTruncatedObjectsAndFullParserRejectsPartialBodies()
    {
        var packet=Packet("direct");var identity=new ResourceIdentity("generated-index",0);var revision=new SourceRevision(identity.IndexContentId,"generated-source");
        var bytes=packet.Stored.AsMemory(0,packet.ReadLength);var extent=new SourceExtent("generated.dat",0,bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
        var loader=new NativeObjectDirectoryLoader();
        Assert.True(loader.ReadDirectMetadata(bytes,packet.Stored.Length,identity,revision,extent).Value!.IsMetadataOnly);
        Assert.Null(loader.ReadDirect(bytes,identity,revision,extent).Value);
        var truncated=bytes[..^1];var truncatedExtent=extent with {Length=truncated.Length,PayloadSha256=Convert.ToHexString(SHA256.HashData(truncated.Span)).ToLowerInvariant()};
        Assert.Null(loader.ReadDirectMetadata(truncated,packet.Stored.Length,identity,revision,truncatedExtent).Value);
        Assert.Null(loader.ReadDirectMetadata(bytes,packet.Stored.Length-1,identity,revision,extent).Value);
    }

    private sealed record FixturePacket(byte[] Stored,int MetadataLength,int ReadLength);
    private static FixturePacket Packet(string profile)
    {
        bool wrapped = profile == "wrapped-straddle";
        using var prefix=new MemoryStream();using(var w=new BinaryWriter(prefix,System.Text.Encoding.UTF8,true))
        {
            w.Write("DC_INFO "u8);w.Write(3u);w.Write(16u);w.Write(0u);w.Write(1u);w.Write(2013u);w.Write(1u);
            w.Write("DC_DATA "u8);w.Write(23u);w.Write((uint)((wrapped?46:13)+64+RemainderLength));
            w.Write(64u);w.Write(wrapped?(byte)1:(byte)0);w.Write(wrapped?2u:0u);w.Write(0u);
            if(wrapped){w.Write(0u);w.Write((byte)1);w.Write(new byte[28]);}
            w.Write(new byte[64]);
        }
        var metadata=prefix.ToArray();var tail=new byte[RemainderLength];tail.AsSpan().Fill(0xCC);
        if(profile=="direct")return new("QZIP\0"u8.ToArray().Concat(metadata).Concat(tail).ToArray(),metadata.Length,metadata.Length+5);
        int prefixLength=profile is "straddle" or "wrapped-straddle"?100:metadata.Length;var decoded=metadata.AsSpan(0,prefixLength).ToArray();
        byte[] storedPrefix;
        bool compressed=profile is "zlib" or "straddle" or "wrapped-straddle";
        if(compressed)
        {
            using var output=new MemoryStream();using(var z=new ZLibStream(output,CompressionLevel.SmallestSize,true))z.Write(decoded);
            storedPrefix=output.ToArray();
        }
        else storedPrefix=decoded;
        using var stream=new MemoryStream();using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
        {
            w.Write("segs"u8);w.Write((ushort)1);w.Write((ushort)1);w.Write((uint)decoded.Length);w.Write((uint)storedPrefix.Length);
            w.Write((ushort)storedPrefix.Length);w.Write((ushort)decoded.Length);w.Write(compressed?1u:0u);w.Write(new byte[8]);w.Write(storedPrefix);
            w.Write(metadata.AsSpan(prefixLength));w.Write(tail);
        }
        return new(stream.ToArray(),metadata.Length,32+storedPrefix.Length+metadata.Length-prefixLength);
    }
}
