using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Animation;

namespace Deviant.Browser;

public sealed class NativeAnimationStreamLease : IDisposable
{
    private IDisposable? retention;
    private ReadOnlyMemory<byte> bytes;
    internal NativeAnimationStreamLease(ResourceIdentity identity, SourceRevision revision,
        ReadOnlyMemory<byte> bytes, SourceExtent? extent, IDisposable retention)
    { Identity = identity; Revision = revision; this.bytes = bytes; PhysicalExtent = extent; this.retention = retention; }
    public ResourceIdentity Identity { get; }
    public SourceRevision Revision { get; }
    public SourceExtent? PhysicalExtent { get; }
    public ReadOnlyMemory<byte> Bytes => retention is not null ? bytes : throw new ObjectDisposedException(nameof(NativeAnimationStreamLease));
    public void Dispose() { bytes = default; Interlocked.Exchange(ref retention, null)?.Dispose(); }
}

/// <summary>Reads a native animation's bounded raw external stream from its owning source container.</summary>
public static class NativeAnimationStreamReader
{
    public static NativeAnimationStreamLease Read(NativeContainerLease container, ResourceIdentity identity,
        AnimationVersion14StreamDescriptor descriptor, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(descriptor); token.ThrowIfCancellationRequested(); container.RequireDataBuffers();
        var frames = container.Directory.Objects.Where(x => x.Identity == identity && x.NativeType == NativeAnimationDataReader.NativeType).Take(2).ToArray();
        if (frames.Length != 1) throw new InvalidDataException("NativeAnimationIdentity");
        var frame = frames[0];
        using var metadataScratch = container.MemoryBudget.Reserve(checked(frame.RawPayload.Length * 48L + 65536), "animation metadata validation");
        var decoded = new NativeAnimationDataReader().Decode(frame, container.Revision, container.SourceExtents, token: token);
        var actual = AnimationVersion14Streams.ReadDescriptor(decoded.Value ?? throw new InvalidDataException("NativeAnimationMetadataUnavailable"), token);
        if (actual.Sizing != descriptor.Sizing || !actual.PrimaryFlags.Span.SequenceEqual(descriptor.PrimaryFlags.Span) ||
            !actual.SecondaryFlags.Span.SequenceEqual(descriptor.SecondaryFlags.Span) ||
            !actual.ConstantVectors.Span.SequenceEqual(descriptor.ConstantVectors.Span) ||
            !actual.CoefficientLow.Span.SequenceEqual(descriptor.CoefficientLow.Span) ||
            !actual.CoefficientHigh.Span.SequenceEqual(descriptor.CoefficientHigh.Span))
            throw new InvalidDataException("NativeAnimationMetadataMismatch");
        if (descriptor.Sizing.AlignedSourceByteLength > AnimationVersion14SizingCalculator.MaximumAlignedSourceByteLength)
            throw new InvalidDataException("NativeAnimationStreamLimit");
        if (descriptor.Sizing.AlignedSourceByteLength == 0 && frame.WrapperFlag == 0)
            return new(identity, container.Revision, ReadOnlyMemory<byte>.Empty, null, container.AcquireLease());
        var header = frame.RawHeader.AsSpan();
        if (header.Length != 52 || frame.WrapperFlag != 1 || frame.WrapperCount != 1 || header[17] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != frame.RawPayload.Length)
            throw new InvalidDataException("NativeAnimationExternalProfileUnsupported");
        uint storedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        uint decodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        if (storedLength != decodedLength) throw new InvalidDataException("NativeAnimationStreamCompressionUnsupported");
        if (decodedLength != descriptor.Sizing.AlignedSourceByteLength || decodedLength == 0)
            throw new InvalidDataException("NativeAnimationStreamSizeMismatch");
        var directory = container.Directory;
        var layout = container.DataLayout;
        if (directory.RemainderExtent is not { } remainder || remainder.Offset != layout.DecodedPrefixLength ||
            remainder.Length != directory.RemainderBytes.Length || remainder.Length != layout.RawSuffixLength ||
            offset > remainder.Length || storedLength > remainder.Length - offset || layout.RawSuffixExtent is not { } physical)
            throw new InvalidDataException("NativeAnimationStreamExtent");
        var stream = directory.RemainderBytes.Slice(checked((int)offset), checked((int)storedLength));
        long sourceOffset = checked(physical.Offset - container.StoredSource.Extent.Offset + offset);
        if (sourceOffset < 0 || sourceOffset > container.StoredSource.OriginalBytes.Length - stream.Length ||
            !stream.Span.SequenceEqual(container.StoredSource.OriginalBytes.Span.Slice(checked((int)sourceOffset), stream.Length)))
            throw new InvalidDataException("NativeAnimationStreamSourceMismatch");
        var extent = new SourceExtent(physical.RelativeSource, checked(physical.Offset + offset), storedLength,
            Convert.ToHexString(SHA256.HashData(stream.Span)).ToLowerInvariant());
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        return new(identity, container.Revision, stream, extent, container.AcquireLease());
    }
}
