using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;

namespace Deviant.Browser;

internal sealed record NativeContainerMetadataReadResult(NativeTexturePayload Source,
    DecodeResult<SegsDocument>? Segments, NativeObjectDirectoryDocument Directory,
    AssetMemoryBudget.Reservation CompressionMemory, IReadOnlyList<SourceExtent> PhysicalExtents);

/// <summary>Reads native object frames through their final payload without retaining the trailing data buffers.</summary>
internal static class NativeContainerMetadataReader
{
    public const int MaximumMetadataBytes = 64 * 1024 * 1024;

    public static async Task<NativeContainerMetadataReadResult> ReadAsync(ArchiveSnapshot snapshot,
        ResourceIdentity identity, AssetMemoryBudget budget, List<IDisposable> resources,
        int maximumStoredBytes, int maximumDecodedBytes, CancellationToken token)
    {
        var row = snapshot.Records[checked((int)identity.Ordinal)];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid || row.StoredSize < 16 || row.Offset < 0 ||
            row.Offset > long.MaxValue - row.StoredSize)
            throw new ArchiveException("NativeContainerLimit", "The native container source range is unavailable.");
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var resolver = new ArchiveResourceResolver(snapshot);
        var stored = new BudgetBuffer(budget, resources, maximumStoredBytes, "container metadata source");
        var logical = new BudgetBuffer(budget, resources, MaximumMetadataBytes, "native object metadata");
        using var readScratch = budget.Reserve(4 * 1024 * 1024 + 256 * 1024, "container metadata read scratch");
        await EnsureStored(16).ConfigureAwait(false);
        bool direct = stored.Memory.Span[..5].SequenceEqual("QZIP\0"u8);
        bool compressed = stored.Memory.Span[..4].SequenceEqual("segs"u8);
        if (!direct && !compressed) throw new ArchiveException("NativeContainerSignature", "The native container format is unavailable.");
        DecodeResult<SegsDocument>? segments = null;
        var compressionMemory = budget.Reserve(0, "container metadata decoding"); resources.Add(compressionMemory);
        ReadOnlyMemory<byte> decoded = default;
        int compressedEnd = 0;
        long originalLogicalLength;
        if (compressed)
        {
            var header = stored.Memory.Span;
            if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 1)
                throw new ArchiveException("NativePayloadVersion", "The compressed container version is unavailable.");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            uint span = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (count is < 1 or > 2048 || declared == 0 || declared > maximumDecodedBytes)
                throw new ArchiveException("NativePayloadLimit", "The compressed metadata exceeds its decode limits.");
            long start = ((16L + count * 8L) + 15) & ~15L;
            long end = checked(start + span);
            if (span == 0 || end > row.StoredSize || end > maximumStoredBytes)
                throw new ArchiveException("NativePayloadLimit", "The compressed metadata span exceeds its source or read limits.");
            compressedEnd = checked((int)end);
            await EnsureStored(compressedEnd).ConfigureAwait(false);
            compressionMemory.Dispose();
            compressionMemory = budget.Reserve(NativeContainerLease.EstimateSegsMemory(stored.Memory.Span, maximumDecodedBytes), "container metadata decoding");
            resources.Add(compressionMemory);
            segments = new SegsContainerReader().Parse(stored.Memory.Span, identity, revision, Extent(stored.Memory),
                null, new(maximumStoredBytes, maximumDecodedBytes), token);
            var value = segments.Value ?? throw new ArchiveException("NativeContainerUnavailable", string.Join("; ", segments.Diagnostics));
            compressionMemory.ReduceTo(NativeContainerLease.SegsRetainedBytes(value));
            decoded = value.DecodedPrefix;
            originalLogicalLength = checked(decoded.Length + row.StoredSize - compressedEnd);
        }
        else originalLogicalLength = row.StoredSize - 5;
        await AppendLogical(0, 24).ConfigureAwait(false);
        if (!logical.Memory.Span[..8].SequenceEqual("DC_INFO "u8) || BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[8..]) != 3)
            throw new ArchiveException("NativeDirectoryUnavailable", "The native directory header is unavailable.");
        uint directoryBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[12..]);
        uint objectCount = BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[20..]);
        if (objectCount > NativeObjectDirectoryReader.MaximumObjects || directoryBodyLength != 8L + objectCount * 8L)
            throw new ArchiveException("NativeDirectoryLimit", "The native directory count and framing disagree.");
        int bodyOffset = checked((int)(32L + directoryBodyLength));
        await AppendLogical(logical.Length, bodyOffset - logical.Length).ConfigureAwait(false);
        int dataHeader = bodyOffset - 16;
        if (!logical.Memory.Span.Slice(dataHeader, 8).SequenceEqual("DC_DATA "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[(dataHeader + 8)..]) != 23)
            throw new ArchiveException("NativeDirectoryUnavailable", "The native data header is unavailable.");
        uint declaredBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[(dataHeader + 12)..]);
        if (declaredBodyLength > originalLogicalLength - bodyOffset || direct && declaredBodyLength != originalLogicalLength - bodyOffset)
            throw new ArchiveException("NativeDirectoryUnavailable", "The declared native body exceeds or disagrees with its source range.");
        long bodyEnd = checked(bodyOffset + (long)declaredBodyLength);
        for (int ordinal = 0; ordinal < objectCount; ordinal++)
        {
            token.ThrowIfCancellationRequested(); int frameStart = logical.Length;
            await FrameAppend(frameStart, 13).ConfigureAwait(false);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(logical.Memory.Span[frameStart..]);
            int headerLength = NativeObjectDirectoryReader.GetFrameHeaderLength(logical.Memory.Span[frameStart..]);
            await FrameAppend(logical.Length, frameStart + headerLength - logical.Length).ConfigureAwait(false);
            headerLength = NativeObjectDirectoryReader.GetFrameHeaderLength(logical.Memory.Span[frameStart..]);
            await FrameAppend(logical.Length, frameStart + headerLength - logical.Length).ConfigureAwait(false);
            if (payloadLength > MaximumMetadataBytes || payloadLength > bodyEnd - logical.Length)
                throw new ArchiveException("NativeMetadataLimit", "The native object payload exceeds the metadata or declared-body bounds.");
            await FrameAppend(logical.Length, checked((int)payloadLength)).ConfigureAwait(false);
        }
        resources.Add(budget.Reserve(checked(objectCount * 2048L + 256 * 1024), "native metadata directory frames"));
        var physical = Extent(stored.Memory);
        var parsed = new NativeObjectDirectoryLoader().ReadMetadata(logical.Memory, originalLogicalLength, identity, revision,
            [physical], direct, token);
        var directory = parsed.Value ?? throw new ArchiveException("NativeDirectoryUnavailable", string.Join("; ", parsed.Diagnostics));
        token.ThrowIfCancellationRequested();
        return new(new(identity, row.Type, row.Id, physical, stored.Memory, physical.PayloadSha256!),
            segments, directory, compressionMemory, [physical]);

        SourceExtent Extent(ReadOnlyMemory<byte> bytes) => new(row.VolumeName, row.Offset, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant());
        async Task EnsureStored(long end)
        {
            if (end < 0 || end > row.StoredSize || end > maximumStoredBytes)
                throw new ArchiveException("NativeMetadataLimit", "The metadata read exceeds its source or configured byte limit.");
            stored.EnsureCapacity(checked((int)end));
            while (stored.Length < end)
            {
                token.ThrowIfCancellationRequested(); int length = (int)Math.Min(4 * 1024 * 1024, end - stored.Length);
                var chunk = await resolver.ReadAsync(identity, stored.Length, length, token).ConfigureAwait(false);
                stored.Append(chunk);
            }
        }
        async Task FrameAppend(int at, int length)
        {
            if (at < bodyOffset || length < 0 || at > bodyEnd - length)
                throw new ArchiveException("NativeDirectoryUnavailable", "An object frame exceeds the declared native body.");
            await AppendLogical(at, length).ConfigureAwait(false);
        }
        async Task AppendLogical(int at, int length)
        {
            if (at != logical.Length || length < 0 || at > originalLogicalLength - length || at > MaximumMetadataBytes - length)
                throw new ArchiveException("NativeMetadataLimit", "The native metadata exceeds its logical bounds or memory limit.");
            logical.EnsureCapacity(checked(at + length));
            int fromPrefix = direct ? 0 : Math.Clamp(decoded.Length - at, 0, length);
            if (fromPrefix > 0) logical.Append(decoded.Span.Slice(at, fromPrefix));
            int remaining = length - fromPrefix;
            if (remaining > 0)
            {
                long sourceOffset = direct ? 5L + at : compressedEnd + (long)at + fromPrefix - decoded.Length;
                await EnsureStored(checked(sourceOffset + remaining)).ConfigureAwait(false);
                logical.Append(stored.Memory.Span.Slice(checked((int)sourceOffset), remaining));
            }
        }
    }

    private sealed class BudgetBuffer(AssetMemoryBudget budget, List<IDisposable> resources, int maximum, string category)
    {
        private byte[] bytes = [];
        private AssetMemoryBudget.Reservation? reservation;
        public int Length { get; private set; }
        public ReadOnlyMemory<byte> Memory => bytes.AsMemory(0, Length);
        public void EnsureCapacity(int required)
        {
            if (required < 0 || required > maximum) throw new ArchiveException("NativeMetadataLimit", "The metadata buffer exceeds its byte limit.");
            if (required <= bytes.Length) return;
            int capacity = (int)Math.Min(maximum, Math.Max(required, Math.Max(4096L, bytes.Length * 2L)));
            var nextReservation = budget.Reserve(checked(capacity * 2L + 256), category);
            resources.Add(nextReservation);
            var next = new byte[capacity]; bytes.AsSpan(0, Length).CopyTo(next);
            bytes = next; reservation?.Dispose(); reservation = nextReservation;
        }
        public void Append(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(checked(Length + data.Length)); data.CopyTo(bytes.AsSpan(Length)); Length += data.Length;
        }
    }
}
