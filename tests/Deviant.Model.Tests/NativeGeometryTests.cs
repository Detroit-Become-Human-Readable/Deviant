using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeGeometryTests
{
    [Fact]
    public void NamesOnlyReaderStopsBeforeUnsupportedLaterMeshSections()
    {
        const string storedName = "Actor_Formal_CHA";
        var sample = CreateMesh(name: storedName, unsupportedTail: true);
        var frame = Frame(sample.Metadata);

        Assert.False(new NativeMeshDataReader().TryRead(frame, out _, out _));
        Assert.True(new NativeMeshNameReader().TryRead(frame, out var result, out var diagnostic,
            maximumNameBytes: 64), diagnostic);
        Assert.Equal(storedName, result!.Text);
        Assert.Equal(frame.PayloadExtent.Offset + sample.NameOffset, result.Extent.Offset);
        Assert.Equal(Encoding.UTF8.GetByteCount(storedName), result.Extent.Length);
    }

    [Fact]
    public void NamesOnlyReaderRejectsNamesAboveItsConfiguredByteLimit()
    {
        var sample = CreateMesh(name: "StoredName");
        Assert.False(new NativeMeshNameReader().TryRead(Frame(sample.Metadata), out var result,
            out var diagnostic, maximumNameBytes: 4));
        Assert.Null(result);
        Assert.Equal("NativeMeshNameLimit", diagnostic);
    }

    [Fact]
    public void InventoryNameReaderReturnsOnlyTypedShapeLabelsForTheSelectedGroup()
    {
        const string storedName = "mesh-source";
        const string shapeName = "Actor_Formal_HEAD";
        var sample = CreateMesh(groupCount: 2, name: storedName, shapeName: shapeName);
        var frame = Frame(sample.Metadata);

        Assert.True(new NativeMeshNameReader().TryReadInventoryHints(frame, targetGroupOrdinal: 0,
            out var hints, out var diagnostic, maximumNameBytes: 64, maximumShapeNames: 4,
            maximumShapeNameBytes: 128), diagnostic);
        Assert.Equal(storedName, hints!.Name!.Text);
        var label = Assert.Single(hints.ShapeNames);
        Assert.Equal(0, label.GroupOrdinal);
        Assert.Equal(shapeName, Encoding.UTF8.GetString(label.RawBytes.Span));
        Assert.Equal(Encoding.UTF8.GetByteCount(shapeName), label.BytesExtent.Length);
        Assert.Equal(0xaabbccddu, label.RawTrailingWord);

        Assert.True(new NativeMeshNameReader().TryReadInventoryHints(frame, targetGroupOrdinal: 1,
            out var otherGroup, out diagnostic, maximumNameBytes: 64, maximumShapeNames: 4,
            maximumShapeNameBytes: 128), diagnostic);
        Assert.Empty(otherGroup!.ShapeNames);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(255)]
    public void InlineMetadataRetainsDeclaredStreamSelectorsWithoutGeometryAdmission(byte stream)
    {
        var sample = CreateMesh(inlineStream1: true, extraStream: stream);
        Assert.True(new NativeMeshDataReader().TryRead(Frame(sample.Metadata), out var document,
            out var diagnostic), diagnostic);
        var buffer = Assert.Single(document!.VertexBuffers);
        var descriptor = buffer.Descriptors[^1];
        Assert.Equal(Descriptor(stream, 0, 2, 6), descriptor.Word);
        Assert.Equal(stream, descriptor.Stream);
        Assert.Equal((byte)1, buffer.Flag);
        Assert.Equal((long)buffer.Streams[1].Stride * buffer.ElementCount,
            buffer.Streams[1].InlineExtent!.Length);
        Assert.Equal(2, document.Groups[0].Parts.Count);
        Assert.Equal(sample.Metadata.Length, document.BlendShapeMetadata!.Extent.Offset +
            document.BlendShapeMetadata.Extent.Length);
        Assert.False(new NativeMeshGeometryDecoder().TryDecode(document, sample.Gpu, 0,
            out _, out var geometryDiagnostic));
        Assert.Equal("NativeVertexBufferUnsupported", geometryDiagnostic);
    }

    [Fact]
    public void MetadataRetentionDoesNotAllowAnOutOfRangeGeometryStream()
    {
        var sample = CreateMesh(extraStream: 4);
        Assert.True(new NativeMeshDataReader().TryRead(Frame(sample.Metadata), out var document,
            out var diagnostic), diagnostic);
        Assert.Equal((byte)4, document!.VertexBuffers[0].Descriptors[^1].Stream);
        Assert.False(new NativeMeshGeometryDecoder().TryDecode(document, sample.Gpu, 0,
            out _, out var geometryDiagnostic));
        Assert.Equal("NativeVertexDescriptorRangeInvalid", geometryDiagnostic);
    }

    [Theory]
    [InlineData(8, 3)]
    [InlineData(8, 5)]
    [InlineData(255, 5)]
    [InlineData(255, 15)]
    public void UnreadAttributeFormatsKeepSupportedGeometryAndRawDescriptors(byte format, byte semantic)
    {
        var sample = CreateMesh();
        var document = Read(sample);
        var buffer = document.VertexBuffers[0];
        var extra = buffer.Descriptors[0] with { Word = Descriptor(1, 0, format, semantic) };
        var updated = document with
        { VertexBuffers = [buffer with { Descriptors = [.. buffer.Descriptors, extra] }] };
        var geometry = new NativeMeshGeometryDecoder().Decode(updated, sample.Gpu, 0);
        var original = new NativeMeshGeometryDecoder().Decode(document, sample.Gpu, 0);
        Assert.Equal(original.Primitives[0].Positions, geometry.Primitives[0].Positions);
        Assert.Equal(original.Primitives[0].Normals, geometry.Primitives[0].Normals);
        Assert.Equal(original.Primitives[0].Indices, geometry.Primitives[0].Indices);
        Assert.Contains(extra.Word.ToString("X8"), geometry.UnknownFields[geometry.Primitives[0].Key + "/Descriptors"]);
    }

    [Fact]
    public void UnreadAttributesStillRequireAnOffsetInsideTheirStream()
    {
        var sample = CreateMesh();
        var document = Read(sample);
        var buffer = document.VertexBuffers[0];
        foreach (var word in new[] { Descriptor(4, 0, 255, 15), Descriptor(1, 16, 255, 15) })
        {
            var extra = buffer.Descriptors[0] with { Word = word };
            var updated = document with { VertexBuffers = [buffer with { Descriptors = [.. buffer.Descriptors, extra] }] };
            Assert.False(new NativeMeshGeometryDecoder().TryDecode(updated, sample.Gpu, 0, out _, out var diagnostic));
            Assert.Equal("NativeVertexDescriptorRangeInvalid", diagnostic);
        }
    }

    [Fact]
    public void SelectedVertexAttributesKeepTheirFormatValidation()
    {
        var sample = CreateMesh();
        var document = Read(sample);
        var buffer = document.VertexBuffers[0];
        var changed = buffer.Descriptors[0] with { Word = Descriptor(0, 0, 255, 0) };
        var updated = document with { VertexBuffers = [buffer with { Descriptors = [changed, .. buffer.Descriptors.Skip(1)] }] };
        Assert.False(new NativeMeshGeometryDecoder().TryDecode(updated, sample.Gpu, 0, out _, out var diagnostic));
        Assert.Equal("NativeVertexFormatUnsupported", diagnostic);
    }

    [Fact]
    public void UnselectedBuffersDoNotRequireDecodersForTheirAttributes()
    {
        var sample = CreateMesh();
        var document = Read(sample);
        var buffer = document.VertexBuffers[0];
        var extra = buffer with
        {
            ElementCount = 0,
            Descriptors = [buffer.Descriptors[0] with { Word = Descriptor(0, 0, 255, 0) }]
        };
        var updated = document with { VertexBuffers = [buffer, extra] };
        var geometry = new NativeMeshGeometryDecoder().Decode(updated, sample.Gpu, 0);
        Assert.Equal(new Vector3(2, 3, 4), geometry.Primitives[0].Positions[0]);
    }

    [Fact]
    public void GeneratedMeshDecodesAttributesAndSharedIndexWindows()
    {
        var sample = CreateMesh();
        var document = Read(sample);
        var geometry = new NativeMeshGeometryDecoder().Decode(document, sample.Gpu, 0);
        Assert.Equal(2, geometry.Primitives.Count);
        var first = geometry.Primitives[0]; var second = geometry.Primitives[1];
        Assert.Equal(new Vector3(2, 3, 4), first.Positions[0]);
        Assert.Equal(new uint[] { 0, 1, 2 }, first.Indices);
        Assert.Equal(first.Indices, second.Indices);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(new Vector3(1, 0, 0), first.Normals![0]);
        Assert.Equal(new Vector4(0, 1, 0, -1), first.Tangents![0]);
        Assert.Equal(new Vector4(1, 128 / 255f, 0, 64 / 255f), first.Colors![0]);
        Assert.Equal(new Vector2(.25f, .5f), first.TextureCoordinates![0][0]);
        Assert.Equal(document.Identity, first.Submeshes[0].Materials[0].Owner);
        Assert.Equal((uint)2133, first.Submeshes[0].Materials[0].ResourceType);
        Assert.Equal((uint)0xaabbccdd, document.Groups[0].Parts[0].LeadingWord);
        Assert.Equal(document.Identity, geometry.Nodes[0].Source.Owner);
        Assert.Equal(new Vector3(2, 3, 4), first.Bounds!.Minimum);
        Assert.Equal(new Vector3(4, 5, 6), first.Bounds.Maximum);
    }

    [Fact]
    public void BaseVertexAboveUnsignedShortRangeKeepsIndicesLocal()
    {
        var sample = CreateMesh(baseVertex: 65537);
        var geometry = new NativeMeshGeometryDecoder().Decode(Read(sample), sample.Gpu, 0);
        Assert.Equal(new Vector3(65537, 65538, 65539), geometry.Primitives[0].Positions[0]);
        Assert.Equal(new uint[] { 0, 1, 2 }, geometry.Primitives[0].Indices);
    }

    [Fact]
    public void FlagOneClusterSectionPreservesRawFieldsAndContinuesToEmptyShapeSection()
    {
        var sample = CreateMesh(clusterFlag: 1, clusterEntryCount: 2);
        var document = Read(sample);
        var cluster = Assert.IsType<NativeMeshClusterV2Section>(document.ClusterMetadata);

        Assert.Equal((byte)1, document.ClusterFlag);
        Assert.Equal((uint)2, cluster.EntryCount);
        Assert.Equal(6, cluster.ScalarWordBits.Count);
        Assert.Equal(24, cluster.RawScalarWords.Length);
        Assert.Equal(6, cluster.RawByteValues.Length);
        Assert.Equal(24, cluster.RawTupleValues.Length);
        Assert.Equal(cluster.Extent.Length, (long)cluster.RawSection.Length);
        Assert.Single(document.BlendShapeCountExtents);
        var shapeGroup = Assert.Single(Assert.IsType<NativeMeshBlendShapeSection>(document.BlendShapeMetadata).Groups);
        Assert.Equal((uint)0, shapeGroup.EntryCount);
        Assert.Empty(shapeGroup.Entries);
        var sectionEnd = checked((int)(cluster.Extent.Offset - document.PayloadExtent.Offset + cluster.Extent.Length));
        Assert.Equal("BLSHAPES", Encoding.ASCII.GetString(sample.Metadata.AsSpan(sectionEnd, 8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sample.Metadata.AsSpan(sectionEnd + 12, 4)));
    }

    [Fact]
    public void MultipleGroupsRequireOneEmptyBlendShapeRecordEach()
    {
        var sample = CreateMesh(groupCount: 3);
        var document = Read(sample);
        Assert.Equal(3, document.Groups.Count);
        Assert.Equal(3, document.BlendShapeCountExtents.Count);
        var geometry = new NativeMeshGeometryDecoder().Decode(document, sample.Gpu, 2);
        Assert.All(geometry.Primitives, p => Assert.Contains("/group/2/", p.Key));
        Assert.False(new NativeMeshDataReader().TryRead(Frame(sample.Metadata[..^4]), out _, out _));
        Assert.False(new NativeMeshDataReader().TryRead(Frame([.. sample.Metadata, 0, 0, 0, 0]), out _, out _));
    }

    [Fact]
    public void HalfFourUvDescriptorUsesFirstPairAndRetainsDescriptorWord()
    {
        var sample = CreateMesh(halfFourUv: true);
        var document = Read(sample);
        var geometry = new NativeMeshGeometryDecoder().Decode(document, sample.Gpu, 0);
        Assert.Equal((byte)6, document.VertexBuffers[0].Descriptors[4].Format);
        Assert.Equal(new Vector2(.25f, .5f), geometry.Primitives[0].TextureCoordinates![0][0]);
        Assert.True(geometry.UnknownFields.ContainsKey(geometry.Primitives[0].Key + "/Descriptors"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackedPrimaryAndSecondaryUvsKeepDistinctChannelsAndBaseVertex(bool secondaryFour)
    {
        var sample = CreateMesh(halfFourUv: true, secondaryUv: true, secondaryFourUv: secondaryFour);
        var primitive = new NativeMeshGeometryDecoder().Decode(Read(sample), sample.Gpu, 0).Primitives[0];
        Assert.Equal(secondaryFour ? 4 : 3, primitive.TextureCoordinates!.Count);
        Assert.Equal(new Vector2(.25f, .5f), primitive.TextureCoordinates[0][0]);
        Assert.Equal(new Vector2(.75f, .875f), primitive.TextureCoordinates[1][0]);
        Assert.Equal(new Vector2(.125f, .375f), primitive.TextureCoordinates[2][0]);
        Assert.True(primitive.FieldExtents!.ContainsKey("TextureCoordinates2"));
        if (secondaryFour)
        {
            Assert.Equal(new Vector2(.625f, .9375f), primitive.TextureCoordinates[3][0]);
            Assert.True(primitive.FieldExtents.ContainsKey("TextureCoordinates3"));
        }
    }

    [Fact]
    public void PackedVectorsUseNativeBoundaryAndTangentBooleanSign()
    {
        var sample = CreateMesh();
        var normalAt = sample.Stream1Start + sample.BaseVertex * 16;
        BinaryPrimitives.WriteUInt32LittleEndian(sample.Gpu.AsSpan(normalAt), 512u | 513u << 10);
        BinaryPrimitives.WriteUInt32LittleEndian(sample.Gpu.AsSpan(normalAt + 4), 511u | 2u << 30);
        var primitive = new NativeMeshGeometryDecoder().Decode(Read(sample), sample.Gpu, 0).Primitives[0];
        Assert.Equal(new Vector3(512 / 511f, -1, 0), primitive.Normals![0]);
        Assert.Equal(new Vector4(1, 0, 0, -1), primitive.Tangents![0]);
    }

    [Fact]
    public void MetadataRejectsFullWidthCountsAndTruncatedOrExtraClasses()
    {
        var sample = CreateMesh();
        var highPalette = sample.Metadata.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(highPalette.AsSpan(sample.PaletteOffset), 0x10000);
        Assert.False(new NativeMeshDataReader().TryRead(Frame(highPalette), out _, out _));
        var highVectors = sample.Metadata.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(highVectors.AsSpan(sample.VectorOffset), 0x100);
        Assert.False(new NativeMeshDataReader().TryRead(Frame(highVectors), out _, out _));
        Assert.False(new NativeMeshDataReader().TryRead(Frame(sample.Metadata[..^1]), out _, out _));
        Assert.False(new NativeMeshDataReader().TryRead(Frame([.. sample.Metadata, 1]), out _, out var extra));
        Assert.Equal("NativeMeshMetadataEndMismatch", extra);
        Assert.False(new NativeMeshDataReader().TryRead(Frame([.. sample.Metadata, 0, 0]), out _, out _));
    }

    [Fact]
    public void MetadataRejectsInvalidWindowAndNonFiniteBounds()
    {
        var sample = CreateMesh();
        var bad = sample.Metadata.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(sample.PartFieldsOffset + 4), uint.MaxValue);
        Assert.False(new NativeMeshDataReader().TryRead(Frame(bad), out _, out var window));
        Assert.Equal("NativeMeshWindowInvalid", window);
        bad = sample.Metadata.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(sample.VectorOffset + 8), 0x7fc00000);
        Assert.False(new NativeMeshDataReader().TryRead(Frame(bad), out _, out var finite));
        Assert.Equal("NativeMeshNonFinite", finite);
    }

    [Fact]
    public void DecoderRejectsGpuEndMismatchIndicesAndNonFiniteAttributes()
    {
        var sample = CreateMesh(); var document = Read(sample); var decoder = new NativeMeshGeometryDecoder();
        Assert.False(decoder.TryDecode(document, sample.Gpu[..^1], 0, out _, out _));
        Assert.False(decoder.TryDecode(document, new byte[sample.Gpu.Length + 16], 0, out _, out var end));
        Assert.Equal("NativeMeshGpuEndMismatch", end);
        var bad = sample.Gpu.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(bad, 3);
        Assert.False(decoder.TryDecode(document, bad, 0, out _, out var indices));
        Assert.Equal("NativeMeshIndexOutOfRange", indices);
        bad = sample.Gpu.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(16 + sample.BaseVertex * 12), 0x7f800000);
        Assert.False(decoder.TryDecode(document, bad, 0, out _, out var finite));
        Assert.Equal("NativeMeshVertexNonFinite", finite);
    }

    [Fact]
    public void DecoderRejectsDescriptorOutsideStrideAndDuplicatePosition()
    {
        var sample = CreateMesh(); var document = Read(sample); var buffer = document.VertexBuffers[0];
        var badDescriptor = buffer.Descriptors[0] with { Word = Descriptor(0, 8, 2, 0) };
        var bad = document with { VertexBuffers = [buffer with { Descriptors = [badDescriptor, .. buffer.Descriptors.Skip(1)] }] };
        Assert.False(new NativeMeshGeometryDecoder().TryDecode(bad, sample.Gpu, 0, out _, out var range));
        Assert.Equal("NativeVertexDescriptorRangeInvalid", range);
        bad = document with { VertexBuffers = [buffer with { Descriptors = [.. buffer.Descriptors, buffer.Descriptors[0]] }] };
        Assert.False(new NativeMeshGeometryDecoder().TryDecode(bad, sample.Gpu, 0, out _, out var duplicate));
        Assert.Equal("NativeMeshSemanticDuplicate", duplicate);
    }

    [Fact]
    public void ReadersAndDecoderPropagateCancellation()
    {
        var sample = CreateMesh(); var document = Read(sample);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeMeshDataReader().TryRead(Frame(sample.Metadata), out _, out _, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new NativeMeshGeometryDecoder().TryDecode(document, sample.Gpu, 0, out _, out _, cancellation.Token));
    }

    [Fact]
    public void MeshEntityUsesSuppliedClassBoundaryAndRetainsAllLods()
    {
        var (frame, entity) = CreateEntity(0x000601c1);
        Assert.True(new NativeMeshEntityReader().TryRead(frame, entity, out var mesh, out var diagnostic), diagnostic);
        Assert.Equal(2, mesh!.Lods.Count); Assert.Equal("17", mesh.Lods[0].MeshData.Id); Assert.Equal(.5f, mesh.Lods[1].Threshold);
        Assert.Equal((uint)3, mesh.GroupIndex);
        Assert.False(new NativeMeshEntityReader().TryRead(frame, entity with { NextClassOffset = entity.NextClassOffset + 1 }, out _, out _));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(0x000601c0u, false)]
    [InlineData(0x000601c1u, true)]
    [InlineData(0x000601c2u, false)]
    [InlineData(0xfffffffeu, false)]
    [InlineData(0xffffffffu, true)]
    public void MeshEntityPreservesFullNativeFlagsAndUsesVisibilityBit(uint flags, bool visible)
    {
        var (frame, entity) = CreateEntity(flags);
        Assert.True(new NativeMeshEntityReader().TryRead(frame, entity, out var mesh, out var diagnostic), diagnostic);
        Assert.Equal(flags, mesh!.NativeFlags);
        Assert.Equal(visible, mesh.IsSourceVisible);
        var extent = mesh.FieldExtents["NativeFlags"];
        Assert.Equal(frame.PayloadExtent.Container, extent.Container);
        Assert.Equal(frame.PayloadExtent.Offset + entity.NextClassOffset + 40 + 12 * mesh.Lods.Count, extent.Offset);
        Assert.Equal(4, extent.Length);
        var relativeOffset = checked((int)(extent.Offset - frame.PayloadExtent.Offset));
        Assert.Equal(flags, BinaryPrimitives.ReadUInt32LittleEndian(frame.RawPayload.Span[relativeOffset..]));
        Assert.Equal((uint)3, mesh.GroupIndex);
        Assert.Equal(2, mesh.Lods.Count);
    }

    private static (NativeObjectFrame Frame, SceneEntityRecord Entity) CreateEntity(uint nativeFlags)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(4u); Tag(writer, "COM_CONT", 6); writer.Write(0u); Tag(writer, "LOADCONT", 2);
        Tag(writer, "ENTITY  ", 7); writer.Write(new byte[37]);
        var meshStart = (int)memory.Position; Tag(writer, "MESH____", 29); writer.Write(2u);
        writer.Write(2130u); writer.Write(17u); writer.Write(.25f);
        writer.Write(2130u); writer.Write(18u); writer.Write(.5f);
        writer.Write(new byte[24]); writer.Write(nativeFlags); writer.Write(3u); writer.Write(0u);
        var bytes = memory.ToArray(); var frame = Frame(bytes, 2009, 4);
        var entity = new SceneEntityRecord(frame.Identity, 2009, frame.NativeId, null, [], 0, 0, 4, meshStart,
            new(frame.PayloadExtent.Container, 32, 49), new Dictionary<string, DecodedExtent>());
        return (frame, entity);
    }

    private static NativeMeshDataDocument Read(Sample sample)
    {
        Assert.True(new NativeMeshDataReader().TryRead(Frame(sample.Metadata), out var document, out var diagnostic), diagnostic);
        return document!;
    }
    private static NativeObjectFrame Frame(byte[] bytes, uint type = 2130, uint classes = 3)
    {
        var identity = new ResourceIdentity("generated-index", 1, "object");
        var container = identity with { MemberPath = null };
        return new(identity, 0, type, 23, new(container, 0, 0), new(container, 0, 0),
            new(container, 0, bytes.Length), [], bytes, 0, 0, 0, classes);
    }
    private static Sample CreateMesh(int baseVertex = 2, int groupCount = 1, bool halfFourUv = false,
        byte clusterFlag = 0, uint clusterEntryCount = 0, bool inlineStream1 = false, byte? extraStream = null, bool secondaryUv = false,
        bool secondaryFourUv = false, string name = "", bool unsupportedTail = false, string? shapeName = null)
    {
        var elements = baseVertex + 3;
        using var metadata = new MemoryStream(); using var writer = new BinaryWriter(metadata);
        writer.Write(3u); Tag(writer, "MESHDATA", 41);
        writer.Write(1u); writer.Write((extraStream.HasValue ? 6u : 5u) + (secondaryUv ? 1u : 0u)); writer.Write((uint)elements);
        writer.Write((byte)(inlineStream1 ? 1 : 0));
        writer.Write(1u); writer.Write(3u); writer.Write((uint)groupCount);
        var fieldsOffset = 0; var paletteOffset = 0; var vectorOffset = 0;
        for (var group = 0; group < groupCount; group++)
        {
            writer.Write(2u); writer.Write(0u); writer.Write(0u);
            for (var part = 0; part < 2; part++)
            {
                writer.Write(0xaabbccddu); writer.Write((byte)0); writer.Write((byte)0);
                if (part == 0 && group == 0) fieldsOffset = (int)metadata.Position;
                writer.Write(0u); writer.Write((uint)baseVertex); writer.Write(3u); writer.Write(0u);
                writer.Write(0u); writer.Write(3u); writer.Write(0u);
                if (part == 0 && group == 0) paletteOffset = (int)metadata.Position;
                writer.Write(0u); writer.Write(2133u); writer.Write((uint)(30 + part));
                if (part == 0 && group == 0) vectorOffset = (int)metadata.Position;
                writer.Write(0u); writer.Write(0u); Bounds(writer);
            }
            writer.Write((byte)0); writer.Write((byte)0); Bounds(writer);
        }
        writer.Write(0u); writer.Write(0u);
        writer.Write(Descriptor(0, 0, 2, 0)); writer.Write(Descriptor(1, 0, 13, 1));
        writer.Write(Descriptor(1, 4, 13, 10)); writer.Write(Descriptor(1, 8, 4, 2)); writer.Write(Descriptor(1, 12, (byte)(halfFourUv ? 6 : 5), 4));
        if (extraStream is { } declaredStream) writer.Write(Descriptor(declaredStream, 0, 2, 6));
        if (secondaryUv) writer.Write(Descriptor(1, 20, (byte)(secondaryFourUv ? 6 : 5), 5));
        writer.Write(12u); writer.Write((uint)(secondaryUv ? secondaryFourUv ? 28 : 24 : halfFourUv ? 20 : 16));
        if (inlineStream1) writer.Write(new byte[elements * (halfFourUv ? 20 : 16)]);
        writer.Write(0u); writer.Write(0u);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        writer.Write((uint)nameBytes.Length);
        var nameOffset = (int)metadata.Position;
        writer.Write(nameBytes);
        writer.Write(uint.MaxValue); writer.Write(0u); writer.Write(0u);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
        if (unsupportedTail) Tag(writer, "BADCLUST", 2);
        else WriteClusterSection(writer, clusterFlag, clusterEntryCount);
        Tag(writer, "BLSHAPES", 3);
        for (var group = 0; group < groupCount; group++)
        {
            if (group != 0 || shapeName is null) { writer.Write(0u); continue; }
            writer.Write(1u); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
            writer.Write((byte)0);
            var label = Encoding.UTF8.GetBytes(shapeName);
            writer.Write((uint)label.Length); writer.Write(label); writer.Write(0xaabbccddu);
        }
        using var gpu = new MemoryStream(); using var gpuWriter = new BinaryWriter(gpu);
        gpuWriter.Write((ushort)0); gpuWriter.Write((ushort)1); gpuWriter.Write((ushort)2); gpuWriter.Write(new byte[10]);
        for (var i = 0; i < elements; i++) { gpuWriter.Write((float)i); gpuWriter.Write((float)i + 1); gpuWriter.Write((float)i + 2); }
        var stream1Start = (int)gpu.Position;
        for (var i = 0; i < elements; i++)
        {
            gpuWriter.Write(511u); gpuWriter.Write(511u << 10 | 1u << 30);
            gpuWriter.Write(new byte[] { 255, 128, 0, 64 });
            gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).25)); gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).5));
            if (halfFourUv) { gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).75)); gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).875)); }
            if (secondaryUv)
            {
                gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).125)); gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).375));
                if (secondaryFourUv) { gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).625)); gpuWriter.Write(BitConverter.HalfToUInt16Bits((Half).9375)); }
            }
        }
        while (gpu.Length % 16 != 0) gpuWriter.Write((byte)0);
        return new(metadata.ToArray(), gpu.ToArray(), baseVertex, stream1Start, fieldsOffset, paletteOffset, vectorOffset, nameOffset);
    }
    private static void WriteClusterSection(BinaryWriter writer, byte flag, uint entryCount)
    {
        Tag(writer, "CLUPSKME", 2);
        writer.Write(flag);
        if (flag != 1) return;
        writer.Write(entryCount);
        for (var index = 0; index < 6; index++) writer.Write(BitConverter.SingleToUInt32Bits(index + .25f));
        for (var index = 0; index < checked((int)entryCount * 3); index++) writer.Write((byte)(index * 7));
        for (var index = 0; index < checked((int)entryCount * 4); index++)
        {
            writer.Write((byte)(index * 3));
            writer.Write((ushort)(0x1200 + index));
        }
    }
    private static uint Descriptor(byte stream, byte offset, byte format, byte semantic) =>
        (uint)stream | (uint)offset << 8 | (uint)format << 16 | (uint)semantic << 24;
    private static void Tag(BinaryWriter writer, string tag, uint version) { writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(version); }
    private static void Bounds(BinaryWriter writer) { for (var i = 0; i < 19; i++) writer.Write(0f); }
    private sealed record Sample(byte[] Metadata, byte[] Gpu, int BaseVertex, int Stream1Start,
        int PartFieldsOffset, int PaletteOffset, int VectorOffset, int NameOffset);
}
