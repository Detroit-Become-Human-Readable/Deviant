using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;

namespace Deviant.Browser;

public sealed class NativeGpuPackage : IDisposable
{
    private AssetMemoryBudget.Reservation? memory;
    private IDisposable? containerLease;
    private DecodeResult<SegsDocument>? decoded;
    internal NativeGpuPackage(DecodeResult<SegsDocument> decoded, SourceExtent extent,
        AssetMemoryBudget.Reservation memory, IDisposable containerLease)
    { this.decoded = decoded; PhysicalExtent = extent; this.memory = memory; this.containerLease = containerLease; }
    public DecodeResult<SegsDocument> Decoded => decoded ?? throw new ObjectDisposedException(nameof(NativeGpuPackage));
    public SourceExtent PhysicalExtent { get; }
    public void Dispose()
    {
        decoded = null;
        Interlocked.Exchange(ref memory, null)?.Dispose();
        Interlocked.Exchange(ref containerLease, null)?.Dispose();
    }
}

/// <summary>Resolves a framed native GPU package through the existing SEGS decoder.</summary>
public static class NativeGpuReader
{
    public static NativeGpuPackage Read(NativeContainerLease container, ResourceIdentity nativeIdentity,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(nativeIdentity);
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        var frames = container.Directory.Objects.Where(frame => frame.Identity == nativeIdentity).ToArray();
        if (frames.Length != 1) throw Error("NativeGpuIdentity", "The native resource is missing or ambiguous.");
        var frame = frames[0];
        var header = frame.RawHeader.AsSpan();
        if (frame.NativeType is not (2130 or 2133) || header.Length != 52 || frame.WrapperFlag != 1 ||
            frame.WrapperCount != 1 || header[17] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != frame.RawPayload.Length)
            throw Error("NativeGpuProfile", "The native GPU wrapper is unavailable.");
        var storedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        var decodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        var directory = container.Directory;
        var outer = container.SegmentLayout;
        if (decodedLength == 0 || decodedLength > NativeContainerLease.MaximumDecodedBytes || storedLength < 16 ||
            directory.RemainderExtent is not { } remainder || remainder.Offset != outer.DecodedPrefixLength ||
            remainder.Length != directory.RemainderBytes.Length || directory.RemainderBytes.Length != outer.RawSuffixLength ||
            offset > directory.RemainderBytes.Length || storedLength > directory.RemainderBytes.Length - (long)offset ||
            outer.RawSuffixExtent is not { } physicalSuffix)
            throw Error("NativeGpuExtent", "The native GPU package has an invalid source range.");
        var encoded = directory.RemainderBytes.Slice(checked((int)offset), checked((int)storedLength));
        var storedOffset = checked((int)(physicalSuffix.Offset - container.StoredSource.Extent.Offset + offset));
        if (storedOffset < 0 || storedOffset > container.StoredSource.OriginalBytes.Length - encoded.Length ||
            !encoded.Span.SequenceEqual(container.StoredSource.OriginalBytes.Span.Slice(storedOffset, encoded.Length)))
            throw Error("NativeGpuSource", "The native GPU package does not match its archive source.");
        var extent = new SourceExtent(physicalSuffix.RelativeSource, checked(physicalSuffix.Offset + offset), storedLength,
            Convert.ToHexString(SHA256.HashData(encoded.Span)).ToLowerInvariant());
        var reservation = container.MemoryBudget.Reserve(NativeContainerLease.EstimateSegsMemory(encoded.Span, decodedLength), "GPU package decoding");
        IDisposable? borrowed = null;
        try
        {
            borrowed = container.AcquireLease();
            var decoded = new SegsContainerReader().Parse(encoded.Span, frame.Identity, container.Revision, extent,
                limits: new(storedLength, decodedLength), cancellationToken: token);
            if (decoded.Value is not { } package || package.RawSuffix.Length != 0 ||
                package.ActualDecodedLength != decodedLength || package.DeclaredDecodedLength != decodedLength)
                throw Error("NativeGpuDecode", decoded.Diagnostics.Count == 0 ? "The native GPU package is incomplete." : string.Join("; ", decoded.Diagnostics));
            reservation.ReduceTo(NativeContainerLease.SegsRetainedBytes(package));
            token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
            return new(decoded, extent, reservation, borrowed);
        }
        catch { borrowed?.Dispose(); reservation.Dispose(); throw; }
    }

    private static ArchiveException Error(string code, string message) => new(code, message);
}
