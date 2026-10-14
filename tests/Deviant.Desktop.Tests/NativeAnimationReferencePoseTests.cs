using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class NativeAnimationReferencePoseTests
{
    [Fact]
    public void StoredRowsExpandWithoutNormalizingAndOutputsAreReadOnly()
    {
        var body = Body([]);
        var result = NativeAnimationReferencePose.Create(body, Globals());
        Assert.Equal(new Vector4(3, 4, 5, 2), result.References[0].Rotation);
        Assert.Equal(new Vector4(1, 10, 3, 0), result.References[0].Translation);
        Assert.Equal(new Vector4(2, 3, 4, 1), result.References[0].Scale);
        Assert.Equal(7u, result.Parents[1]);
        Assert.False(result.HasHeight); Assert.Equal(0, result.Height);
        Assert.Throws<NotSupportedException>(() => ((IList<uint>)result.Parents)[0] = 0);
        Assert.Equal(10, body.Nodes[0].Components.VectorAt16.Y);
    }

    [Fact]
    public void MinimumGlobalPointYShiftsOnlyImmediateParentPeersAndClampsHeight()
    {
        var body = Body([Row(1252226046, 0, new(1, 2, 3)), Row(2101515373, 1, new(0, 30, 0))]);
        var globals = Globals(); globals[0] = new(1, 2, 0, 0, 0, 3, 0, 0, 0, 4, 1, 0, 0, 5, 0, 1);
        var result = NativeAnimationReferencePose.Create(body, globals);
        Assert.True(result.HasHeight); Assert.Equal(0, result.HeightNode);
        Assert.Equal(25, result.GroundOffset); Assert.Equal(0, result.Height);
        Assert.Equal(-15, result.References[0].Translation.Y);
        Assert.Equal(6, result.References[1].Translation.Y);
        Assert.Equal(-18, result.References[2].Translation.Y);
        Assert.Equal(11, result.References[5].Translation.Y);
        Assert.Equal(-13, result.References[6].Translation.Y);
        Assert.Equal(10, body.Nodes[0].Components.VectorAt16.Y);
    }

    [Fact]
    public void ParentWritesFollowSerializedReaderOrder()
    {
        var body = Body([]); var nodes = body.Nodes.ToArray();
        // The later node's own parent read replaces the earlier child assignment.
        nodes[0] = nodes[0] with { Children = [6] };
        // A later child assignment to an already-read node remains in the runtime table.
        nodes[6] = nodes[6] with { StoredParent = uint.MaxValue, Children = [0] };
        var result = NativeAnimationReferencePose.Create(body with { Nodes = nodes }, Globals());
        Assert.Equal(6u, result.Parents[0]); Assert.Equal(7u, result.Parents[6]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MissingAnySpecialNodeDisablesHeightAndAdjustment(int missing)
    {
        var body = Body([Row(1252226046, 0, new(0, 2, 0))]); var descriptors = body.Descriptors.ToArray();
        descriptors[missing] = descriptors[missing] with { Key = 1 };
        var result = NativeAnimationReferencePose.Create(body with { Descriptors = descriptors }, Globals());
        Assert.False(result.HasHeight); Assert.Equal(0, result.Height); Assert.Equal(0, result.GroundOffset);
        Assert.Equal(10, result.References[0].Translation.Y);
    }

    [Fact]
    public void ToeBaseFallbackAndFirstMatchingDescriptorAndRecordAreUsed()
    {
        var body = Body([Row(1252226046, 0, new(0, 2, 0)), Row(1252226046, 0, new(0, -100, 0))]);
        var descriptors = body.Descriptors.ToArray();
        descriptors[2] = descriptors[2] with { Key = 0x6c810382 }; // CRC32 L_ToeBase
        descriptors[4] = descriptors[4] with { Key = 0x08839a61 }; // CRC32 R_ToeBase
        descriptors[6] = descriptors[6] with { Key = 0x47d23906 };
        var result = NativeAnimationReferencePose.Create(body with { Descriptors = descriptors }, Globals());
        Assert.True(result.HasHeight); Assert.Equal(0, result.HeightNode);
        Assert.Equal(2, result.GroundOffset); Assert.Equal(8, result.Height);
    }

    [Theory]
    [InlineData(0.000001f, 0f)]
    [InlineData(0.00001f, 0.00001f)]
    [InlineData(-2f, -2f)]
    public void GroundThresholdAndNegativeOffsetsFollowNativeBranch(float pointY, float applied)
    {
        var result = NativeAnimationReferencePose.Create(Body([Row(2101515373, 0, new(0, pointY, 0))]), Globals());
        Assert.True(result.HasHeight); Assert.Equal(applied, result.GroundOffset);
        Assert.Equal(10 - applied, result.Height);
    }

    [Fact]
    public void ExternalMetadataIndexRequiresOwnerMatrix()
    {
        var body = Body([Row(1252226046, 99, new(0, 2, 0))]);
        var error = Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body, Globals()));
        Assert.Equal("AnimationReferenceOwnerGlobalUnavailable", error.Message);
        var result = NativeAnimationReferencePose.Create(body, Globals(), Matrix4x4.CreateTranslation(0, 3, 0));
        Assert.Equal(5, result.GroundOffset); Assert.Equal(5, result.Height);
    }

    [Fact]
    public void MalformedMetadataNonfiniteValuesAndCancellationAreRejected()
    {
        var body = Body([Row(1252226046, 0, new(0, 2, 0))]);
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body with
        { RemainingTables = [body.RemainingTables[0] with { Count = 2 }] }, Globals()));
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body with
        { Owner = body.Owner with { BodyVersion = 13 } }, Globals()));
        var nodes = body.Nodes.ToArray(); nodes[0] = nodes[0] with { Children = [99] };
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body with { Nodes = nodes }, Globals()));
        var components = body.Nodes[0].Components.RawBytes.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(components, float.NaN);
        nodes = body.Nodes.ToArray(); nodes[0] = nodes[0] with { Components = nodes[0].Components with { RawBytes = components } };
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body with { Nodes = nodes }, Globals()));
        var globals = Globals(); globals[0].M42 = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(body, globals));
        Assert.Throws<InvalidDataException>(() => NativeAnimationReferencePose.Create(Body([Row(1252226046, 0, new(0, float.NaN, 0))]), Globals()));
        Assert.Throws<OperationCanceledException>(() => NativeAnimationReferencePose.Create(body, Globals(), token: new(true)));
    }

    private static Matrix4x4[] Globals() => Enumerable.Repeat(Matrix4x4.Identity, 7).ToArray();
    private static byte[] Row(uint key, uint node, Vector3 point)
    {
        var bytes = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), node);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), point.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), point.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), point.Z); return bytes;
    }
    private static NativeNodeArrayBodyDocument Body(byte[][] rows)
    {
        var container = new ResourceIdentity("synthetic-index", 1);
        var identity = container with { MemberPath = "node-array" }; var revision = new SourceRevision(identity.IndexContentId, "synthetic-revision");
        var extent = new DecodedExtent(container, 0, 40);
        var transform = new SourceTransform([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var inherited = new SceneNodeRecord(identity, revision, 1, 0, Vector3.Zero, transform, transform,
            null, null, null, -1, 0, 0, extent, new Dictionary<string, DecodedExtent>(), ReadOnlyMemory<byte>.Empty);
        var owner = new NativeNodeArrayDocument(identity, revision, 14, 14, inherited, extent, extent, extent,
            new Dictionary<string, DecodedExtent>(), ReadOnlyMemory<byte>.Empty);
        uint[] parents = [1, uint.MaxValue, 1, 1, 1, 2, 1]; float[] heights = [10, 6, 7, 8, 9, 11, 12];
        uint[] keys = [0x47d23906, 0x3be27a1f, 0x7c473972, 0x023e19f4, 0xa3971091, 1, 2];
        var nodes = new NodeArrayBodyNode[7]; var descriptors = new NodeArrayBodyDescriptor[7];
        for (int i = 0; i < nodes.Length; i++)
        {
            var bytes = new byte[40]; float[] components = [2, 3, 4, 5, 1, heights[i], 3, 2, 3, 4];
            for (int j = 0; j < components.Length; j++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(j * 4), components[j]);
            nodes[i] = new(i, 0, new(bytes, extent), 0, parents[i], [], 0, extent);
            descriptors[i] = new(keys[i], 0, 0, 0, extent);
        }
        byte[] records = rows.SelectMany(row => row).ToArray(); var tableExtent = new DecodedExtent(container, 40, records.Length);
        return new(owner, nodes, descriptors, [], new("OptionalObject", 0, 1, new(container, 0, 0), ReadOnlyMemory<byte>.Empty),
            0, 0, 0, 0, [new("FiveWordRecords", rows.Length, 20, tableExtent, records)], [], 0, extent);
    }
}
