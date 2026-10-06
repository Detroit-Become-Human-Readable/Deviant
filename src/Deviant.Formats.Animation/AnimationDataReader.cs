using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Animation;

/// <summary>Reads framed ANIMDATA records while retaining the encoded channel data.</summary>
public sealed class AnimationDataReader : IAssetDecoder
{
    public const uint ResourceType = 0x7F1;
    public const int MaximumResourceBytes = 16 * 1024 * 1024;
    public string Family => "Animations";
    public string Version => "5";
    public bool Supports(AssetDescriptor descriptor) => descriptor.Family == Family;

    public DecodeResult<AnimationDataDocument> Decode(ReadOnlyMemory<byte> bytes,
        ResourceIdentity identity, SourceRevision revision, SourceExtent extent,
        AnimationReadProfile profile = AnimationReadProfile.FramedRecords, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(extent);
        if (identity.IndexContentId != revision.IndexContentId)
            throw new ArgumentException("Resource and revision belong to different indexes.", nameof(revision));
        if (extent.Offset < 0 || extent.Length != bytes.Length)
            throw new ArgumentException("Source extent must describe the complete supplied resource.", nameof(extent));
        _ = checked(extent.Offset + extent.Length);
        string? payloadSha256 = null;
        if (bytes.Length > MaximumResourceBytes)
            return Result(null, ["Animation resource exceeds the configured decoding limit."], new Dictionary<string, string>());

        // Own the encoded bytes so a caller's reusable read buffer cannot change the document.
        var encoded = bytes.ToArray();
        payloadSha256 = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        if (extent.PayloadSha256 is not null && !string.Equals(extent.PayloadSha256, payloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The supplied payload hash does not match the resource bytes.", nameof(extent));
        extent = extent with { PayloadSha256 = payloadSha256 };
        var sections = new List<AnimationRecordSection>();
        var unknown = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var expected in new[] { "COM_CONT", "LOADCONT", "ANIMDATA" })
        {
            if (encoded.Length - position < 16)
                return Result(null, ["Animation record header is truncated."], unknown);
            var header = encoded.AsSpan(position, 16);
            if (!header[..8].SequenceEqual(Encoding.ASCII.GetBytes(expected)))
                return Result(null, [$"Expected {expected} record at resource offset {position}."], unknown);
            var version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            var bodyOffset = position + 16;
            if (length > encoded.Length - bodyOffset)
                return Result(null, [$"{expected} body exceeds the resource extent."], unknown);
            sections.Add(new(expected, version, position, bodyOffset, length,
                encoded.AsMemory(bodyOffset, checked((int)length))));
            unknown[$"{expected}.version"] = version.ToString(CultureInfo.InvariantCulture);
            position = checked(bodyOffset + (int)length);
        }
        if (position != encoded.Length)
            return Result(null, ["Unexpected bytes follow the ANIMDATA record."], unknown);
        var data = sections[^1];
        var words = new List<AnimationHeaderWord>();
        if (data.Version == 14)
        {
            var prefixLength = (int)Math.Min(32, data.BodyLength);
            for (var offset = 0; offset + 4 <= prefixLength; offset += 4)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Body.Span[offset..]);
                words.Add(new(offset, bits));
                unknown[$"ANIMDATA.header.{offset}"] = $"0x{bits:X8}";
            }
        }
        var diagnostics = new List<string>();
        var layout = data.Version == 14 && profile is AnimationReadProfile.Version14Tables or AnimationReadProfile.Version14Blocks
            ? AnimationVersion14Reader.Read(data, diagnostics, cancellationToken) : null;
        if (layout is not null && profile == AnimationReadProfile.Version14Blocks)
            layout = AnimationVersion14Blocks.Read(data, layout, cancellationToken);
        if (data.Version != 14) diagnostics.Add($"ANIMDATA version {data.Version} has no decoded table layout.");
        var document = new AnimationDataDocument(sections.AsReadOnly(), words.AsReadOnly(), encoded)
        { Version14Layout = layout };
        return Result(document, diagnostics.AsReadOnly(), unknown);

        DecodeResult<AnimationDataDocument> Result(AnimationDataDocument? value, IReadOnlyList<string> diagnostics,
            IReadOnlyDictionary<string, string> unknownFields) => new(value, identity, revision, [extent],
                [new(nameof(AnimationDataReader), Version, $"Framed records; raw prefix words; profile={profile}; remaining encoded bytes retained",
                    payloadSha256)], diagnostics, [], unknownFields);
    }
}
