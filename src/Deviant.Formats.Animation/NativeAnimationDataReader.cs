using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Animation;

/// <summary>Reads ANIMDATA from a native object whose envelope has class headers without record lengths.</summary>
public sealed class NativeAnimationDataReader
{
    public const uint NativeType = 2033;
    public const string Version = "1";

    public DecodeResult<AnimationDataDocument> Decode(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> sourceExtents, AnimationReadProfile profile = AnimationReadProfile.Version14Blocks,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceExtents); token.ThrowIfCancellationRequested();
        if (frame.Identity.IndexContentId != revision.IndexContentId || frame.NativeType != NativeType ||
            frame.PayloadExtent.Container.IndexContentId != revision.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal)
            throw new ArgumentException("The native animation identity does not match its source.", nameof(frame));
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (sourceExtents.Count == 0 || sourceExtents.Any(x => x.Offset < 0 || x.Length <= 0 ||
                string.IsNullOrWhiteSpace(x.RelativeSource) || string.IsNullOrWhiteSpace(x.PayloadSha256)))
            throw new ArgumentException("Native animation requires original source fingerprints.", nameof(sourceExtents));
        foreach (var extent in sourceExtents) _ = checked(extent.Offset + extent.Length);
        var diagnostics = new List<string>();
        if (frame.RawPayload.Length > AnimationDataReader.MaximumResourceBytes)
            return Result(null, "NativeAnimationSizeLimit", null);
        if (frame.ClassCount != 3) return Result(null, "NativeAnimationClassCount", null);
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out var error, token) || prefix is null)
            return Result(null, error ?? "NativeAnimationClassCount", null);
        var at = prefix.NextClassOffset;
        if (at > frame.RawPayload.Length - 12 || !frame.RawPayload.Span.Slice(at, 8).SequenceEqual("ANIMDATA"u8))
            return Result(null, "NativeAnimationClassMissing", null);
        var encoded = frame.RawPayload.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(at + 8));
        var bodyOffset = at + 12;
        var data = new AnimationRecordSection("ANIMDATA", version, at, bodyOffset,
            checked((uint)(encoded.Length - bodyOffset)), encoded.AsMemory(bodyOffset));
        var loadOffset = at - 12;
        var sections = new AnimationRecordSection[]
        {
            new("COM_CONT", 6, 4, 16, checked((uint)(loadOffset - 16)), encoded.AsMemory(16, loadOffset - 16)),
            new("LOADCONT", 2, loadOffset, at, 0, ReadOnlyMemory<byte>.Empty), data
        };
        var words = new List<AnimationHeaderWord>();
        if (version == 14)
            for (var offset = 0; offset + 4 <= Math.Min(32, data.BodyLength); offset += 4)
                words.Add(new(offset, BinaryPrimitives.ReadUInt32LittleEndian(data.Body.Span[offset..])));
        else diagnostics.Add($"ANIMDATA version {version} has no decoded table layout.");
        var layout = version == 14 && profile != AnimationReadProfile.FramedRecords
            ? AnimationVersion14Reader.Read(data, diagnostics, token) : null;
        if (layout is not null && profile == AnimationReadProfile.Version14Blocks)
            layout = AnimationVersion14Blocks.Read(data, layout, token);
        token.ThrowIfCancellationRequested();
        return Result(new(sections, words.AsReadOnly(), encoded) { Version14Layout = layout }, null, hash);

        DecodeResult<AnimationDataDocument> Result(AnimationDataDocument? value, string? diagnostic, string? payloadHash)
        {
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            return new(value, frame.Identity, revision, sourceExtents.ToArray(),
                [new(nameof(NativeAnimationDataReader), Version, "Native COM_CONT v6 / LOADCONT v2 / ANIMDATA envelope", payloadHash)],
                diagnostics.AsReadOnly(), [], new Dictionary<string, string>());
        }
    }
}
