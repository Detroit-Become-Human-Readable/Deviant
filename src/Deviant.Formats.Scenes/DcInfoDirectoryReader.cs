using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads a decoded DC_INFO directory prefix while preserving raw entry pairs and provenance.</summary>
public sealed class DcInfoDirectoryReader
{
    public const string Version = "dc-info-v3-1";

    public DecodeResult<DcInfoDirectoryInspection> Inspect(ReadOnlySpan<byte> decodedPrefix,
        ResourceIdentity container, SourceRevision revision, IReadOnlyList<SourceExtent> originalExtents,
        long decodedOffset = 0, DcInfoDirectoryLimits? limits = null,
        IReadOnlyList<DecodeStep>? precedingSteps = null, CancellationToken token = default)
    {
        limits ??= new();
        if (limits.MaximumBytes < 24 || limits.MaximumEntries < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        if (container.IndexContentId != revision.IndexContentId)
            throw new ArgumentException("Container and revision must belong to the same archive.", nameof(container));
        if (decodedOffset < 0 || decodedOffset > long.MaxValue - decodedPrefix.Length)
            throw new ArgumentOutOfRangeException(nameof(decodedOffset));
        ArgumentNullException.ThrowIfNull(originalExtents);
        var provenance = originalExtents.ToArray();
        if (provenance.Any(e => e is null || e.Offset < 0 || e.Length < 0 || e.Offset > long.MaxValue - e.Length ||
            string.IsNullOrWhiteSpace(e.RelativeSource) || e.RelativeSource.Contains(':') || e.RelativeSource.StartsWith('/') ||
            e.RelativeSource.StartsWith('\\') || e.RelativeSource.Split('/', '\\').Any(p => p is ".." or "." or "")))
            throw new ArgumentException("Original extents require valid relative source names and ranges.", nameof(originalExtents));
        var preceding = precedingSteps?.ToArray() ?? [];
        DecodeResult<DcInfoDirectoryInspection> Fail(string diagnostic) => new(null, container, revision,
            provenance, [.. preceding, new(nameof(DcInfoDirectoryReader), Version, "little-endian")],
            [diagnostic], [], new Dictionary<string, string>());
        token.ThrowIfCancellationRequested();
        if (decodedPrefix.Length > limits.MaximumBytes) return Fail("DirectoryByteLimit");
        if (decodedPrefix.Length < 16) return Fail("TruncatedDirectoryHeader");
        if (!decodedPrefix[..8].SequenceEqual("DC_INFO "u8)) return Fail("InvalidDirectorySignature");
        var version = Word(decodedPrefix, 8); var bodyLength = Word(decodedPrefix, 12);
        if (version != 3) return Fail("UnsupportedDirectoryVersion");
        if (bodyLength < 8) return Fail("InvalidDirectoryBodyLength");
        if (decodedPrefix.Length < 24) return Fail("TruncatedDirectoryBody");
        var unknown = Word(decodedPrefix, 16); var count = Word(decodedPrefix, 20);
        if (count > limits.MaximumEntries) return Fail("DirectoryEntryLimit");
        var expectedLength = 8L + count * 8L;
        if (bodyLength != expectedLength) return Fail("DirectoryCountLengthMismatch");
        if (bodyLength > decodedPrefix.Length - 16) return Fail("TruncatedDirectoryEntries");
        var directoryLength = checked(16 + (int)bodyLength);
        var entries = new DcInfoDirectoryEntry[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            if ((ordinal & 1023) == 0) token.ThrowIfCancellationRequested();
            var offset = 24 + ordinal * 8;
            entries[ordinal] = new(ordinal, Word(decodedPrefix, offset), Word(decodedPrefix, offset + 4),
                new(container, decodedOffset + offset, 8));
        }
        var duplicates = entries.GroupBy(e => (e.Word0, e.Word1)).Where(g => g.Count() > 1)
            .Select(g => new DcInfoDuplicate(g.Key.Word0, g.Key.Word1, g.Select(e => e.Ordinal).ToArray())).ToArray();
        token.ThrowIfCancellationRequested();
        var digest = Convert.ToHexString(SHA256.HashData(decodedPrefix[..directoryLength])).ToLowerInvariant();
        var value = new DcInfoDirectoryInspection(version, bodyLength, unknown, count,
            new(container, decodedOffset, directoryLength), entries, duplicates,
            directoryLength == decodedPrefix.Length ? null : new(container, decodedOffset + directoryLength, decodedPrefix.Length - directoryLength));
        return new(value, container, revision, provenance,
            [.. preceding, new(nameof(DcInfoDirectoryReader), Version, "little-endian; decoded-prefix", digest)], [], [],
            new Dictionary<string, string>
            {
                ["directoryWord"] = unknown.ToString(CultureInfo.InvariantCulture),
                ["entrySemantics"] = "Raw pairs",
                ["followingPayload"] = value.UnparsedRemainder is null ? "Absent" : "Retained as a decoded extent"
            });
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
