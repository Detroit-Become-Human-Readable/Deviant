using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Enumerates bounded tagged framing and stored tokens while leaving DATA and object schemas opaque.</summary>
public sealed class PhysicsTaggedMetadataReader
{
    public const string Version = "physics-tagged-metadata-v1";

    /// <summary>The input envelope must remain available and unchanged throughout this synchronous read.</summary>
    public DecodeResult<PhysicsTaggedMetadataDocument> Read(PhysicsEnvelopeDocument envelope,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        PhysicsTaggedMetadataLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested(); limits ??= new(); ValidateLimits(limits);
        var physicalCount = physicalExtents.Count; token.ThrowIfCancellationRequested();
        var stepCount = precedingSteps?.Count ?? 0; token.ThrowIfCancellationRequested();
        DecodeResult<PhysicsTaggedMetadataDocument> Early(string code) => new(null, envelope.Identity, envelope.Revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents || stepCount < 0 || stepCount > limits.MaximumPrecedingSteps)
            return Early("TaggedMetadataContextLimit");
        if (!ValidEnvelope(envelope)) return Early("TaggedMetadataEnvelopeMismatch");
        long accounting = 4096L + physicalCount * 8L + stepCount * 8L + TextBytes(envelope.Identity.IndexContentId) +
            TextBytes(envelope.Identity.MemberPath) + TextBytes(envelope.Revision.IndexContentId) + TextBytes(envelope.Revision.PhysicalSourceStamp) +
            TextBytes(envelope.EmbeddedExtent.Container.IndexContentId) + TextBytes(envelope.EmbeddedSha256);
        if (accounting > limits.MaximumMetadataBytes) return Early("TaggedMetadataContextLimit");
        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physical.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = physicalExtents[index]; token.ThrowIfCancellationRequested();
            if (extent is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                extent.RelativeSource is null || extent.RelativeSource.Length > 4096 || string.IsNullOrWhiteSpace(extent.RelativeSource) ||
                extent.RelativeSource.Contains(':') || extent.RelativeSource.StartsWith('/') || extent.RelativeSource.StartsWith('\\') ||
                extent.RelativeSource.Split('/', '\\').Any(part => part is "" or "." or "..")) return Early("TaggedMetadataPhysicalExtentInvalid");
            accounting += 96 + TextBytes(extent.RelativeSource) + TextBytes(extent.PayloadSha256);
            if (accounting > limits.MaximumMetadataBytes) return Early("TaggedMetadataContextLimit");
            physical[index] = extent;
        }
        var steps = new DecodeStep[stepCount];
        for (var index = 0; index < steps.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var step = precedingSteps![index]; token.ThrowIfCancellationRequested();
            if (step is null) return Early("TaggedMetadataStepInvalid");
            accounting += 96 + TextBytes(step.Decoder) + TextBytes(step.Version) + TextBytes(step.Options) + TextBytes(step.PayloadSha256);
            if (accounting > limits.MaximumMetadataBytes) return Early("TaggedMetadataContextLimit");
            steps[index] = step;
        }
        token.ThrowIfCancellationRequested();
        if (!ValidEnvelope(envelope)) return Early("TaggedMetadataEnvelopeMismatch");
        var scan = new Scan(envelope, limits, token, accounting);
        scan.Walk(0, envelope.EmbeddedBytes.Length, 0, null);
        var value = new PhysicsTaggedMetadataDocument(envelope.Identity, envelope.Revision, envelope.EmbeddedExtent,
            envelope.EmbeddedSha256, scan.Chunks.ToArray(), scan.Tokens.ToArray(),
            scan.Diagnostics.Count == 0 ? PhysicsTaggedMetadataCoverage.CompleteFraming : PhysicsTaggedMetadataCoverage.Partial,
            scan.Unknown.ToArray(), scan.BoundedRemainder, scan.Accounting);
        token.ThrowIfCancellationRequested();
        return new(value, envelope.Identity, envelope.Revision, physical, [.. steps, Step()], scan.Diagnostics.ToArray(), [],
            new Dictionary<string, string> { ["EmbeddedTypes"] = "Unmapped", ["ObjectInstances"] = "Unmapped" });
    }

    private static bool ValidEnvelope(PhysicsEnvelopeDocument input)
    {
        var identity = input.Identity; var extent = input.EmbeddedExtent;
        if (identity.IndexContentId is null || identity.IndexContentId.Length > 4096 || string.IsNullOrWhiteSpace(identity.IndexContentId) ||
            identity.Ordinal < 0 || identity.IndexContentId != input.Revision.IndexContentId || input.ClassCount != 1 || input.SerializedVersion != 5 ||
            identity.MemberPath is null || identity.MemberPath.Length > 4096 || extent.Container.MemberPath is not null ||
            extent.Container.IndexContentId != identity.IndexContentId || extent.Container.Ordinal != identity.Ordinal ||
            input.PayloadExtent.Container != extent.Container || input.PayloadExtent.Offset < 0 ||
            input.PayloadExtent.Offset > long.MaxValue - input.PayloadExtent.Length || input.PayloadExtent.Length != input.RawPayload.Length ||
            input.RawPayload.Length < 20 || input.EmbeddedLength != input.EmbeddedBytes.Length || extent.Length != input.EmbeddedBytes.Length ||
            !input.RawPayload[20..].Equals(input.EmbeddedBytes) ||
            extent.Offset != input.PayloadExtent.Offset + 20 || extent.Offset > long.MaxValue - extent.Length) return false;
        var path = identity.MemberPath.Split('/');
        if (path.Length != 4 || path[0] != "dc-object" || !int.TryParse(path[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directory) ||
            directory < 0 || path[1] != directory.ToString(CultureInfo.InvariantCulture) || path[2] != "2201" ||
            path[3] != input.NativeId.ToString(CultureInfo.InvariantCulture)) return false;
        var raw = input.RawPayload.Span;
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != 1 || !raw.Slice(4, 8).SequenceEqual("PHYSYS__"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]) != 5 || BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]) != input.EmbeddedLength ||
            input.RawPayload.Length - 20 != input.EmbeddedBytes.Length || input.EmbeddedSha256 is not { Length: 64 } || !input.EmbeddedSha256.All(Uri.IsHexDigit)) return false;
        if (input.EmbeddedBytes.Length < 8) return input.EmbeddedHeader is null;
        var header = input.EmbeddedHeader; var embedded = input.EmbeddedBytes.Span; var word = BinaryPrimitives.ReadUInt32BigEndian(embedded);
        return header is not null && header.RawTagBytes is { Length: 4 } && header.RawBigEndianWord == word &&
            header.Lower30Bits == (word & 0x3fffffff) && header.High2Bits == (byte)(word >> 30) &&
            header.RawTagBytes.AsSpan().SequenceEqual(embedded.Slice(4, 4)) && header.Extent.Container == extent.Container &&
            header.Extent.Offset == extent.Offset && header.Extent.Length == 8 && raw.Slice(20, 8).SequenceEqual(embedded[..8]);
    }
    private static DecodeStep Step() => new(nameof(PhysicsTaggedMetadataReader), Version, "big-endian-header-projections; bounded-stored-tokens; opaque-DATA");
    private static long TextBytes(string? text) => text is null ? 0 : 24L + text.Length * 2L;
    private static void ValidateLimits(PhysicsTaggedMetadataLimits limits)
    {
        if (limits.MaximumDepth < 0 || limits.MaximumDepth > 64 || limits.MaximumChunks < 0 || limits.MaximumChunks > 4096 ||
            limits.MaximumTokens < 0 || limits.MaximumTokens > 65_536 || limits.MaximumTokenBytes < 0 || limits.MaximumTokenBytes > 4096 ||
            limits.MaximumLeafBytes < 0 || limits.MaximumLeafBytes > 1024 * 1024 || limits.MaximumMetadataBytes < 0 || limits.MaximumMetadataBytes > 64L * 1024 * 1024 ||
            limits.MaximumPhysicalExtents < 0 || limits.MaximumPhysicalExtents > 262_144 || limits.MaximumPrecedingSteps < 0 || limits.MaximumPrecedingSteps > 4096)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private sealed class Scan(PhysicsEnvelopeDocument envelope, PhysicsTaggedMetadataLimits limits, CancellationToken token, long accounting)
    {
        internal List<PhysicsTaggedChunk> Chunks { get; } = [];
        internal List<PhysicsStoredToken> Tokens { get; } = [];
        internal List<DecodedExtent> Unknown { get; } = [];
        internal List<string> Diagnostics { get; } = [];
        internal long Accounting { get; private set; } = accounting;
        private long? remainderStart;
        private long remainderEnd;
        internal DecodedExtent? BoundedRemainder => remainderStart is { } start
            ? new(envelope.EmbeddedExtent.Container, start, remainderEnd - start) : null;
        internal void Walk(int start, int end, int depth, int? parent)
        {
            token.ThrowIfCancellationRequested();
            if (depth > limits.MaximumDepth) { Partial("TaggedMetadataDepthLimit", start, end - start); return; }
            for (var cursor = start; cursor < end;)
            {
                token.ThrowIfCancellationRequested();
                if (Chunks.Count >= limits.MaximumChunks) { Partial("TaggedMetadataChunkLimit", cursor, end - cursor); return; }
                if (end - cursor < 8) { Partial("TaggedMetadataHeaderTruncated", cursor, end - cursor); return; }
                // Includes record/extents/header/text and both list-capacity and final-array references.
                if (!Reserve(512)) { Partial("TaggedMetadataAllocationLimit", cursor, end - cursor); return; }
                var header = envelope.EmbeddedBytes.Slice(cursor, 8).ToArray();
                var word = BinaryPrimitives.ReadUInt32BigEndian(header); var length = word & 0x3fffffff; var flags = (byte)(word >> 30);
                if (length < 8 || length > end - cursor) { Partial("TaggedMetadataLengthInvalid", cursor, end - cursor); return; }
                var tag = Printable(header.AsSpan(4, 4)) ? Encoding.ASCII.GetString(header, 4, 4) : null;
                var bodyOffset = cursor + 8; var bodyLength = checked((int)length - 8); var ordinal = Chunks.Count;
                ReadOnlyMemory<byte>? ownedBody = null; string? bodyHash = null;
                var container = flags == 0 && tag is "TAG0" or "TYPE" or "INDX";
                var metadataLeaf = flags == 1 && tag is "SDKV" or "TPTR" or "TSTR" or "TNA1" or "FSTR" or "TBDY" or "THSH" or "TPAD" or "ITEM" or "PTCH";
                var data = flags == 1 && tag == "DATA";
                if (metadataLeaf)
                {
                    if (bodyLength > limits.MaximumLeafBytes || !Reserve(192L + bodyLength)) Partial("TaggedMetadataLeafLimit", bodyOffset, bodyLength);
                    else
                    {
                        var copy = new byte[bodyLength];
                        for (var copied = 0; copied < copy.Length;)
                        {
                            token.ThrowIfCancellationRequested(); var count = Math.Min(4096, copy.Length - copied);
                            envelope.EmbeddedBytes.Span.Slice(bodyOffset + copied, count).CopyTo(copy.AsSpan(copied, count)); copied += count;
                        }
                        token.ThrowIfCancellationRequested(); ownedBody = copy; bodyHash = Hash(copy, token);
                    }
                }
                Chunks.Add(new(ordinal, parent, depth, word, flags, length, tag, At(cursor, 8), At(bodyOffset, bodyLength), header, ownedBody, bodyHash));
                if (container) Walk(bodyOffset, cursor + checked((int)length), depth + 1, ordinal);
                else if (metadataLeaf && ownedBody is { } body && tag is "TSTR" or "FSTR") ReadTokens(ordinal, bodyOffset, body);
                else if (!metadataLeaf && !data) Partial("TaggedMetadataUnknownProfile", bodyOffset, bodyLength);
                cursor += checked((int)length);
            }
        }
        private void ReadTokens(int chunk, int absolute, ReadOnlyMemory<byte> body)
        {
            for (var cursor = 0; cursor < body.Length;)
            {
                token.ThrowIfCancellationRequested(); var end = body.Span[cursor..].IndexOf((byte)0);
                var terminated = end >= 0; var length = terminated ? end : body.Length - cursor;
                if (length != 0)
                {
                    if (Tokens.Count >= limits.MaximumTokens) { Partial("TaggedMetadataTokenLimit", absolute + cursor, body.Length - cursor); return; }
                    if (length > limits.MaximumTokenBytes || !Reserve(352L + length * 3L))
                    { Partial("TaggedMetadataTokenByteLimit", absolute + cursor, length); }
                    else
                    {
                        var bytes = body.Slice(cursor, length).ToArray(); var printable = Printable(bytes);
                        var text = printable ? Encoding.ASCII.GetString(bytes) : null;
                        Tokens.Add(new(chunk, cursor, At(absolute + cursor, length), text, terminated, Hash(bytes, token), bytes));
                        if (!printable || !terminated) Partial("TaggedMetadataTokenProfile", absolute + cursor, length);
                    }
                }
                cursor += length + (terminated ? 1 : 0);
            }
        }
        private bool Reserve(long bytes)
        {
            if (bytes > limits.MaximumMetadataBytes - Accounting) return false;
            Accounting += bytes; return true;
        }
        private void Partial(string code, int offset, int length)
        {
            if (!Diagnostics.Contains(code)) Diagnostics.Add(code);
            if (length <= 0) return;
            if (Reserve(128)) Unknown.Add(At(offset, length));
            else
            {
                // One fallback extent is charged in the context's fixed allowance, independent of rejected row count.
                var start = envelope.EmbeddedExtent.Offset + offset; var end = start + length;
                remainderStart = remainderStart is { } old ? Math.Min(old, start) : start;
                remainderEnd = Math.Max(remainderEnd, end);
            }
        }
        private DecodedExtent At(int offset, int length) => new(envelope.EmbeddedExtent.Container, checked(envelope.EmbeddedExtent.Offset + offset), length);
        private static bool Printable(ReadOnlySpan<byte> bytes) { foreach (var value in bytes) if (value < 32 || value > 126) return false; return true; }
        private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var result = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); token.ThrowIfCancellationRequested(); return result;
        }
    }
}
