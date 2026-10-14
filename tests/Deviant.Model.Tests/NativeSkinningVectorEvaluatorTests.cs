using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkinningVectorEvaluatorTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void RotationTransformsNormalizedVectorsAndCanonicalizesTangentFlag(uint mode)
    {
        var sample = Create(mode); var budget = new Budget();
        for (var matrix = 0; matrix < 8; matrix++)
        {
            Write(sample.Rows, matrix * 48, 0); Write(sample.Rows, matrix * 48 + 4, -1);
            Write(sample.Rows, matrix * 48 + 16, 1); Write(sample.Rows, matrix * 48 + 20, 0);
        }
        using (var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve))
        using (var pose = Pose(sample, budget.Reserve))
        using (var output = new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0, [511u << 20],
            mode == 1 ? [] : [511u | 1u << 31], pose, sample.Source.Revision, 1, budget.Reserve))
        {
            Assert.Equal(511u << 20, output.GetNormal(0)); Assert.Equal(mode != 1, output.HasTangents);
            if (mode != 1) Assert.Equal(511u << 10 | 1u << 30, output.GetTangent(0));
            else Assert.Throws<InvalidOperationException>(() => output.GetTangent(0));
        }
        Assert.Equal(0, budget.Used);
    }

    [Theory]
    [InlineData(.5f, 255u)]
    [InlineData(3f, 509u)]
    [InlineData(-2f, 513u)]
    public void PostTransformPackingPreservesNativeScaleAndLowerClamp(float scale, uint expected)
    {
        var sample = Create(4); var budget = new Budget();
        for (var matrix = 0; matrix < 8; matrix++) Write(sample.Rows, matrix * 48, scale);
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = Pose(sample, budget.Reserve);
        using var output = new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0, [511u], [511u],
            pose, sample.Source.Revision, 1, budget.Reserve);
        Assert.Equal(expected, output.GetNormal(0)); Assert.Equal(expected, output.GetTangent(0));
    }

    [Theory]
    [InlineData(512u, 511u)]
    [InlineData(513u, 513u)]
    [InlineData(1023u, 513u)]
    public void UnpackUsesTheNativeStrictGreaterThan512Branch(uint source, uint expected)
    {
        var sample = Create(1); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = Pose(sample, budget.Reserve);
        using var output = new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0, [source], [],
            pose, sample.Source.Revision, 1, budget.Reserve);
        Assert.Equal(expected, output.GetNormal(0));
    }

    [Fact]
    public void ZeroVectorAndUndefinedQuantizationReleaseTheOutputReservation()
    {
        var sample = Create(4); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using (var pose = Pose(sample, budget.Reserve))
        {
            var baseline = budget.Used;
            Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
                [0], [511u], pose, sample.Source.Revision, 1, budget.Reserve));
            Assert.Equal(baseline, budget.Used);
        }
        for (var matrix = 0; matrix < 8; matrix++) Write(sample.Rows, matrix * 48, 1e20f);
        using var excessivePose = Pose(sample, budget.Reserve);
        var before = budget.Used;
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
            [511u], [511u], excessivePose, sample.Source.Revision, 1, budget.Reserve));
        Assert.Equal(before, budget.Used);
    }

    [Theory]
    [InlineData(4u, 50)]
    [InlineData(8u, 66)]
    public void ZeroWeightDoesNotHideOutOfRangeMatrixRows(uint mode, int offset)
    {
        var sample = Create(mode); var budget = new Budget();
        BinaryPrimitives.WriteUInt16LittleEndian(sample.Gpu.AsSpan(offset), BitConverter.HalfToUInt16Bits((Half)24));
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = Pose(sample, budget.Reserve);
        var baseline = budget.Used;
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
            [511u << 20], [511u], pose, sample.Source.Revision, 1, budget.Reserve));
        Assert.Equal(baseline, budget.Used);
    }

    [Fact]
    public void VectorCountAndStaleFrameFailBeforeOutputReservation()
    {
        var sample = Create(4); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = Pose(sample, budget.Reserve);
        IDisposable Never(long _, string __) => throw new InvalidOperationException("Output reservation must not occur.");
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
            [511u << 20], [], pose, sample.Source.Revision, 1, Never));
        Assert.Throws<InvalidDataException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
            [511u << 20], [511u], pose, sample.Source.Revision, 2, Never));
    }

    [Fact]
    public void CancellationAndRepeatedDisposalPreserveInputOwnership()
    {
        var sample = Create(8); var budget = new Budget();
        using var attributes = new NativeSkinningAttributeDecoder().Decode(sample.Mesh, sample.Gpu, 0, budget.Reserve);
        using var pose = Pose(sample, budget.Reserve);
        var baseline = budget.Used; using var cancel = new CancellationTokenSource();
        IDisposable Reserve(long bytes, string category) { var value = budget.Reserve(bytes, category); cancel.Cancel(); return value; }
        Assert.Throws<OperationCanceledException>(() => new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0,
            [511u << 20], [511u], pose, sample.Source.Revision, 1, Reserve, token: cancel.Token));
        Assert.Equal(baseline, budget.Used);
        var output = new NativeSkinningVectorEvaluator().Evaluate(attributes.Document, 0, [511u << 20], [511u],
            pose, sample.Source.Revision, 1, budget.Reserve);
        output.Dispose(); output.Dispose(); Assert.Equal(baseline, budget.Used);
        Assert.Throws<ObjectDisposedException>(() => output.GetNormal(0));
        Assert.Throws<ObjectDisposedException>(() => output.GetTangent(0));
    }

    private static Generated Create(uint mode)
    {
        var sample = NativeSkinningPositionEvaluatorTests.Sample(mode);
        return new(sample.Mesh, sample.Gpu, sample.Rows, sample.Source);
    }
    private static NativeSkinningPoseRowLease Pose(Generated sample, NativeSkinningMemoryReservation reserve)
    {
        var source = sample.Source with { MatrixRowsSha256 = Convert.ToHexString(SHA256.HashData(sample.Rows)) };
        return new NativeSkinningPoseRowsReader().Read(source, sample.Rows, source.MeshIdentity, source.RigIdentity, source.Revision, 1, 8, reserve);
    }
    private static void Write(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToUInt32Bits(value));
    private sealed record Generated(NativeMeshDataDocument Mesh, byte[] Gpu, byte[] Rows, NativeSkinningPoseSource Source);
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
