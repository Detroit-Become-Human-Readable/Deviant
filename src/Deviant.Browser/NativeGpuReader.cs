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
    /// <summary>Reads a selected GPU package from a full lease or directly from a metadata lease's source range.</summary>
    public static async Task<NativeGpuPackage> ReadAsync(NativeContainerLease container, ResourceIdentity nativeIdentity,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(nativeIdentity);
        if (!container.IsMetadataOnly) return Read(container, nativeIdentity, token);
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        var frames = container.Directory.Objects.Where(frame => frame.Identity == nativeIdentity).Take(2).ToArray();
        if (frames.Length != 1) throw Error("NativeGpuIdentity", "The native resource is missing or ambiguous.");
        var frame = frames[0];
        var header = frame.RawHeader.AsSpan();
        if (frame.NativeType is not (2130 or 2133) || header.Length != 52 || frame.WrapperFlag != 1 ||
            frame.WrapperCount != 1 || header[17] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header) != frame.RawPayload.Length)
            throw Error("NativeGpuProfile", "The native GPU wrapper is unavailable.");

        uint storedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        uint decodedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header[44..]);
        if (container.Directory.DeclaredRemainderExtent is not { } remainder)
            throw Error("NativeGpuExtent", "The native GPU package has no declared remainder extent.");
        var row = container.Snapshot.FindRecord(container.Identity.Ordinal);
        if (row is null || row.Type != 29 || row.Validity != ResourceValidity.Valid ||
            remainder.Container != container.Identity || remainder.Offset < 0 || remainder.Length < 0 ||
            storedLength < SegsContainerReader.HeaderLength || storedLength > NativeContainerLease.MaximumStoredBytes ||
            decodedLength == 0 ||
            decodedLength > NativeContainerLease.MaximumDecodedBytes || storedLength > int.MaxValue ||
            offset > remainder.Length || storedLength > remainder.Length - offset)
            throw Error("NativeGpuExtent", "The native GPU package has an invalid declared source range.");

        long resourceDataStart;
        if (container.MetadataSourceIsDirect)
        {
            // QZIP stores the logical container after its five-byte signature. The metadata reader
            // stops exactly at the first byte after the object frames, where the declared remainder begins.
            resourceDataStart = checked(5L + remainder.Offset);
            if (container.MetadataSource.Extent.Offset != row.Offset ||
                container.MetadataSource.Extent.Length != resourceDataStart ||
                remainder.Length != row.StoredSize - resourceDataStart)
                throw Error("NativeGpuExtent", "The direct native GPU remainder does not match its stored source range.");
        }
        else
        {
            // A compressed outer container stores the complete decoded prefix first and its raw suffix
            // immediately after the SEGS span. Native GPU offsets are relative to that raw suffix.
            var decodedPrefixLength = container.MetadataDecodedPrefixLength;
            if (decodedPrefixLength is null || remainder.Offset != decodedPrefixLength.Value ||
                container.MetadataSource.Extent.Offset != row.Offset ||
                remainder.Length != row.StoredSize - container.MetadataSource.Extent.Length)
                throw Error("NativeGpuExtent", "The compressed native GPU remainder does not match its stored suffix.");
            resourceDataStart = container.MetadataSource.Extent.Length;
        }

        long resourceOffset;
        try { resourceOffset = checked(resourceDataStart + offset); }
        catch (OverflowException) { throw Error("NativeGpuExtent", "The native GPU package range overflows its source."); }
        if (resourceOffset < 0 || resourceOffset > row.StoredSize - (long)storedLength)
            throw Error("NativeGpuExtent", "The native GPU package exceeds its archive resource.");
        var headerBytes = await new ArchiveResourceResolver(container.Snapshot)
            .ReadAsync(container.Identity, resourceOffset, SegsContainerReader.HeaderLength, token).ConfigureAwait(false);
        if (!headerBytes.AsSpan(0, 4).SequenceEqual("segs"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(4)) != 1)
            throw Error("NativeGpuDecode", "The selected native GPU package is not a supported SEGS payload.");
        ushort chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(6));
        uint packageDecodedLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(8));
        uint compressedSpan = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(12));
        if (chunkCount is 0 or > 2048 || packageDecodedLength != decodedLength ||
            packageDecodedLength == 0 || packageDecodedLength > NativeContainerLease.MaximumDecodedBytes ||
            compressedSpan > storedLength || storedLength < SegsContainerReader.HeaderLength)
            throw Error("NativeGpuDecode", "The selected native GPU package header is inconsistent with its wrapper.");

        var estimate = EstimateSegsMemory(storedLength, packageDecodedLength, chunkCount);
        var reservation = container.MemoryBudget.Reserve(estimate, "GPU package decoding");
        SourceExtentVerificationLease? verification = null;
        IDisposable? borrowed = null;
        try
        {
            using var readScratch = container.MemoryBudget.Reserve(Math.Min(4L * 1024 * 1024, storedLength),
                "GPU package range read scratch");
            var encodedBytes = new byte[checked((int)storedLength)];
            headerBytes.CopyTo(encodedBytes, 0);
            var resolver = new ArchiveResourceResolver(container.Snapshot);
            for (int cursor = SegsContainerReader.HeaderLength; cursor < encodedBytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                int length = Math.Min(4 * 1024 * 1024, encodedBytes.Length - cursor);
                var chunk = await resolver.ReadAsync(container.Identity, resourceOffset + cursor, length, token)
                    .ConfigureAwait(false);
                chunk.CopyTo(encodedBytes, cursor); cursor += length;
            }

            var volume = container.Snapshot.Volumes.Single(item => item.Package == row.Package);
            var extent = new SourceExtent(volume.RelativeName, checked(row.Offset + resourceOffset), storedLength,
                Convert.ToHexString(SHA256.HashData(encodedBytes)).ToLowerInvariant());
            verification = await new SourceExtentVerifier().AcquireAsync(container.Snapshot, [extent], token,
                maximumBytes: Math.Max(1, storedLength)).ConfigureAwait(false);
            var decoded = new SegsContainerReader().Parse(encodedBytes, frame.Identity, container.Revision, extent,
                limits: new(storedLength, decodedLength), cancellationToken: token);
            if (decoded.Value is not { } package || package.RawSuffix.Length != 0 ||
                package.ActualDecodedLength != decodedLength || package.DeclaredDecodedLength != decodedLength)
                throw Error("NativeGpuDecode", decoded.Diagnostics.Count == 0 ? "The native GPU package is incomplete." : string.Join("; ", decoded.Diagnostics));

            verification.EnsureUnchanged(); token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
            borrowed = container.AcquireLease();
            container.OwnVerifiedExtent(extent, verification);
            verification = null;
            reservation.ReduceTo(NativeContainerLease.SegsRetainedBytes(package));
            return new(decoded, extent, reservation, borrowed);
        }
        catch
        {
            borrowed?.Dispose(); verification?.Dispose(); reservation.Dispose(); throw;
        }
    }

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

    private static long EstimateSegsMemory(long storedLength, uint decodedLength, ushort chunkCount)
    {
        if (storedLength < SegsContainerReader.HeaderLength || storedLength > int.MaxValue ||
            decodedLength == 0 || decodedLength > NativeContainerLease.MaximumDecodedBytes ||
            chunkCount is 0 or > 2048)
            throw Error("NativeGpuDecode", "The native GPU package exceeds its supported memory bounds.");
        return checked(storedLength + 2L * decodedLength + chunkCount * 512L + (chunkCount + 1L) * 128 + 256 * 1024);
    }

    private static ArchiveException Error(string code, string message) => new(code, message);
}
