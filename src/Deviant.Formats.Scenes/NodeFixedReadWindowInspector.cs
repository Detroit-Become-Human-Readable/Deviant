using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record NodeReadWindowSlot(int Ordinal, int RelativeOffset, byte[] Bytes, DecodedExtent Extent,
    uint? Word0LittleEndian, uint? Word1LittleEndian, IReadOnlyList<long> DirectoryPairCandidates);
public sealed record NodeFixedReadWindow(DecodedExtent CandidateExtent, IReadOnlyList<NodeReadWindowSlot> Reads);

/// <summary>Inventories the known callback read lengths at an explicitly supplied candidate cursor.</summary>
public sealed class NodeFixedReadWindowInspector
{
    public const string Version = "node-v5-consumption-window-1";
    public const int BaseReadBytes = 172;
    public const int TotalReadBytes = 180;
    public static IReadOnlyList<int> ReadLengths { get; } = Array.AsReadOnly(new[] { 4, 4, 4, 4, 64, 64, 8, 4, 8, 8, 4, 4 });

    public DecodeResult<NodeFixedReadWindow> Inspect(ReadOnlySpan<byte> decodedPrefix, int bodyCandidateOffset,
        ResourceIdentity container, SourceRevision revision, IReadOnlyList<SourceExtent> originalExtents,
        DecodeResult<DcInfoDirectoryInspection>? directory = null, long decodedOffset = 0,
        IReadOnlyList<DecodeStep>? precedingSteps = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(revision); ArgumentNullException.ThrowIfNull(originalExtents);
        if (container.IndexContentId != revision.IndexContentId) throw new ArgumentException("Container and revision must match.", nameof(container));
        if (bodyCandidateOffset < 0 || bodyCandidateOffset > decodedPrefix.Length) throw new ArgumentOutOfRangeException(nameof(bodyCandidateOffset));
        if (decodedOffset < 0 || decodedOffset > long.MaxValue - decodedPrefix.Length) throw new ArgumentOutOfRangeException(nameof(decodedOffset));
        if (directory is not null && (directory.Identity != container || directory.Revision != revision ||
            directory.Value is { } value && (value.Extent.Container != container || value.Entries.Any(e => e.Extent.Container != container))))
            throw new ArgumentException("Directory evidence must have the same owner and revision.", nameof(directory));
        var extents = originalExtents.ToArray(); var preceding = precedingSteps?.ToArray() ?? [];
        if (extents.Any(e => e is null || e.Offset < 0 || e.Length < 0 || e.Offset > long.MaxValue - e.Length ||
            string.IsNullOrWhiteSpace(e.RelativeSource) || e.RelativeSource.Contains(':') || e.RelativeSource.StartsWith('/') ||
            e.RelativeSource.StartsWith('\\') || e.RelativeSource.Split('/', '\\').Any(p => p is ".." or "." or "")))
            throw new ArgumentException("Original extents require relative source names and valid ranges.", nameof(originalExtents));
        var unknown = new Dictionary<string, string> { ["cursorOrigin"] = "Caller-supplied candidate", ["readSemantics"] = "Unassigned", ["recordBoundary"] = "Unknown" };
        token.ThrowIfCancellationRequested();
        if (decodedPrefix.Length > 16 * 1024 * 1024 || decodedPrefix.Length - bodyCandidateOffset < TotalReadBytes)
            return new(null, container, revision, extents, preceding,
                [decodedPrefix.Length > 16 * 1024 * 1024 ? "NodeWindowByteLimit" : "TruncatedNodeReadWindow"], [], unknown);
        var groups = directory?.Value?.Entries.GroupBy(e => (e.Word0, e.Word1)).ToDictionary(g => g.Key, g => g.Select(e => e.Ordinal).ToArray());
        var window = decodedPrefix.Slice(bodyCandidateOffset, TotalReadBytes); var reads = new List<NodeReadWindowSlot>(); var relative = 0;
        foreach (var length in ReadLengths)
        {
            token.ThrowIfCancellationRequested();
            var bytes = window.Slice(relative, length).ToArray();
            uint? first = length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
            uint? second = length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) : null;
            var matches = length == 8 && groups is not null ? groups.GetValueOrDefault((first!.Value, second!.Value)) ?? [] : [];
            reads.Add(new(reads.Count, relative, bytes, new(container, decodedOffset + bodyCandidateOffset + relative, length), first, second, matches));
            relative += length;
        }
        var hash = Convert.ToHexString(SHA256.HashData(window)).ToLowerInvariant();
        return new(new(new(container, decodedOffset + bodyCandidateOffset, TotalReadBytes), reads.ToArray()), container, revision,
            extents, [.. preceding, new(nameof(NodeFixedReadWindowInspector), Version, "known-read-lengths; explicit-candidate-origin", hash)], [], [], unknown);
    }
}
