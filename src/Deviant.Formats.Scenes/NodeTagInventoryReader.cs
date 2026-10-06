using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record NodeTagCandidate(long Ordinal, byte[] HeaderBytes, uint? WordAt8LittleEndian,
    uint? WordAt12LittleEndian, DecodedExtent HeaderExtent, byte[] FollowingBytes, DecodedExtent? FollowingExtent);
public sealed record NodeTagInventory(DecodedExtent ScannedExtent, IReadOnlyList<NodeTagCandidate> Candidates, bool Complete);
public sealed record NodeTagInventoryLimits(int MaximumBytes = 16 * 1024 * 1024,
    int MaximumMatches = 4096, int FollowingContextBytes = 64);

/// <summary>Inventories exact NODE tag occurrences without assigning record or field semantics.</summary>
public sealed class NodeTagInventoryReader
{
    public const string Version = "node-tag-inventory-1";
    public DecodeResult<NodeTagInventory> Scan(ReadOnlySpan<byte> decodedPrefix, ResourceIdentity container,
        SourceRevision revision, IReadOnlyList<SourceExtent> originalExtents, long decodedOffset = 0,
        NodeTagInventoryLimits? limits = null, IReadOnlyList<DecodeStep>? precedingSteps = null,
        CancellationToken token = default)
    {
        limits ??= new();
        if (limits.MaximumBytes < 0 || limits.MaximumMatches < 1 || limits.FollowingContextBytes is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(limits));
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(revision);
        if (container.IndexContentId != revision.IndexContentId) throw new ArgumentException("Container and revision must match.", nameof(container));
        if (decodedOffset < 0 || decodedOffset > long.MaxValue - decodedPrefix.Length) throw new ArgumentOutOfRangeException(nameof(decodedOffset));
        ArgumentNullException.ThrowIfNull(originalExtents);
        var extents = originalExtents.ToArray(); var steps = precedingSteps?.ToArray() ?? [];
        if (extents.Any(e => e is null || e.Offset < 0 || e.Length < 0 || e.Offset > long.MaxValue - e.Length ||
            string.IsNullOrWhiteSpace(e.RelativeSource) || e.RelativeSource.Contains(':') || e.RelativeSource.StartsWith('/') ||
            e.RelativeSource.StartsWith('\\') || e.RelativeSource.Split('/', '\\').Any(p => p is ".." or "." or "")))
            throw new ArgumentException("Original extents require relative source names and valid ranges.", nameof(originalExtents));
        token.ThrowIfCancellationRequested();
        if (decodedPrefix.Length > limits.MaximumBytes)
            return new(null, container, revision, extents, steps, ["NodeInventoryByteLimit"], [], new Dictionary<string, string>());
        var matches = new List<NodeTagCandidate>(); var diagnostics = new List<string>(); var complete = true;
        for (var offset = 0; offset <= decodedPrefix.Length - 8; offset++)
        {
            if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!decodedPrefix.Slice(offset, 8).SequenceEqual("NODE    "u8)) continue;
            if (matches.Count == limits.MaximumMatches) { diagnostics.Add("NodeInventoryMatchLimit"); complete = false; break; }
            var headerLength = Math.Min(16, decodedPrefix.Length - offset);
            var header = decodedPrefix.Slice(offset, headerLength).ToArray();
            var contextLength = Math.Min(limits.FollowingContextBytes, decodedPrefix.Length - offset - headerLength);
            var following = decodedPrefix.Slice(offset + headerLength, contextLength).ToArray();
            matches.Add(new(matches.Count, header,
                headerLength >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) : null,
                headerLength >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) : null,
                new(container, decodedOffset + offset, headerLength), following,
                contextLength == 0 ? null : new(container, decodedOffset + offset + headerLength, contextLength)));
            if (headerLength < 16 && !diagnostics.Contains("TruncatedNodeTagHeader")) diagnostics.Add("TruncatedNodeTagHeader");
        }
        token.ThrowIfCancellationRequested();
        var digest = Convert.ToHexString(SHA256.HashData(decodedPrefix)).ToLowerInvariant();
        return new(new(new(container, decodedOffset, decodedPrefix.Length), matches.ToArray(), complete), container, revision,
            extents, [.. steps, new(nameof(NodeTagInventoryReader), Version, "exact-8-byte-tag; header-words-unassigned", digest)], diagnostics, [],
            new Dictionary<string, string> { ["recordExtent"] = "Unknown", ["callbackCursorOrigin"] = "Unknown", ["followingBytes"] = "Adjacent context" });
    }
}
