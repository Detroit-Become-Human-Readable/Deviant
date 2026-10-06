using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationVersion14BlockTests
{
    [Fact]
    public void AddsRawBlocksWithoutMovingTheExistingTableBoundary()
    {
        var bytes = Fixture(0x202, 3, Tail(blobLength: 3, vectorCount: 2));
        var tables = Read(bytes, AnimationReadProfile.Version14Tables).Value!.Version14Layout!;
        var blocksResult = Read(bytes, AnimationReadProfile.Version14Blocks);
        var blocks = blocksResult.Value!.Version14Layout!;
        Assert.Equal(tables.RemainingResourceOffset, blocks.RemainingResourceOffset);
        Assert.Equal(tables.RemainingLength, blocks.RemainingLength);
        Assert.Empty(blocks.PostTableDiagnostics);
        Assert.Equal(new[] { "0x2", "0x200", "U32", "U8", "1024-A", "1024-B", "U32+skip" },
            blocks.PostTableBlocks.Select(b => b.Kind));
        Assert.Equal(3, blocks.PostTableBlocks[0].Length);
        Assert.Equal(36, blocks.PostTableBlocks[1].Length);
        Assert.Equal(new AnimationBlockField(0, 4, 2), blocks.PostTableBlocks[1].Fields[0]);
        Assert.Equal(1024, blocks.PostTableBlocks[4].Length);
        Assert.Equal(1024, blocks.PostTableBlocks[5].Length);
        Assert.Equal(3, blocks.PostTableRemainderLength);
        Assert.Equal(new byte[] { 0xF1, 0xF2, 0xF3 },
            blocksResult.Value.EncodedResource.Span[(int)blocks.PostTableRemainderResourceOffset!..].ToArray());
    }

    [Fact]
    public void OptionalByteAndDwordRemainStoredBits()
    {
        var tail = Tail(0, 0, option: true);
        var layout = Read(Fixture(0x2200, 0, tail), AnimationReadProfile.Version14Blocks).Value!.Version14Layout!;
        var optional = layout.PostTableBlocks[0];
        Assert.Equal("0x2000", optional.Kind);
        Assert.Equal(5, optional.Length);
        Assert.Equal(new AnimationBlockField(0, 1, 2), optional.Fields[0]);
        Assert.Equal(new AnimationBlockField(1, 4, 0x7FC01234), optional.Fields[1]);
    }

    [Fact]
    public void FinalSkipRecordsItsCountAndBytes()
    {
        var layout = Read(Fixture(0, 0, Tail(0, null, skip: 4, scalarWord: 1)), AnimationReadProfile.Version14Blocks).Value!.Version14Layout!;
        var final = layout.PostTableBlocks[^1];
        Assert.Equal("U32+skip", final.Kind);
        Assert.Equal(8, final.Length);
        Assert.Equal(4u, final.Fields[0].Bits);
        Assert.Equal(3, layout.PostTableRemainderLength);
    }

    [Fact]
    public void OversizedCountPreservesTablesAndStopsBeforeTheRegion()
    {
        var layout = Read(Fixture(0x200, 0, U32(uint.MaxValue)), AnimationReadProfile.Version14Blocks).Value!.Version14Layout!;
        Assert.Empty(layout.PostTableBlocks);
        Assert.Single(layout.PostTableDiagnostics);
        Assert.Equal(layout.RemainingResourceOffset, layout.PostTableRemainderResourceOffset);
    }

    [Fact]
    public void MissingScalarKeepsTheCapturedResource()
    {
        var bytes = Fixture(0, 0, [0, 0, 0, 0]);
        var decoded = Read(bytes, AnimationReadProfile.Version14Blocks);
        Assert.NotNull(decoded.Value!.Version14Layout);
        Assert.NotEmpty(decoded.Value.Version14Layout.PostTableDiagnostics);
        Assert.Equal(bytes, decoded.Value.EncodedResource.ToArray());
    }

    [Fact]
    public void VariableReferenceRegionStopsAtTheOriginalTailBoundary()
    {
        var layout = Read(Fixture(0x9000, 0, new byte[32]), AnimationReadProfile.Version14Blocks).Value!.Version14Layout!;
        Assert.Empty(layout.PostTableBlocks);
        Assert.Single(layout.PostTableDiagnostics);
        Assert.Equal(layout.RemainingResourceOffset, layout.PostTableRemainderResourceOffset);
    }

    [Fact]
    public void ConditionalSkipStateRemainsUnparsed()
    {
        var bytes = Fixture(0, 0, [.. U32(2), 1, .. new byte[32]]);
        var layout = Read(bytes, AnimationReadProfile.Version14Blocks).Value!.Version14Layout!;
        Assert.Equal(new[] { "U32", "U8" }, layout.PostTableBlocks.Select(b => b.Kind));
        Assert.Single(layout.PostTableDiagnostics);
        Assert.Equal(32, layout.PostTableRemainderLength);
    }

    [Fact]
    public void CancellationStopsBeforeDecoding()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var bytes = Fixture(0x200, 0, Tail(0, 2));
        Assert.Throws<OperationCanceledException>(() => new AnimationDataReader().Decode(bytes,
            new("index", 1), new("index", "revision"), new("volume.dat", 0, bytes.Length),
            AnimationReadProfile.Version14Blocks, cancellation.Token));
    }

    private static DecodeResult<AnimationDataDocument> Read(byte[] bytes, AnimationReadProfile profile) =>
        new AnimationDataReader().Decode(bytes, new("index", 1), new("index", "revision"),
            new("volume.dat", 128, bytes.Length), profile);

    private static byte[] Tail(int blobLength, uint? vectorCount, bool option = false, uint skip = 0, uint scalarWord = 2)
    {
        using var output = new MemoryStream();
        if (option) { output.WriteByte(2); output.Write(U32(0x7FC01234)); }
        output.Write(new byte[blobLength]);
        if (vectorCount is uint count) { output.Write(U32(count)); output.Write(new byte[count * 16]); }
        output.Write(U32(scalarWord)); output.WriteByte(0);
        if (scalarWord != 1) output.Write(new byte[2048]);
        output.Write(U32(skip)); output.Write(new byte[skip]);
        output.Write(new byte[] { 0xF1, 0xF2, 0xF3 }); return output.ToArray();
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes;
    }

    private static byte[] Fixture(uint flags, uint primaryCount, byte[] tail)
    {
        var body = new byte[28 + tail.Length];
        uint[] words = [2, 1, 1, flags, 7, primaryCount, 0];
        for (var i = 0; i < words.Length; i++) U32(words[i]).CopyTo(body, i * 4);
        tail.CopyTo(body, 28); using var output = new MemoryStream();
        Write("COM_CONT", 6, new byte[13]); Write("LOADCONT", 2, []); Write("ANIMDATA", 14, body);
        return output.ToArray();

        void Write(string tag, uint version, byte[] data)
        {
            output.Write(Encoding.ASCII.GetBytes(tag)); output.Write(U32(version)); output.Write(U32((uint)data.Length)); output.Write(data);
        }
    }
}
