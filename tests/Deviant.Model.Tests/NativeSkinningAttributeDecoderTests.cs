using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkinningAttributeDecoderTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void NativeProfilesPreserveStoredLanesAndProjectEffectiveWeights(uint mode)
    {
        var sample = Create(mode); var budget = new Budget();
        using (var lease = new NativeSkinningAttributeDecoder().Decode(sample.Document, sample.Gpu, 0, budget.Reserve))
        {
            var buffer = Assert.Single(lease.Document.Buffers);
            Assert.Equal(mode, buffer.StoredMode); Assert.Equal(2, buffer.VertexCount);
            Assert.Equal(mode == 8 ? 8 : 4, buffer.StoredLaneCount);
            Assert.Equal((int)mode, buffer.ActiveInfluenceCount);
            Assert.Equal(new NativeMatrixRowInfluence(3, .25f), buffer.GetStoredInfluence(0, 1));
            Assert.Equal(.5f, buffer.GetStoredInfluence(0, 0).RawWeight);
            var total = Enumerable.Range(0, buffer.ActiveInfluenceCount)
                .Sum(lane => buffer.GetNormalizedInfluence(0, lane).Weight);
            Assert.Equal(1f, total, 6);
            if (mode == 1) Assert.Equal(1f, buffer.GetNormalizedInfluence(0, 0).Weight);
            Assert.True(budget.Used > 0);
        }
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void SourceWeightsRemainSeparateFromKernelNormalization()
    {
        var sample = Create(4); WriteHalf(sample.Gpu, sample.WeightsOffset, 3);
        var budget = new Budget();
        using var lease = new NativeSkinningAttributeDecoder().Decode(sample.Document, sample.Gpu, 0, budget.Reserve);
        var buffer = Assert.Single(lease.Document.Buffers);
        Assert.Equal(3, buffer.GetStoredInfluence(0, 0).RawWeight);
        Assert.Equal(3f / 3.5f, buffer.GetNormalizedInfluence(0, 0).Weight, 6);
        Assert.Equal(0u, buffer.GetNormalizedInfluence(0, 0).MatrixRowOffset);
    }

    [Fact]
    public void AttributeExtentsUseTheCallerGpuOriginAndLeaseDisposesOnce()
    {
        var sample = Create(8); var budget = new Budget();
        var lease = new NativeSkinningAttributeDecoder().Decode(sample.Document, sample.Gpu, 0, budget.Reserve,
            gpuExtent: new(sample.Document.Identity, 200, sample.Gpu.Length));
        var buffer = Assert.Single(lease.Document.Buffers);
        Assert.Equal(200 + sample.WeightsOffset, buffer.AttributeExtents[6].Offset);
        Assert.Equal(60, buffer.AttributeExtents[6].Length);
        lease.Dispose(); lease.Dispose(); Assert.Equal(0, budget.Used);
        Assert.Throws<ObjectDisposedException>(() => lease.Document);
    }

    [Theory]
    [InlineData(-1f, "NativeSkinningWeightInvalid", false)]
    [InlineData(float.PositiveInfinity, "NativeSkinningWeightInvalid", false)]
    [InlineData(float.NaN, "NativeSkinningWeightInvalid", false)]
    [InlineData(1.5f, "NativeSkinningMatrixOffsetInvalid", true)]
    [InlineData(-3f, "NativeSkinningMatrixOffsetInvalid", true)]
    [InlineData(float.NaN, "NativeSkinningMatrixOffsetInvalid", true)]
    public void InvalidHalfValuesReleaseEveryReservation(float value, string code, bool index)
    {
        var sample = Create(4); WriteHalf(sample.Gpu, index ? sample.IndicesOffset : sample.WeightsOffset, value);
        AssertFailure(sample.Document, sample.Gpu, code);
    }

    [Fact]
    public void ZeroWeightsFailForBlendedProfilesButRigidProjectionRemainsOne()
    {
        foreach (var mode in new[] { 1u, 4u, 8u })
        {
            var sample = Create(mode);
            for (var lane = 0; lane < 4; lane++) WriteHalf(sample.Gpu, sample.WeightsOffset + lane * 2, 0);
            if (mode == 8)
                for (var lane = 0; lane < 4; lane++) WriteHalf(sample.Gpu, sample.WeightsOffset + 8 + lane * 2, 0);
            if (mode != 1) AssertFailure(sample.Document, sample.Gpu, "NativeSkinningWeightSumInvalid");
            else
            {
                var budget = new Budget();
                using var lease = new NativeSkinningAttributeDecoder().Decode(sample.Document, sample.Gpu, 0, budget.Reserve);
                Assert.Equal(0, lease.Document.Buffers[0].GetStoredInfluence(0, 0).RawWeight);
                Assert.Equal(1, lease.Document.Buffers[0].GetNormalizedInfluence(0, 0).Weight);
            }
        }
    }

    [Fact]
    public void DescriptorFailuresDoNotReturnPartialAttributes()
    {
        var sample = Create(8); var b = sample.Document.VertexBuffers[0];
        var weights = b.Descriptors.Single(d => d.Semantic == 6);
        var variants = new[]
        {
            (b with { Descriptors = b.Descriptors.Where(d => d.Semantic != 9).ToArray() }, "NativeSkinningAttributeMissing"),
            (b with { Descriptors = [.. b.Descriptors, weights] }, "NativeSkinningAttributeDuplicate"),
            (b with { Descriptors = b.Descriptors.Select(d => d.Semantic == 6 ? d with { Word = d.Word ^ (3u << 16) } : d).ToArray() }, "NativeSkinningAttributeFormatUnsupported"),
            (b with { Descriptors = b.Descriptors.Select(d => d.Semantic == 6 ? d with { Word = (d.Word & ~0xff00u) | 255u << 8 } : d).ToArray() }, "NativeVertexDescriptorRangeInvalid")
        };
        foreach (var (buffer, code) in variants)
            AssertFailure(sample.Document with { VertexBuffers = [buffer] }, sample.Gpu, code);
    }

    [Fact]
    public void MixedBufferModesAcrossGroupsAndUnsupportedModesAreRejected()
    {
        var sample = Create(4); var group = sample.Document.Groups[0];
        AssertFailure(sample.Document with
        { Groups = [group, group with { Parts = [group.Parts[0] with { TopologySelector = 0 }] }] },
            sample.Gpu, "NativeSkinningBufferModeConflict");
        AssertFailure(sample.Document with
        { Groups = [group with { Parts = [group.Parts[0] with { TopologySelector = 2 }] }] },
            sample.Gpu, "NativeSkinningModeUnsupported");
    }

    [Fact]
    public void LayoutTruncationAndForeignGpuIdentityFail()
    {
        var sample = Create(4);
        AssertFailure(sample.Document, sample.Gpu[..^1], "NativeMeshGpuTruncated");
        AssertFailure(sample.Document, [.. sample.Gpu, 0], "NativeMeshGpuEndMismatch");
        var budget = new Budget();
        var error = Assert.Throws<InvalidDataException>(() => new NativeSkinningAttributeDecoder().Decode(
            sample.Document, sample.Gpu, 0, budget.Reserve,
            gpuExtent: new(sample.Document.Identity with { MemberPath = "other-mesh" }, 0, sample.Gpu.Length)));
        Assert.Equal("NativeMeshGpuSourceMismatch", error.Message); Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void LimitsBudgetFailureAndCancellationReleaseScratch()
    {
        var sample = Create(8); var budget = new Budget();
        Assert.Throws<InvalidDataException>(() => new NativeSkinningAttributeDecoder().Decode(
            sample.Document, sample.Gpu, 0, budget.Reserve, new(MaximumVertices: 1)));
        Assert.Equal(0, budget.Used);
        budget.Maximum = 8000;
        Assert.Throws<InsufficientMemoryException>(() => new NativeSkinningAttributeDecoder().Decode(
            sample.Document, sample.Gpu, 0, budget.Reserve));
        Assert.Equal(0, budget.Used);
        budget.Maximum = long.MaxValue; using var cancellation = new CancellationTokenSource();
        IDisposable CancelOnAttributes(long bytes, string category)
        {
            var result = budget.Reserve(bytes, category);
            if (category == "native skinning attributes") cancellation.Cancel();
            return result;
        }
        Assert.Throws<OperationCanceledException>(() => new NativeSkinningAttributeDecoder().Decode(
            sample.Document, sample.Gpu, 0, CancelOnAttributes, token: cancellation.Token));
        Assert.Equal(0, budget.Used);
    }

    private static void AssertFailure(NativeMeshDataDocument document, byte[] gpu, string code)
    {
        var budget = new Budget();
        var error = Assert.Throws<InvalidDataException>(() => new NativeSkinningAttributeDecoder().Decode(document, gpu, 0, budget.Reserve));
        Assert.Equal(code, error.Message); Assert.Equal(0, budget.Used);
    }

    private static Sample Create(uint mode)
    {
        var owner = new ResourceIdentity("fixture-index", 12, "mesh-record");
        var extent = new DecodedExtent(owner, 0, 512);
        var stride = mode == 8 ? 52 : mode == 4 ? 36 : 32;
        var weights = mode == 1 ? 16 : 20;
        var indices = mode == 8 ? 36 : mode == 4 ? 28 : 24;
        NativeVertexDescriptor D(byte offset, byte format, byte semantic) =>
            new(3u | (uint)offset << 8 | (uint)format << 16 | (uint)semantic << 24, extent);
        var descriptors = new List<NativeVertexDescriptor> { D(0, 2, 0), D(12, 13, 1), D((byte)weights, 6, 6), D((byte)indices, 6, 7) };
        if (mode != 1) descriptors.Add(D(16, 13, 10));
        if (mode == 8) { descriptors.Add(D(28, 6, 8)); descriptors.Add(D(44, 6, 9)); }
        var buffer = new NativeVertexBuffer(2, 0, descriptors, [new(0, null), new(0, null), new(0, null), new((uint)stride, null)]);
        var bounds = new NativeMeshBounds(new float[15], Vector4.Zero, extent);
        var material = new ResourceReference(IdentifierNamespace.Native, "material", owner, 2133);
        var part = new NativeMeshPart(0, 0, 0, 0, 0, 2, 0, 0, 3, mode, [], material, 0, extent, 0, bounds, extent);
        var document = new NativeMeshDataDocument(owner, 10, extent, [buffer], [3], [new(0, 0, [part], 0, 0, bounds)],
            0, [], null, 0, "Generated mesh", 0, null, 0, 0, 0, extent, 0, []);
        var unalignedLength = 16 + stride * 2;
        var gpu = new byte[(unalignedLength + 15) & ~15];
        BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(2), 1);
        var values = mode == 8 ? new float[] { .5f, .25f, .125f, .0625f, .03125f, .015625f, .0078125f, .0078125f }
            : [.5f, .25f, .125f, .125f];
        for (var vertex = 0; vertex < 2; vertex++)
            for (var lane = 0; lane < values.Length; lane++)
            {
                WriteHalf(gpu, 16 + vertex * stride + weights + lane * 2, values[lane]);
                WriteHalf(gpu, 16 + vertex * stride + indices + lane * 2, lane * 3);
            }
        return new(document, gpu, 16 + weights, 16 + indices);
    }

    private static void WriteHalf(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), BitConverter.HalfToUInt16Bits((Half)value));
    private sealed record Sample(NativeMeshDataDocument Document, byte[] Gpu, int WeightsOffset, int IndicesOffset);
    private sealed class Budget
    {
        public long Used; public long Maximum = long.MaxValue;
        public IDisposable Reserve(long bytes, string category)
        {
            if (bytes > Maximum - Used) throw new InsufficientMemoryException();
            Used += bytes; return new Reservation(() => Used -= bytes);
        }
        private sealed class Reservation(Action release) : IDisposable
        {
            private Action? remaining = release;
            public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
        }
    }
}
