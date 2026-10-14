using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads the inherited NODE v5 prefix of a native NODEAR v4 frame.</summary>
public sealed class NativeNodeArrayReader
{
    public const string Version = "native-node-array-prefix-v1";
    public const uint NativeType = 2138;
    public const int ClassOffset = 196;
    public const int BodyOffset = 208;
    public const int MinimumPayloadBytes = 212;
    public const int MaximumSupportedPhysicalExtents = 262_144;
    public const int MaximumSupportedPrecedingSteps = 4096;
    public const int MaximumSourceNameCharacters = 4096;

    public DecodeResult<NativeNodeArrayDocument> Read(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        NativeNodeArrayLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents); token.ThrowIfCancellationRequested();
        limits ??= new(); limits.Validate();
        var physicalCount = physicalExtents.Count; var precedingCount = precedingSteps?.Count ?? 0;
        token.ThrowIfCancellationRequested();
        DecodeResult<NativeNodeArrayDocument> FailMetadata(string code) => new(null, frame.Identity, revision,
            [], [new(nameof(NativeNodeArrayReader), Version, "NODE-v5; NODEAR-v4; little-endian")],
            [code], [], new Dictionary<string, string>());
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents ||
            precedingCount < 0 || precedingCount > limits.MaximumPrecedingSteps)
            return FailMetadata("NodeArrayMetadataLimit");
        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physical.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = physicalExtents[index];
            token.ThrowIfCancellationRequested();
            if (extent is null) return FailMetadata("NodeArrayPhysicalExtentInvalid");
            physical[index] = extent;
        }
        var preceding = new DecodeStep[precedingCount];
        for (var index = 0; index < preceding.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var step = precedingSteps![index];
            token.ThrowIfCancellationRequested();
            if (step is null) return FailMetadata("NodeArrayStepInvalid");
            preceding[index] = step;
        }
        DecodeResult<NativeNodeArrayDocument> Fail(string code) => new(null, frame.Identity, revision, physical,
            [.. preceding, new(nameof(NativeNodeArrayReader), Version, "NODE-v5; NODEAR-v4; little-endian")],
            [code], [], new Dictionary<string, string>());
        var expectedPath = "dc-object/" + frame.DirectoryIndex.ToString(CultureInfo.InvariantCulture) + "/" +
            frame.NativeType.ToString(CultureInfo.InvariantCulture) + "/" + frame.NativeId.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(frame.Identity.IndexContentId) ||
            frame.Identity.IndexContentId.Length > MaximumSourceNameCharacters ||
            revision.IndexContentId != frame.Identity.IndexContentId || frame.Identity.Ordinal < 0 || frame.DirectoryIndex < 0 ||
            !StringComparer.Ordinal.Equals(frame.Identity.MemberPath, expectedPath) ||
            frame.PayloadExtent.Container.MemberPath is not null ||
            frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal ||
            frame.PayloadExtent.Offset < 0 || frame.PayloadExtent.Length != frame.RawPayload.Length ||
            frame.PayloadExtent.Offset > long.MaxValue - frame.PayloadExtent.Length)
            return Fail("NodeArraySourceIdentityMismatch");
        foreach (var extent in physical)
        {
            token.ThrowIfCancellationRequested();
            if (extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                extent.RelativeSource is null || extent.RelativeSource.Length > MaximumSourceNameCharacters ||
                string.IsNullOrWhiteSpace(extent.RelativeSource) || extent.RelativeSource.Contains(':') ||
                extent.RelativeSource.StartsWith('/') || extent.RelativeSource.StartsWith('\\') ||
                extent.RelativeSource.Split('/', '\\').Any(part => part is "" or "." or ".."))
                return Fail("NodeArrayPhysicalExtentInvalid");
        }
        if (frame.NativeType != NativeType) return Fail("NodeArrayTypeUnsupported");
        if (frame.RawPayload.Length > limits.MaximumPayloadBytes) return Fail("NodeArrayPayloadLimit");
        if (frame.RawPayload.Length < MinimumPayloadBytes) return Fail("NodeArrayFrameTruncated");

        // The document owns one complete byte copy; both body views and inherited fields use that copy.
        var owned = new byte[frame.RawPayload.Length];
        for (var at = 0; at < owned.Length; at += 64 * 1024)
        {
            token.ThrowIfCancellationRequested();
            frame.RawPayload.Slice(at, Math.Min(64 * 1024, owned.Length - at)).CopyTo(owned.AsMemory(at));
        }
        var payload = owned.AsSpan();
        if (Word(payload, 0) != 2 || frame.ClassCount != 2 || !payload.Slice(4, 8).SequenceEqual("NODE    "u8))
            return Fail("NodeArrayClassEnvelopeUnsupported");
        if (Word(payload, 12) != 5) return Fail("NodeArrayNodeVersionUnsupported");
        if (!payload.Slice(ClassOffset, 8).SequenceEqual("NODEAR  "u8)) return Fail("NodeArrayClassHeaderUnsupported");
        if (Word(payload, ClassOffset + 8) != 4) return Fail("NodeArrayVersionUnsupported");
        var stableFrame = frame with { RawPayload = owned };
        var inherited = NodeRecordReader.ReadBody(stableFrame, revision);
        var bodyVersion = Word(payload, BodyOffset);
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["ClassCount"] = At(0, 4), ["Version"] = At(ClassOffset + 8, 4),
            ["BodyVersion"] = At(BodyOffset, 4)
        };
        var value = new NativeNodeArrayDocument(frame.Identity, revision, 4, bodyVersion, inherited,
            At(0, ClassOffset), At(ClassOffset, 12), At(BodyOffset, payload.Length - BodyOffset), fields, owned);
        token.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        token.ThrowIfCancellationRequested();
        var references = new[] { inherited.Parent, inherited.FirstChild, inherited.NextSibling }
            .OfType<ResourceReference>().ToArray();
        return new(value, frame.Identity, revision, physical,
            [.. preceding, new(nameof(NativeNodeArrayReader), Version,
                "NODE-v5; NODEAR-v4; little-endian; inherited-node-prefix; owned-payload", hash)],
            [], references, new Dictionary<string, string>
            {
                ["Word172"] = inherited.Word172.ToString("X8", CultureInfo.InvariantCulture),
                ["Word176"] = inherited.Word176.ToString("X8", CultureInfo.InvariantCulture),
                ["NodeArrayBody"] = $"{value.BodyExtent.Offset}:{value.BodyExtent.Length}"
            });
        DecodedExtent At(int offset, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + offset), length);
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
