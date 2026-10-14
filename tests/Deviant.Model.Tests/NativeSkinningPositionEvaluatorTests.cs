using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkinningPositionEvaluatorTests
{
    [Theory]
    [InlineData(1u, 2f)]
    [InlineData(4u, 5f)]
    [InlineData(8u, 5.5f)]
    public void CapturedRowsAddressNativeLanesAndNormalizeTheWeightedMatrix(uint mode, float expectedX)
    {
        var sample = Sample(mode); var budget = new Budget();
        using (var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve))
        using (var pose = ReadPose(sample, sample.Rows, budget.Reserve))
        using (var output = new NativeSkinningPositionEvaluator().Evaluate(attributes.Document, 0,
            [new(2, 3, 4)], pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve))
        {
            Assert.Equal(new Vector3(expectedX, 3, 4), output.GetPosition(0));
            Assert.Equal(1, output.Count); Assert.Equal(8, pose.MatrixCount);
            Assert.True(budget.Used > 0);
        }
        Assert.Equal(0, budget.Used);
    }

    [Theory]
    [InlineData("mesh")]
    [InlineData("rig")]
    [InlineData("revision")]
    [InlineData("digest")]
    [InlineData("extent")]
    [InlineData("frame")]
    [InlineData("count")]
    public void CaptureMetadataMismatchFailsBeforeReservation(string field)
    {
        var sample = Sample(4); var source = sample.Source;
        source = field switch
        {
            "mesh" => source with { MeshIdentity = source.MeshIdentity with { Ordinal = 90 } },
            "rig" => source with { RigIdentity = source.RigIdentity with { Ordinal = 90 } },
            "revision" => source with { Revision = source.Revision with { PhysicalSourceStamp = "changed" } },
            "digest" => source with { MatrixRowsSha256 = new string('0', 64) },
            "frame" => source with { SnapshotSequence = 2 },
            "count" => source,
            _ => source with { MatrixRowsExtent = source.MatrixRowsExtent with { Length = 1 } }
        };
        Assert.Throws<InvalidDataException>(() => new NativeSkinningPoseRowsReader().Read(source, sample.Rows,
            sample.Source.MeshIdentity, sample.Source.RigIdentity, sample.Source.Revision, sample.Source.SnapshotSequence,
            field == "count" ? 9 : 8,
            (_, _) => throw new InvalidOperationException("Reservation must not occur.")));
    }

    [Fact]
    public void NonfiniteRowsAndPostReservationCancellationReleaseMemory()
    {
        var sample = Sample(4); var budget = new Budget();
        var bytes = (byte[])sample.Rows.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, BitConverter.SingleToUInt32Bits(float.NaN));
        Assert.Throws<InvalidDataException>(() => ReadPose(sample with
            { Source = sample.Source with { MatrixRowsSha256 = Digest(bytes) } }, bytes, budget.Reserve));
        Assert.Equal(0, budget.Used);
        using var cancel = new CancellationTokenSource();
        IDisposable Reserve(long size, string category) { var lease = budget.Reserve(size, category); cancel.Cancel(); return lease; }
        Assert.Throws<OperationCanceledException>(() => ReadPose(sample, sample.Rows, Reserve, cancel.Token));
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void RowDomainFailureReleasesOutputAndLeavesTheInputLeasesUsable()
    {
        var sample = Sample(8); var budget = new Budget();
        BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(66), BitConverter.HalfToUInt16Bits((Half)24));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, sample.Rows, budget.Reserve);
        var baseline = budget.Used;
        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().Evaluate(attributes.Document, 0,
            [new(2, 3, 4)], pose, sample.Source.Revision, sample.Source.SnapshotSequence, budget.Reserve));
        Assert.Equal(baseline, budget.Used); Assert.Equal(24, pose.RowCount);
    }

    [Fact]
    public void StaleEvaluatorFrameFailsBeforeOutputReservation()
    {
        var sample = Sample(4); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, sample.Rows, budget.Reserve);
        var baseline = budget.Used;
        Assert.Throws<InvalidDataException>(() => new NativeSkinningPositionEvaluator().Evaluate(attributes.Document, 0,
            [new(2, 3, 4)], pose, sample.Source.Revision, 2,
            (_, _) => throw new InvalidOperationException("Output reservation must not occur.")));
        Assert.Equal(baseline, budget.Used);
    }

    [Fact]
    public void OutputCancellationReleasesOnlyItsReservation()
    {
        var sample = Sample(4); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = ReadPose(sample, sample.Rows, budget.Reserve);
        var baseline = budget.Used; using var cancel = new CancellationTokenSource();
        IDisposable Reserve(long size, string category) { var lease = budget.Reserve(size, category); cancel.Cancel(); return lease; }
        Assert.Throws<OperationCanceledException>(() => new NativeSkinningPositionEvaluator().Evaluate(attributes.Document, 0,
            [new(2, 3, 4)], pose, sample.Source.Revision, sample.Source.SnapshotSequence, Reserve, token: cancel.Token));
        Assert.Equal(baseline, budget.Used);
    }

    [Fact]
    public void DisposedRowsCannotBeEvaluatedAndRepeatedDisposeDoesNotReleaseTwice()
    {
        var sample = Sample(1); var budget = new Budget(); var pose = ReadPose(sample, sample.Rows, budget.Reserve);
        pose.Dispose(); pose.Dispose(); Assert.Equal(0, budget.Used);
        Assert.Throws<ObjectDisposedException>(() => pose.GetRow(0));
        Assert.Throws<ObjectDisposedException>(() => pose.RowCount);
    }

    private static NativeSkinningPoseRowLease ReadPose(GeneratedSample sample, byte[] bytes,
        NativeSkinningMemoryReservation reserve, CancellationToken token = default) =>
        new NativeSkinningPoseRowsReader().Read(sample.Source, bytes, sample.Source.MeshIdentity,
            sample.Source.RigIdentity, sample.Source.Revision, sample.Source.SnapshotSequence, 8, reserve, token: token);

    internal static GeneratedSample Sample(uint mode)
    {
        var owner = new ResourceIdentity("generated-index", 12, "mesh");
        var rig = new ResourceIdentity(owner.IndexContentId, 13, "rig");
        var revision = new SourceRevision(owner.IndexContentId, "generated-revision");
        var extent = new DecodedExtent(owner, 0, 512);
        var stride = mode == 8 ? 52 : mode == 4 ? 36 : 32;
        var weightOffset = mode == 1 ? 16 : 20;
        var indexOffset = mode == 8 ? 36 : mode == 4 ? 28 : 24;
        NativeVertexDescriptor Descriptor(byte offset, byte format, byte semantic) =>
            new(3u | (uint)offset << 8 | (uint)format << 16 | (uint)semantic << 24, extent);
        var descriptors = new List<NativeVertexDescriptor> { Descriptor(0, 2, 0), Descriptor(12, 13, 1),
            Descriptor((byte)weightOffset, 6, 6), Descriptor((byte)indexOffset, 6, 7) };
        if (mode != 1) descriptors.Add(Descriptor(16, 13, 10));
        if (mode == 8) { descriptors.Add(Descriptor(28, 6, 8)); descriptors.Add(Descriptor(44, 6, 9)); }
        var buffer = new NativeVertexBuffer(1, 0, descriptors, [new(0, null), new(0, null), new(0, null), new((uint)stride, null)]);
        var bounds = new NativeMeshBounds(new float[15], Vector4.Zero, extent);
        var part = new NativeMeshPart(0, 0, 0, 0, 0, 1, 0, 0, 3, mode, [],
            new(IdentifierNamespace.Native, "1", owner, 2133), 0, extent, 0, bounds, extent);
        var mesh = new NativeMeshDataDocument(owner, 10, extent, [buffer], [3], [new(0, 0, [part], 0, 0, bounds)],
            0, [], null, 0, "Generated mesh", 0, null, 0, 0, 0, extent, 0, []);
        var gpu = new byte[(16 + stride + 15) & ~15];
        var weights = mode == 8 ? new float[] { 4, 2, 1, 1, 0, 0, 0, 0 } : [2, 1, 1, 0];
        for (var lane = 0; lane < weights.Length; lane++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(16 + weightOffset + lane * 2), BitConverter.HalfToUInt16Bits((Half)weights[lane]));
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(16 + indexOffset + lane * 2), BitConverter.HalfToUInt16Bits((Half)(lane * 3)));
        }
        var rows = new byte[8 * 48];
        for (var matrix = 0; matrix < 8; matrix++)
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 4; column++)
                {
                    var value = row == column ? 1f : row == 0 && column == 3 ? matrix * 4f : 0f;
                    BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan(matrix * 48 + row * 16 + column * 4), BitConverter.SingleToUInt32Bits(value));
                }
        var source = new NativeSkinningPoseSource(owner, rig, revision, 0, 1, new string('a', 64), Digest(rows), new(rig, 0, rows.Length));
        return new(mesh, gpu, rows, source);
    }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal sealed record GeneratedSample(NativeMeshDataDocument Mesh, byte[] Gpu, byte[] Rows, NativeSkinningPoseSource Source);
    private sealed class Budget
    {
        public long Used;
        public IDisposable Reserve(long bytes, string category)
        { Used += bytes; return new Reservation(() => Used -= bytes); }
        private sealed class Reservation(Action release) : IDisposable
        {
            private Action? pending = release;
            public void Dispose() => Interlocked.Exchange(ref pending, null)?.Invoke();
        }
    }
}
