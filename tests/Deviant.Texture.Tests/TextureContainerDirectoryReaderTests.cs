using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureContainerDirectoryReaderTests
{
    private static readonly ResourceIdentity Container = new(new string('a', 64), 17);
    private static readonly SourceRevision Revision = new(Container.IndexContentId, "revision");

    [Fact]
    public void MapsAllWrapperBranchesAndRetainsUnparsedTail()
    {
        var frames = new[] { Frame(0, 0, []), Frame(1, 2, new byte[5 + 4 + 24], external: 1),
            Frame(1, 2, new byte[5 + 48]), Frame(1, 1, new byte[5 + 34]) };
        var fixture = Fixture(frames, [(2137u, 101u), (2133u, 202u), (2133u, 303u), (2130u, 404u)], [0xAB, 0xCD]);
        var result = Parse(fixture);
        Assert.Empty(result.Diagnostics);
        var document = Assert.IsType<TextureContainerDirectoryDocument>(result.Value);
        Assert.Equal(4, document.Objects.Count);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, document.RemainderBytes.ToArray());
        Assert.Equal(2, document.RemainderExtent!.Length);
        Assert.Equal(new long[] { 13, 46, 66, 52 }, document.Objects.Select(item => item.HeaderExtent.Length));
        Assert.All(document.Objects, item => Assert.Equal(1u, item.ClassCount));
        Assert.Equal(202u, document.Objects[1].NativeId);
        Assert.Equal(fixture.Body, document.RawBody.ToArray());
    }

    [Fact]
    public void KeepsDuplicateTypedIdentifiersAmbiguous()
    {
        var fixture = Fixture([Frame(0, 0, []), Frame(0, 0, [])], [(2137u, 101u), (2137u, 101u)], []);
        var document = Assert.IsType<TextureContainerDirectoryDocument>(Parse(fixture).Value);
        var candidates = document.FindNative(2137, 101);
        Assert.Equal(2, candidates.Count);
        Assert.NotEqual(candidates[0].Identity, candidates[1].Identity);
        Assert.Empty(document.FindNative(2133, 101));
    }

    [Fact]
    public void RejectsTruncatedFrameAndDirectoryCountMismatch()
    {
        var fixture = Fixture([Frame(0, 0, [])], [(2137u, 101u)], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Body, 1024);
        Assert.Null(Parse(fixture).Value);
        fixture = Fixture([Frame(0, 0, [])], [(2137u, 101u)], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Prefix.AsSpan(20), 2);
        Assert.Null(Parse(fixture).Value);
    }

    [Fact]
    public void RejectsWrongVersionOriginAndOversizedWrapper()
    {
        var fixture = Fixture([Frame(1, 1, new byte[39])], [(2137u, 101u)], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Body.AsSpan(5), uint.MaxValue);
        Assert.Null(Parse(fixture).Value);
        fixture = Fixture([Frame(0, 0, [])], [(2137u, 101u)], []);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Prefix.AsSpan(fixture.Origin - 8), 22);
        Assert.Null(Parse(fixture).Value);
        fixture = Fixture([Frame(0, 0, [])], [(2137u, 101u)], []);
        var result = new TextureContainerDirectoryReader().Parse(fixture.Prefix, fixture.Body, Container,
            Revision, new(Container, fixture.Origin + 1, fixture.Body.Length), [new("source.dat", 0, 128)]);
        Assert.Null(result.Value);
    }

    private static DecodeResult<TextureContainerDirectoryDocument> Parse((byte[] Prefix, byte[] Body, int Origin) fixture) =>
        new TextureContainerDirectoryReader().Parse(fixture.Prefix, fixture.Body, Container, Revision,
            new(Container, fixture.Origin, fixture.Body.Length), [new("source.dat", 0, 128)]);

    private static byte[] Frame(byte flag, uint count, byte[] extra, byte external = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(16u); writer.Write(flag); writer.Write(count); writer.Write(0u);
        if (flag == 1)
        {
            extra[4] = external;
            writer.Write(extra);
        }
        writer.Write(1u); writer.Write("TEST_TAG"u8); writer.Write(1u);
        return stream.ToArray();
    }

    private static (byte[] Prefix, byte[] Body, int Origin) Fixture(byte[][] frames,
        (uint Type, uint Id)[] entries, byte[] tail)
    {
        var body = frames.SelectMany(item => item).Concat(tail).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(8u + (uint)entries.Length * 8);
        writer.Write(3u); writer.Write((uint)entries.Length);
        foreach (var entry in entries) { writer.Write(entry.Type); writer.Write(entry.Id); }
        writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length);
        return (stream.ToArray(), body, checked((int)stream.Length));
    }
}
