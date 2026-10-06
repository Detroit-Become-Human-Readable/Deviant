using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NodeRecordReaderTests
{
    [Fact]
    public void ReadsStoredMatrixOrderAndKeepsTypedLinksAndUnknownWords()
    {
        var frame = SceneFixture.Frame(0, 11, Matrix4x4.CreateTranslation(2, 3, 7), parent: 12, sibling: 13);
        var result = new NodeRecordReader().Read(frame, SceneFixture.Revision, SceneFixture.Physical);
        var node = Assert.IsType<SceneNodeRecord>(result.Value);
        Assert.True(SceneTransformMath.TryRead(node.StoredLocalTransform, out var local));
        Assert.Equal(new Vector3(2, 3, 7), Vector3.Transform(Vector3.Zero, local));
        Assert.Equal(IdentifierNamespace.Native, node.Parent!.Namespace);
        Assert.Equal(frame.Identity, node.Parent.Owner);
        Assert.Equal("12", node.Parent.Id);
        Assert.Equal(2002u, node.Parent.ResourceType);
        Assert.Equal("13", node.NextSibling!.Id);
        Assert.Null(node.FirstChild);
        Assert.Equal(0x12345678u, node.Word172);
        Assert.Equal(0x87654321u, node.Word176);
        Assert.Equal(frame.PayloadExtent.Offset + 160, node.FieldExtents["Parent"].Offset);
        Assert.Equal(frame.RawPayload.ToArray(), node.RawPayload.ToArray());
        Assert.Equal(2, result.UnresolvedReferences.Count);
        Assert.NotNull(Assert.Single(result.Steps).PayloadSha256);
    }

    [Fact]
    public void RejectsUnframedTagsVersionAndSourceMismatch()
    {
        var reader = new NodeRecordReader();
        var original = SceneFixture.Frame(0, 11, Matrix4x4.Identity);
        var bytes = original.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 6);
        Assert.Contains("NodeVersionUnsupported", reader.Read(original with { RawPayload = bytes },
            SceneFixture.Revision, SceneFixture.Physical).Diagnostics);
        Assert.Contains("NodeClassEnvelopeUnsupported", reader.Read(original with
        {
            RawPayload = original.RawPayload[..^1],
            PayloadExtent = original.PayloadExtent with { Length = original.RawPayload.Length - 1 }
        }, SceneFixture.Revision, SceneFixture.Physical).Diagnostics);
        Assert.Contains("NodeSourceIdentityMismatch", reader.Read(original,
            new("different-index", "stamp"), SceneFixture.Physical).Diagnostics);
        Assert.Contains("NodePhysicalExtentInvalid", reader.Read(original, SceneFixture.Revision,
            [new("../outside.dat", 0, 1)]).Diagnostics);
    }

    [Fact]
    public void RetainsNonFiniteStoredValuesForInspectionWithoutProducingPlacement()
    {
        var frame = SceneFixture.Frame(0, 11, Matrix4x4.Identity);
        var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + 16), float.NaN);
        var result = new NodeRecordReader().Read(frame with { RawPayload = bytes }, SceneFixture.Revision, SceneFixture.Physical);
        var node = Assert.IsType<SceneNodeRecord>(result.Value);
        Assert.False(SceneTransformMath.TryGetLocal(node, out _));
        Assert.True(float.IsNaN(node.StoredLocalTransform.Elements[15]));
    }

    [Fact]
    public void CancellationIsNotConvertedToAnUnsupportedResult()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NodeRecordReader().Read(
            SceneFixture.Frame(0, 11, Matrix4x4.Identity), SceneFixture.Revision, SceneFixture.Physical, token: cancel.Token));
    }
}

internal static class SceneFixture
{
    public static readonly ResourceIdentity Container = new("generated-index", 7);
    public static readonly SourceRevision Revision = new("generated-index", "generated-revision");
    public static readonly SourceExtent[] Physical = [new("sample.dat", 64, 4096)];

    public static NativeObjectFrame Frame(int ordinal, uint id, Matrix4x4 local, uint? parent = null,
        uint? child = null, uint? sibling = null, int attachment = -1, uint flags = 8, Vector3? pivot = null)
    {
        var bytes = new byte[196];
        Put(0, 1); "NODE    "u8.CopyTo(bytes.AsSpan(4)); Put(12, 5); Put(16, flags);
        var p = pivot ?? Vector3.Zero;
        Single(20, p.X); Single(24, p.Y); Single(28, p.Z);
        Matrix(32, local); Matrix(96, Matrix4x4.CreateTranslation(900, 800, 700));
        Reference(160, parent); Put(168, unchecked((uint)attachment)); Reference(172, child); Reference(180, sibling);
        Put(188, 0x12345678); Put(192, 0x87654321);
        var identity = new ResourceIdentity(Container.IndexContentId, Container.Ordinal, $"dc-object/{ordinal}/2002/{id}");
        return new(identity, ordinal, 2002, id, new(Container, 24 + ordinal * 8, 8),
            new(Container, 512 + ordinal * 209, 13), new(Container, 525 + ordinal * 209, bytes.Length),
            new byte[13], bytes, 0, 0, 0, 1);

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void Single(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        void Reference(int offset, uint? target) { if (target is { } value) { Put(offset, 2002); Put(offset + 4, value); } }
        void Matrix(int offset, Matrix4x4 value)
        {
            var values = SceneTransformMath.ToSource(value).Elements;
            for (var index = 0; index < 16; index++) Single(offset + index * 4, values[15 - index]);
        }
    }

    public static NativeObjectDirectoryDocument Directory(params NativeObjectFrame[] frames) =>
        new(Container, Revision, new(Container, 0, 24 + frames.Length * 8), new(Container, 512, frames.Length * 209),
            [], ReadOnlyMemory<byte>.Empty, frames, null, ReadOnlyMemory<byte>.Empty);

    public static SceneNodeRecord[] Read(IEnumerable<NativeObjectFrame> frames) => frames.Select(frame =>
        new NodeRecordReader().Read(frame, Revision, Physical).Value!).ToArray();
}
