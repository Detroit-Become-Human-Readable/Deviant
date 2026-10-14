using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkeletonBoundaryTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void InvalidEntityReferenceCountsRejectBeforeAccess(int count)
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        var references = new CallbackList<ResourceReference>(count, _ => throw new InvalidOperationException());
        Assert.False(new NativeSkeletonReader().TryRead(sample.Frame, sample.Entity with { OtherReferences = references },
            out var value, out var diagnostic));
        Assert.Null(value); Assert.Equal("NativeSkeletonEntityMetadataInvalid", diagnostic);
        Assert.Equal(0, references.Accesses);
    }

    [Fact]
    public void FixedMetadataBudgetRejectsBeforeCallerCollectionCallbacks()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        var references = new CallbackList<ResourceReference>(0, _ => throw new InvalidOperationException(),
            () => throw new InvalidOperationException("Budget must reject before collection access."));
        Assert.False(new NativeSkeletonReader().TryRead(sample.Frame, sample.Entity with { OtherReferences = references },
            new(MaximumMetadataBytes: 4095), out var value, out var diagnostic));
        Assert.Null(value); Assert.Equal("NativeSkeletonMetadataLimit", diagnostic);
    }

    [Fact]
    public void CancellationAfterInvalidEntityReferenceCountIsPropagated()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        using var stop = new CancellationTokenSource();
        var references = new CallbackList<ResourceReference>(0, _ => throw new InvalidOperationException(),
            () => { stop.Cancel(); return -1; });
        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonReader().TryRead(sample.Frame,
            sample.Entity with { OtherReferences = references }, out _, out _, stop.Token));
        Assert.Equal(0, references.Accesses);
    }

    [Fact]
    public void CancellationAfterInvalidReferenceIndexerIsPropagated()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        using var stop = new CancellationTokenSource();
        var references = new CallbackList<ResourceReference>(1, _ => { stop.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonReader().TryRead(sample.Frame,
            sample.Entity with { OtherReferences = references }, out _, out _, stop.Token));
        Assert.Equal(1, references.Accesses);
    }

    [Fact]
    public void CancellationAfterMissingCachedExtentIsPropagated()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        using var stop = new CancellationTokenSource();
        var fields = new CallbackFields(6, () => stop.Cancel());
        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonReader().TryRead(sample.Frame,
            sample.Entity with { FieldExtents = fields }, out _, out _, stop.Token));
        Assert.Equal(1, fields.Lookups);
    }

    [Fact]
    public void InvalidFieldCountRejectsWithoutDictionaryTraversal()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        var fields = new CallbackFields(-1, () => throw new InvalidOperationException());
        Assert.False(new NativeSkeletonReader().TryRead(sample.Frame, sample.Entity with { FieldExtents = fields },
            out var value, out var diagnostic));
        Assert.Null(value); Assert.Equal("NativeSkeletonEntityMetadataInvalid", diagnostic);
        Assert.Equal(0, fields.Lookups);
    }

    [Fact]
    public void AdmissionUsesBytesCopiedAfterMetadataCallbacks()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        var references = new CallbackList<ResourceReference>(0, _ => throw new InvalidOperationException(), () =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sample.Bytes.AsSpan(sample.SkeletonAt + 8), 14);
            return 0;
        });
        Assert.False(new NativeSkeletonReader().TryRead(sample.Frame, sample.Entity with { OtherReferences = references },
            out var value, out var diagnostic));
        Assert.Null(value); Assert.Equal("NativeSkeletonClassUnsupported", diagnostic);
    }

    [Fact]
    public void CancellationFromInputMemoryPrecedesSnapshotCopy()
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        using var stop = new CancellationTokenSource();
        using var memory = new CallbackMemory(sample.Bytes, stop.Cancel);
        var frame = sample.Frame with { RawPayload = memory.BoundedMemory };
        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonReader().TryRead(frame,
            sample.Entity, out _, out _, stop.Token));
        Assert.Equal(1, memory.SpanCalls);
    }

    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? readCount = null) : IReadOnlyList<T>
    {
        public int Count => readCount?.Invoke() ?? count;
        public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Bounded indexing is required.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CallbackFields(int count, Action lookup) : IReadOnlyDictionary<string, DecodedExtent>
    {
        public int Count => count;
        public int Lookups { get; private set; }
        public bool TryGetValue(string key, out DecodedExtent value)
        { Lookups++; lookup(); value = null!; return false; }
        public DecodedExtent this[string key] => throw new InvalidOperationException();
        public IEnumerable<string> Keys => throw new InvalidOperationException();
        public IEnumerable<DecodedExtent> Values => throw new InvalidOperationException();
        public bool ContainsKey(string key) => throw new InvalidOperationException();
        public IEnumerator<KeyValuePair<string, DecodedExtent>> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CallbackMemory(byte[] bytes, Action accessed) : MemoryManager<byte>
    {
        public int SpanCalls { get; private set; }
        public Memory<byte> BoundedMemory => CreateMemory(bytes.Length);
        public override Span<byte> GetSpan() { SpanCalls++; accessed(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
