using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkinningWindowTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void PositionWindowUsesSourceVertexOffsetAndMatchesFullBuffer(uint mode)
    {
        var sample = Expanded(mode, VectorRows(false));
        var positions = Enumerable.Range(0, 4).Select(vertex => new Vector3(vertex + 1, vertex, 2)).ToArray();
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        using var full = new NativeSkinningPositionEvaluator().Evaluate(attributes.Document, 0, positions,
            pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);
        var reservedBeforeWindow = budget.Used;
        var windowPositions = positions.AsSpan(2, 2).ToArray();

        using var window = new NativeSkinningPositionEvaluator().EvaluateWindow(attributes.Document, 0, 2,
            windowPositions, pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);

        Assert.Equal(full.GetPosition(2), window.GetPosition(0));
        Assert.Equal(full.GetPosition(3), window.GetPosition(1));
        Assert.Equal(new Vector3(11, 2, 2), window.GetPosition(0));
        Assert.Equal(new Vector3(16, 3, 2), window.GetPosition(1));
        Assert.Equal(reservedBeforeWindow + 2 * 12 + 128, budget.Used);
        Assert.Equal(2 * 12 + 128, budget.LastBytes);
        Assert.Equal("native posed positions", budget.LastCategory);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void VectorWindowUsesSourceVertexOffsetAndMatchesFullBuffer(uint mode)
    {
        var sample = Expanded(mode, VectorRows(true));
        var normals = Enumerable.Repeat(511u << 20, 4).ToArray();
        var tangents = mode == 1 ? [] : Enumerable.Repeat(511u | 1u << 30, 4).ToArray();
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        using var full = new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0, normals,
            tangents, pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);
        var reservedBeforeWindow = budget.Used;
        var windowTangents = tangents.Length == 0 ? Array.Empty<uint>() : tangents.AsSpan(2, 2).ToArray();

        using var window = new NativeSkinningVectorEvaluator().EvaluateWindow(attributes.Document, 0, 2,
            normals.AsSpan(2, 2).ToArray(), windowTangents,
            pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);

        Assert.Equal(full.GetNormal(2), window.GetNormal(0));
        Assert.Equal(full.GetNormal(3), window.GetNormal(1));
        Assert.Equal(511u, window.GetNormal(0));
        Assert.Equal(513u, window.GetNormal(1));
        Assert.Equal(mode != 1, window.HasTangents);
        if (mode != 1)
        {
            Assert.Equal(full.GetTangent(2), window.GetTangent(0));
            Assert.Equal(full.GetTangent(3), window.GetTangent(1));
            Assert.Equal(513u << 20 | 1u << 30, window.GetTangent(0));
            Assert.Equal(511u << 20 | 1u << 30, window.GetTangent(1));
        }
        var bytesPerVertex = mode is 4 or 8 ? 8 : 4;
        var reservedBytes = 2 * bytesPerVertex + 192;
        Assert.Equal(reservedBeforeWindow + reservedBytes, budget.Used);
        Assert.Equal(reservedBytes, budget.LastBytes);
        Assert.Equal("native posed vectors", budget.LastCategory);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void TbnWindowEvaluatesBothVectorStreamsAtTheSourceOffset(uint mode)
    {
        var sample = Expanded(mode, VectorRows(true));
        var normals = Enumerable.Repeat(511u << 20, 4).ToArray();
        var tangents = Enumerable.Repeat((511u << 20) | (1u << 30), 4).ToArray();
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);

        using var origin = new NativeSkinningVectorEvaluator().EvaluateTbnWindow(attributes.Document, 0, 0,
            [normals[0]], [tangents[0]], pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);
        var reservedBeforeWindow = budget.Used;
        using var window = new NativeSkinningVectorEvaluator().EvaluateTbnWindow(attributes.Document, 0, 2,
            [normals[2]], [tangents[2]], pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);

        Assert.True(window.HasTangents);
        Assert.Equal(511u << 20, origin.GetNormal(0));
        Assert.Equal(511u, window.GetNormal(0));
        Assert.Equal(511u << 20 | 1u << 30, origin.GetTangent(0));
        Assert.Equal(511u | 1u << 30, window.GetTangent(0));
        Assert.Equal(reservedBeforeWindow + 8 + 192, budget.Used);
        Assert.Equal(8 + 192, budget.LastBytes);
        if (mode == 1)
        {
            using var normalOnly = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
                attributes.Document, 0, 0, [normals[0]], pose, sample.Source.Revision,
                sample.Source.SnapshotSequence, budget.Reserve);
            Assert.Equal(origin.GetNormal(0), normalOnly.GetNormal(0));
        }
        window.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = window.GetTangent(0); });
    }

    [Theory]
    [InlineData(1u << 30)]
    [InlineData(3u << 30)]
    public void TbnWindowMatchesGeneratedSingleJointOutputAndPreservesTangentFlag(uint tangentFlag)
    {
        var sample = Expanded(1, SingleJointTbnRows());
        // The one-influence kernel uses its implicit unit weight; the stored lane value is ignored.
        BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(16 + 16), 0);
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);

        using var output = new NativeSkinningVectorEvaluator().EvaluateTbnWindow(attributes.Document, 0, 0,
            [511u | 256u << 10 | 513u << 20], [511u | tangentFlag], pose,
            sample.Source.Revision, sample.Source.SnapshotSequence, ReserveNoop);

        Assert.True(output.HasTangents);
        Assert.Equal(646253652u, output.GetNormal(0));
        Assert.Equal(1933678975u, output.GetTangent(0));
    }

    [Theory]
    [InlineData(7, 0, 0x201401FFu, 0xC00001FFu, 0x3670C554u, 0x489E956Au)]
    [InlineData(13, 3, 0x1FF80600u, 0x80000200u, 0x2DACB18Eu, 0x470B24DBu)]
    public void TbnWindowMatchesGeneratedMultiRowSingleInfluenceOutput(int sourceVertex, int rowOffset,
        uint normalInput, uint tangentInput, uint expectedNormal, uint expectedTangent)
    {
        var sample = Expanded(1, MultiMatrixTbnRows(), sourceVertex + 1);
        var vertexAt = 16 + sourceVertex * 32;
        BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(vertexAt + 16), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(vertexAt + 24),
            BitConverter.HalfToUInt16Bits((Half)rowOffset));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);

        using var output = new NativeSkinningVectorEvaluator().EvaluateTbnWindow(attributes.Document, 0,
            sourceVertex, [normalInput], [tangentInput], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, ReserveNoop);

        Assert.True(output.HasTangents);
        Assert.Equal(expectedNormal, output.GetNormal(0));
        Assert.Equal(expectedTangent, output.GetTangent(0));
    }

    [Fact]
    public void TbnWindowRejectsUnequalInputsAndReleasesItsReservationOnFailureOrCancellation()
    {
        var sample = Expanded(1, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("TBN validation must precede reservation.");

        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, 0, [511u], [], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, -1, [511u], [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, int.MaxValue, [511u, 511u], [511u, 511u], pose,
            sample.Source.Revision, sample.Source.SnapshotSequence, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, 0, [511u, 511u], [511u, 511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve, maximumVertices: 1));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, 0, [511u], [511u], pose, sample.Source.Revision with { PhysicalSourceStamp = "changed" },
            sample.Source.SnapshotSequence, NeverReserve));

        var baseline = budget.Used;
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, 0, [0u], [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, budget.Reserve));
        Assert.Equal(baseline, budget.Used);

        using var cancel = new CancellationTokenSource();
        IDisposable CancelAfterReserve(long bytes, string category)
        {
            var reservation = budget.Reserve(bytes, category);
            cancel.Cancel();
            return reservation;
        }
        Assert.Throws<OperationCanceledException>(() => new NativeSkinningVectorEvaluator().EvaluateTbnWindow(
            attributes.Document, 0, 0, [511u], [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, CancelAfterReserve, token: cancel.Token));
        Assert.Equal(baseline, budget.Used);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(4, 1)]
    [InlineData(3, 2)]
    [InlineData(int.MaxValue, 2)]
    public void InvalidPositionWindowRangeFailsBeforeReservation(int baseVertex, int windowCount)
    {
        var sample = Expanded(1, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("Window validation must precede output reservation.");
        var positions = windowCount == 1 ? [Vector3.One] : new[] { Vector3.One, Vector3.One };

        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, baseVertex, positions, pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
    }

    [Fact]
    public void WindowVertexLimitAppliesToTheWindowLength()
    {
        var sample = Expanded(1, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("Window limit must precede reservation.");

        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 1, [Vector3.One, Vector3.One], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve, maximumVertices: 1));
    }

    [Fact]
    public void WindowIdentityGroupRevisionAndSequenceFencesFailBeforeReservation()
    {
        var sample = Expanded(4, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        using var wrongGroupPose = ReadPose(sample with
            { Source = sample.Source with { GroupIndex = sample.Source.GroupIndex + 1 } }, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("Source validation must precede reservation.");

        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 1, [Vector3.One], pose, sample.Source.Revision with { PhysicalSourceStamp = "changed" },
            sample.Source.SnapshotSequence, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 1, [Vector3.One], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence + 1, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 1, [Vector3.One], wrongGroupPose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
    }

    [Fact]
    public void WindowErrorsAndCancellationReleaseOnlyTheirOutputReservation()
    {
        var sample = Expanded(4, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        var baseline = budget.Used;

        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 2, [new Vector3(float.NaN, 0, 0)], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, budget.Reserve));
        Assert.Equal(baseline, budget.Used);

        using var cancel = new CancellationTokenSource();
        IDisposable CancelAfterReserve(long bytes, string category)
        {
            var reservation = budget.Reserve(bytes, category);
            cancel.Cancel();
            return reservation;
        }
        Assert.Throws<OperationCanceledException>(() => new NativeSkinningPositionEvaluator().EvaluateWindow(
            attributes.Document, 0, 2, [Vector3.One], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, CancelAfterReserve, token: cancel.Token));
        Assert.Equal(baseline, budget.Used);
    }

    [Fact]
    public void VectorWindowFailureReleasesOutputReservation()
    {
        var sample = Expanded(4, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        var baseline = budget.Used;

        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().EvaluateWindow(
            attributes.Document, 0, 2, [0u], [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, budget.Reserve));
        Assert.Equal(baseline, budget.Used);
    }

    [Fact]
    public void LegacyEvaluatorsContinueRejectingPartialBuffers()
    {
        var sample = Expanded(4, VectorRows(false));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("Legacy partial input must fail before reservation.");

        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().Evaluate(
            attributes.Document, 0, [Vector3.One], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(
            attributes.Document, 0, [511u], [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void DecodeSkinSourceReturnsTheStoredVertexStreams(uint mode)
    {
        var sample = Expanded(mode, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);

        var geometry = new NativeMeshGeometryDecoder().DecodeSkinSource(sample.Mesh, sample.Gpu, 0,
            attributes.Document);

        var primitive = Assert.Single(geometry.Primitives);
        Assert.Equal(4, primitive.Positions.Count);
        Assert.Equal(new Vector3(1, 0, 2), primitive.Positions[0]);
        Assert.Equal(new Vector3(3, 2, 2), primitive.Positions[2]);
        Assert.Equal(new Vector3(0, 0, 1), Assert.IsType<Vector3[]>(primitive.Normals)[0]);
        if (mode == 1) Assert.Null(primitive.Tangents);
        else Assert.Equal(new Vector4(1, 0, 0, -1), Assert.IsType<Vector4[]>(primitive.Tangents)[0]);
    }

    [Fact]
    public void StaticGeometryDecodeStillRejectsSkinModesAndPalettes()
    {
        var sample = Expanded(1, VectorRows(false));
        Assert.Equal("NativeMeshPartProfileUnsupported", Assert.Throws<InvalidDataException>(() =>
            new NativeMeshGeometryDecoder().Decode(sample.Mesh, sample.Gpu, 0)).Message);

        var part = sample.Mesh.Groups[0].Parts[0] with { TopologySelector = 0, Palette = [17] };
        var paletteMesh = sample.Mesh with { Groups = [sample.Mesh.Groups[0] with { Parts = [part] }] };
        Assert.Equal("NativeMeshPartProfileUnsupported", Assert.Throws<InvalidDataException>(() =>
            new NativeMeshGeometryDecoder().Decode(paletteMesh, sample.Gpu, 0)).Message);
    }

    [Fact]
    public void DecodeSkinSourceRequiresUniqueAttributesWithMatchingBufferMode()
    {
        var sample = Expanded(4, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        var decoder = new NativeMeshGeometryDecoder();
        var part = sample.Mesh.Groups[0].Parts[0];
        var modeMismatch = sample.Mesh with { Groups = [sample.Mesh.Groups[0] with
            { Parts = [part with { TopologySelector = 1 }] }] };
        var missing = attributes.Document with { Buffers = Array.Empty<NativeSkinningBufferAttributes>() };
        var duplicate = attributes.Document with
            { Buffers = [.. attributes.Document.Buffers, attributes.Document.Buffers[0]] };

        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, missing));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, duplicate));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(modeMismatch, sample.Gpu, 0, attributes.Document));
    }

    [Fact]
    public void DecodeSkinSourceBoundsAndValidatesTheAttributeBufferCollection()
    {
        var sample = Expanded(1, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        var decoder = new NativeMeshGeometryDecoder();
        var nullEntry = attributes.Document with
            { Buffers = new NativeSkinningBufferAttributes[] { null! } };
        var excessiveEntries = attributes.Document with
            { Buffers = new NativeSkinningBufferAttributes[NativeMeshDataReader.MaximumBuffers + 1] };
        var noSkin = Expanded(0, VectorRows(false));
        using var noSkinAttributes = new NativeSkinningAttributeDecoder().Decode(noSkin.Mesh, noSkin.Gpu, 0, ReserveNoop);
        var nullUnusedEntry = noSkinAttributes.Document with
            { Buffers = new NativeSkinningBufferAttributes[] { null! } };

        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, nullEntry));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, excessiveEntries));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(noSkin.Mesh, noSkin.Gpu, 0, nullUnusedEntry));
    }

    [Fact]
    public void DecodeSkinSourceRejectsUnsupportedModePaletteSourceGroupAndGpuExtent()
    {
        var sample = Expanded(1, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        var decoder = new NativeMeshGeometryDecoder();
        var part = sample.Mesh.Groups[0].Parts[0];
        var unsupported = sample.Mesh with { Groups = [sample.Mesh.Groups[0] with
            { Parts = [part with { TopologySelector = 2 }] }] };
        var palette = sample.Mesh with { Groups = [sample.Mesh.Groups[0] with
            { Parts = [part with { Palette = [1] }] }] };
        var invalidRange = sample.Mesh with { Groups = [sample.Mesh.Groups[0] with
            { Parts = [part with { BaseVertex = 3, VertexCount = 2 }] }] };
        var wrongIdentity = attributes.Document with
            { Identity = attributes.Document.Identity with { Ordinal = attributes.Document.Identity.Ordinal + 1 } };
        var wrongGroup = attributes.Document with { GroupIndex = attributes.Document.GroupIndex + 1 };
        var wrongExtent = attributes.Document with
            { GpuExtent = attributes.Document.GpuExtent with { Offset = attributes.Document.GpuExtent.Offset + 1 } };
        var alternateGpuExtent = attributes.Document.GpuExtent with { Offset = attributes.Document.GpuExtent.Offset + 1 };

        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(unsupported, sample.Gpu, 0, attributes.Document));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(palette, sample.Gpu, 0, attributes.Document));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(invalidRange, sample.Gpu, 0, attributes.Document));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, wrongIdentity));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, wrongGroup));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0, wrongExtent));
        Assert.Throws<InvalidDataException>(() => decoder.DecodeSkinSource(sample.Mesh, sample.Gpu, 0,
            attributes.Document, gpuExtent: alternateGpuExtent));
    }

    [Fact]
    public void SkinningVectorLeaseGettersReusePackedFormatMappingWithoutRenormalizing()
    {
        var sample = Expanded(4, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);
        using var output = new NativeSkinningVectorEvaluator().EvaluateWindow(attributes.Document, 0, 0,
            [512u, 513u], [512u | 1u << 30, 513u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, ReserveNoop);

        Assert.Equal(Vector3.UnitX, output.GetNormalVector(0));
        Assert.Equal(-Vector3.UnitX, output.GetNormalVector(1));
        Assert.Equal(new Vector4(1, 0, 0, -1), output.GetTangentVector(0));
        Assert.Equal(new Vector4(-1, 0, 0, 1), output.GetTangentVector(1));
        Assert.Throws<IndexOutOfRangeException>(() => output.GetNormalVector(2));
        Assert.Throws<IndexOutOfRangeException>(() => output.GetTangentVector(2));
    }

    [Fact]
    public void SkinningVectorLeaseGettersPreservePacked512Boundary()
    {
        var sample = Expanded(4, VectorRows(false, firstXScale: 512f / 511f));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);
        using var output = new NativeSkinningVectorEvaluator().EvaluateWindow(attributes.Document, 0, 0,
            [511u], [511u | 1u << 30], pose, sample.Source.Revision, sample.Source.SnapshotSequence, ReserveNoop);

        Assert.Equal(512u, output.GetNormal(0));
        Assert.Equal(512u | 1u << 30, output.GetTangent(0));
        Assert.Equal(new Vector3(512f / 511f, 0, 0), output.GetNormalVector(0));
        Assert.Equal(new Vector4(512f / 511f, 0, 0, -1), output.GetTangentVector(0));
    }

    [Fact]
    public void SkinningVectorLeaseGettersRespectMissingTangentsAndDisposal()
    {
        var sample = Expanded(1, VectorRows(false));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);
        var output = new NativeSkinningVectorEvaluator().EvaluateWindow(attributes.Document, 0, 0,
            [511u], [], pose, sample.Source.Revision, sample.Source.SnapshotSequence, ReserveNoop);

        Assert.Throws<InvalidOperationException>(() => output.GetTangentVector(0));
        output.Dispose();
        Assert.Throws<ObjectDisposedException>(() => output.GetNormalVector(0));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void NormalOnlyWindowEvaluatesPackedNormalsWithoutTangentInput(uint mode)
    {
        var sample = WithoutTangentDescriptor(Expanded(mode, VectorRows(true)));
        Assert.DoesNotContain(sample.Mesh.VertexBuffers[0].Descriptors, descriptor => descriptor.Semantic is 10 or 11);
        var normals = Enumerable.Repeat(511u << 20, 4).ToArray();
        var tangents = mode == 1 ? Array.Empty<uint>() : Enumerable.Repeat(511u | 1u << 30, 4).ToArray();
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        using var tbn = new NativeSkinningVectorEvaluator().EvaluateWindow(attributes.Document, 0, 2,
            normals.AsSpan(2, 2).ToArray(), tangents.Length == 0 ? Array.Empty<uint>() : tangents.AsSpan(2, 2).ToArray(),
            pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve);
        var normalOnly = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(attributes.Document, 0, 2,
            normals.AsSpan(2, 2).ToArray(), pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, budget.Reserve);

        Assert.False(normalOnly.HasTangents);
        Assert.Equal(tbn.GetNormal(0), normalOnly.GetNormal(0));
        Assert.Equal(tbn.GetNormal(1), normalOnly.GetNormal(1));
        Assert.Equal(511u, normalOnly.GetNormal(0));
        Assert.Equal(513u, normalOnly.GetNormal(1));
        Assert.Throws<InvalidOperationException>(() => { _ = normalOnly.GetTangent(0); });
        if (mode is 4 or 8)
            Assert.Throws<InvalidDataException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateWindow(
                attributes.Document, 0, 2, normals.AsSpan(2, 2).ToArray(), [], pose,
                sample.Source.Revision, sample.Source.SnapshotSequence,
                (_, _) => throw new InvalidOperationException("TBN input validation must precede reservation.")); });
        normalOnly.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = normalOnly.GetNormal(0); });
    }

    [Theory]
    [InlineData(4u, .25f, 4)]
    [InlineData(8u, .125f, 8)]
    public void NormalOnlyWindowMatchesIndependentGeneratedNativeOutput(uint mode, float laneWeight, int laneCount)
    {
        var rows = VectorRows(false);
        WriteFloat(rows, 0, .75f); WriteFloat(rows, 4, -.5f); WriteFloat(rows, 8, .25f); WriteFloat(rows, 12, 0);
        WriteFloat(rows, 16, .2f); WriteFloat(rows, 20, .9f); WriteFloat(rows, 24, -.3f); WriteFloat(rows, 28, 0);
        WriteFloat(rows, 32, -.4f); WriteFloat(rows, 36, .1f); WriteFloat(rows, 40, .85f); WriteFloat(rows, 44, 0);
        var sample = Expanded(mode, rows);
        var weightOffset = 20;
        var indexOffset = mode == 8 ? 36 : 28;
        for (var lane = 0; lane < laneCount; lane++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(16 + weightOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)laneWeight));
            BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(16 + indexOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)0));
        }
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);

        using var output = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(attributes.Document, 0, 0,
            [511u | 256u << 10 | 513u << 20], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, ReserveNoop);

        Assert.False(output.HasTangents);
        Assert.Equal(646253652u, output.GetNormal(0));
    }

    [Theory]
    [InlineData(4u, 7, 0x201801FFu, 0x3642794Cu)]
    [InlineData(8u, 13, 0x1FF80600u, 0x125C8C8Au)]
    public void NormalOnlyWindowMatchesGeneratedMultiJointReference(uint mode, int sourceVertex,
        uint packedInput, uint expectedPackedOutput)
    {
        var sample = WithoutTangentDescriptor(Expanded(mode, MultiJointNormalRows(mode), sourceVertex + 1));
        var stride = mode == 8 ? 52 : 36;
        var weightOffset = mode == 8 ? 20 : 20;
        var indexOffset = mode == 8 ? 36 : 28;
        for (var lane = 0; lane < (int)mode; lane++)
        {
            var weight = (float)(0.1 + 0.037 * lane);
            var at = 16 + sourceVertex * stride;
            BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(at + weightOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)weight));
            BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(at + indexOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)(lane * 3)));
        }
        Assert.DoesNotContain(sample.Mesh.VertexBuffers[0].Descriptors, descriptor => descriptor.Semantic is 10 or 11);
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, ReserveNoop);
        using var pose = ReadPose(sample, ReserveNoop);

        using var output = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(attributes.Document, 0,
            sourceVertex, [packedInput], pose, sample.Source.Revision, sample.Source.SnapshotSequence, ReserveNoop);

        Assert.False(output.HasTangents);
        Assert.Equal(expectedPackedOutput, output.GetNormal(0));
    }

    [Fact]
    public void NormalOnlyWindowValidationAndFailuresReleaseItsReservation()
    {
        var sample = WithoutTangentDescriptor(Expanded(4, VectorRows(false)));
        var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, budget.Reserve);
        IDisposable NeverReserve(long _, string __) => throw new InvalidOperationException("Window checks must precede reservation.");
        Assert.Throws<InvalidDataException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
            attributes.Document, 0, -1, [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve); });
        Assert.Throws<InvalidDataException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
            attributes.Document, 0, 3, [511u, 511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve); });
        Assert.Throws<InvalidDataException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
            attributes.Document, 0, 0, [511u, 511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, NeverReserve, maximumVertices: 1); });

        var baseline = budget.Used;
        Assert.Throws<InvalidDataException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
            attributes.Document, 0, 2, [0u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, budget.Reserve); });
        Assert.Equal(baseline, budget.Used);

        using var cancel = new CancellationTokenSource();
        IDisposable CancelAfterReserve(long bytes, string category)
        {
            var reservation = budget.Reserve(bytes, category);
            cancel.Cancel();
            return reservation;
        }
        Assert.Throws<OperationCanceledException>(() => { _ = new NativeSkinningVectorEvaluator().EvaluateNormalWindow(
            attributes.Document, 0, 2, [511u], pose, sample.Source.Revision,
            sample.Source.SnapshotSequence, CancelAfterReserve, token: cancel.Token); });
        Assert.Equal(baseline, budget.Used);
    }

    private static WindowSample Expanded(uint mode, byte[] rows, int vertexCount = 4)
    {
        var basis = NativeSkinningPositionEvaluatorTests.Sample(mode);
        var stride = mode == 8 ? 52 : mode == 4 ? 36 : 32;
        var weightOffset = mode == 1 ? 16 : 20;
        var indexOffset = mode == 8 ? 36 : mode == 4 ? 28 : 24;
        var lanes = mode == 8 ? 8 : 4;
        var gpu = new byte[(16 + stride * vertexCount + 15) & ~15];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        for (var lane = 0; lane < lanes; lane++)
        {
            var weight = lane == 0 ? 1f : 0f;
            var matrixRowOffset = lane == 0 ? vertex * 3 : 0;
            var at = 16 + vertex * stride;
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(at + weightOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)weight));
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(at + indexOffset + lane * 2),
                BitConverter.HalfToUInt16Bits((Half)matrixRowOffset));
        }
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var at = 16 + vertex * stride;
            WriteFloat(gpu, at, vertex + 1);
            WriteFloat(gpu, at + 4, vertex);
            WriteFloat(gpu, at + 8, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(gpu.AsSpan(at + 12), 511u << 20);
            if (mode != 1)
                BinaryPrimitives.WriteUInt32LittleEndian(gpu.AsSpan(at + 16), 511u | 1u << 30);
        }

        var originalGroup = basis.Mesh.Groups[0];
        var part = originalGroup.Parts[0] with { BaseVertex = 0, VertexCount = checked((uint)vertexCount) };
        var group = originalGroup with { Parts = [part] };
        var originalBuffer = basis.Mesh.VertexBuffers[0];
        var buffer = originalBuffer with { ElementCount = checked((uint)vertexCount) };
        var mesh = basis.Mesh with { VertexBuffers = [buffer], Groups = [group] };
        var source = basis.Source with { MatrixRowsSha256 = Digest(rows), MatrixRowsExtent = basis.Source.MatrixRowsExtent with { Length = rows.Length } };
        return new(mesh, gpu, rows, source);
    }

    private static WindowSample WithoutTangentDescriptor(WindowSample sample)
    {
        var buffer = sample.Mesh.VertexBuffers[0];
        var descriptors = buffer.Descriptors.Where(descriptor => descriptor.Semantic != 10).ToArray();
        return sample with { Mesh = sample.Mesh with { VertexBuffers = [buffer with { Descriptors = descriptors }] } };
    }

    private static byte[] VectorRows(bool rotations, float firstXScale = 1f)
    {
        var bytes = new byte[8 * 48];
        for (var matrix = 0; matrix < 8; matrix++)
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 4; column++)
        {
            float value;
            if (rotations && matrix == 2)
                value = (row, column) switch { (0, 2) => 1, (1, 1) => 1, (2, 0) => -1, _ => 0 };
            else if (rotations && matrix == 3)
                value = (row, column) switch { (0, 2) => -1, (1, 1) => 1, (2, 0) => 1, _ => 0 };
            else
                value = matrix == 0 && row == 0 && column == 0 ? firstXScale :
                    row == column ? 1 : row == 0 && column == 3 ? matrix * 4 : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(matrix * 48 + row * 16 + column * 4),
                BitConverter.SingleToUInt32Bits(value));
        }
        return bytes;
    }

    private static byte[] MultiJointNormalRows(uint mode)
    {
        var rows = VectorRows(false);
        for (var joint = 0; joint < mode; joint++)
        {
            var rowValues = new double[,]
            {
                { 0.71 + 0.031 * joint, 0.12 + 0.017 * joint, -0.23 + 0.011 * joint, 0.03 * joint },
                { -0.18 + 0.013 * joint, 0.83 - 0.019 * joint, 0.09 + 0.007 * joint, -0.02 * joint },
                { 0.27 - 0.009 * joint, -0.16 + 0.023 * joint, 0.64 + 0.015 * joint, 0.04 * joint }
            };
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 4; column++)
                WriteFloat(rows, joint * 48 + row * 16 + column * 4, (float)rowValues[row, column]);
        }
        return rows;
    }

    private static byte[] SingleJointTbnRows()
    {
        var rows = VectorRows(false);
        WriteFloat(rows, 0, .75f); WriteFloat(rows, 4, -.5f); WriteFloat(rows, 8, .25f); WriteFloat(rows, 12, 0);
        WriteFloat(rows, 16, .2f); WriteFloat(rows, 20, .9f); WriteFloat(rows, 24, -.3f); WriteFloat(rows, 28, 0);
        WriteFloat(rows, 32, -.4f); WriteFloat(rows, 36, .1f); WriteFloat(rows, 40, .85f); WriteFloat(rows, 44, 0);
        return rows;
    }

    private static byte[] MultiMatrixTbnRows()
    {
        var rows = new byte[8 * 48];
        float[,] values =
        {
            { .71f, .12f, -.23f, .03f },
            { -.18f, .83f, .09f, -.02f },
            { .27f, -.16f, .64f, .04f },
            { .43f, -.37f, .55f, .08f },
            { -.61f, .29f, .18f, -.06f },
            { .22f, .73f, -.49f, .11f }
        };
        for (var row = 0; row < values.GetLength(0); row++)
        for (var column = 0; column < values.GetLength(1); column++)
            WriteFloat(rows, row * 16 + column * 4, values[row, column]);
        return rows;
    }

    private static NativeSkinningPoseRowLease ReadPose(WindowSample sample,
        NativeSkinningMemoryReservation reserve, NativeSkinningPoseSource? source = null)
    {
        source ??= sample.Source;
        return new NativeSkinningPoseRowsReader().Read(source, sample.Rows, sample.Source.MeshIdentity,
            sample.Source.RigIdentity, sample.Source.Revision, sample.Source.SnapshotSequence, 8, reserve);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void WriteFloat(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToUInt32Bits(value));
    private static IDisposable ReserveNoop(long _, string __) => new NoopReservation();
    private sealed class NoopReservation : IDisposable { public void Dispose() { } }
    private sealed record WindowSample(NativeMeshDataDocument Mesh, byte[] Gpu, byte[] Rows, NativeSkinningPoseSource Source);

    private sealed class Budget
    {
        public long Used { get; private set; }
        public long LastBytes { get; private set; }
        public string? LastCategory { get; private set; }
        public IDisposable Reserve(long bytes, string category)
        {
            Used = checked(Used + bytes); LastBytes = bytes; LastCategory = category;
            return new Reservation(() => Used -= bytes);
        }
        private sealed class Reservation(Action release) : IDisposable
        {
            private Action? pending = release;
            public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
        }
    }
}
