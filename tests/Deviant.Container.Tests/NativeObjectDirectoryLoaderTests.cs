using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Xunit;

namespace Deviant.Container.Tests;

public sealed class NativeObjectDirectoryLoaderTests
{
    private static readonly ResourceIdentity Identity = new("generated-container", 0);
    private static readonly SourceRevision Revision = new(Identity.IndexContentId, "generated-source");

    [Fact]
    public void ReadsDirectQzipFramingWithoutCopyingBodyAndKeepsSourceProvenance()
    {
        var bytes = Packet(); var extent = Source(bytes);
        var result = new NativeObjectDirectoryLoader().ReadDirect(bytes, Identity, Revision, extent);
        var directory = Assert.IsType<NativeObjectDirectoryDocument>(result.Value);
        Assert.Empty(result.Diagnostics); Assert.Single(directory.Objects);
        Assert.Equal(48, directory.BodyExtent.Offset);
        Assert.Equal(new byte[] { 7, 8, 9 }, directory.RemainderBytes.ToArray());
        Assert.Equal(extent, Assert.Single(result.Extents));
        Assert.Contains(result.Steps, step => step.Version == NativeObjectDirectoryLoader.DirectVersion && step.Options.Contains("skip5"));
        Assert.True(MemoryMarshal.TryGetArray(directory.RawBody, out var body));
        Assert.Same(bytes, body.Array); Assert.Equal(5 + directory.BodyExtent.Offset, body.Offset);
        Assert.Equal(directory.RemainderBytes.ToArray(), bytes.AsSpan(checked((int)(5 + directory.RemainderExtent!.Offset)), 3).ToArray());
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("directory-length")]
    [InlineData("body-length")]
    [InlineData("data-version")]
    [InlineData("truncate")]
    [InlineData("trailing")]
    public void RejectsInvalidDirectFraming(string defect)
    {
        var bytes = Packet();
        switch (defect)
        {
            case "signature": bytes[0] = 0; break;
            case "directory-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), uint.MaxValue); break;
            case "body-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(49), uint.MaxValue); break;
            case "data-version": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(45), 22); break;
            case "trailing": bytes = bytes.Concat(new byte[] { 1 }).ToArray(); break;
            default: bytes = bytes[..^1]; break;
        }
        var result = new NativeObjectDirectoryLoader().ReadDirect(bytes, Identity, Revision, Source(bytes));
        Assert.Null(result.Value); Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RejectsChangedSourceHashAndHonorsCancellation()
    {
        var bytes = Packet(); var extent = Source(bytes); bytes[^1] ^= 1;
        var result = new NativeObjectDirectoryLoader().ReadDirect(bytes, Identity, Revision, extent);
        Assert.Null(result.Value); Assert.Contains("NativeDirectSourceHashMismatch", result.Diagnostics);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeObjectDirectoryLoader().ReadDirect(
            bytes, Identity, Revision, Source(bytes), cancelled.Token));
    }

    private static SourceExtent Source(byte[] bytes) => new("generated.dat", 100, bytes.Length,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    private static byte[] Packet()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("QZIP\0"u8); writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(16u); writer.Write(3u); writer.Write(1u);
        writer.Write(2002u); writer.Write(10u); writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write(32u);
        writer.Write(16u); writer.Write((byte)0); writer.Write(0u); writer.Write(0u);
        writer.Write(1u); writer.Write("TEST_TAG"u8); writer.Write(1u); writer.Write(new byte[] { 7, 8, 9 });
        return stream.ToArray();
    }
}
