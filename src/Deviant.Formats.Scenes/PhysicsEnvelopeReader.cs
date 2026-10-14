using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads the PHYSYS v5 outer envelope without decoding its embedded objects.</summary>
public sealed class PhysicsEnvelopeReader
{
    public const string Version = "physics-envelope-v5-1";
    public const uint NativeType = 2201;
    public const int HeaderBytes = 20;
    public const long MaximumSupportedEmbeddedBytes = 256L * 1024 * 1024;
    public const int MaximumSupportedPhysicalExtents = 262_144;
    public const int MaximumSupportedPrecedingSteps = 4096;
    public const int MaximumSourceNameCharacters = 4096;

    /// <summary>Accepts a frame produced by NativeObjectDirectoryReader, which owns wrapper and directory framing.</summary>
    public DecodeResult<PhysicsEnvelopeDocument> Read(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        PhysicsEnvelopeLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested();
        limits ??= new();
        if (limits.MaximumEmbeddedBytes < 0 || limits.MaximumEmbeddedBytes > MaximumSupportedEmbeddedBytes ||
            limits.MaximumPhysicalExtents < 0 || limits.MaximumPhysicalExtents > MaximumSupportedPhysicalExtents ||
            limits.MaximumPrecedingSteps < 0 || limits.MaximumPrecedingSteps > MaximumSupportedPrecedingSteps)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var physicalCount = physicalExtents.Count;
        var precedingCount = precedingSteps?.Count ?? 0;
        DecodeResult<PhysicsEnvelopeDocument> FailMetadata(string code) => new(null, frame.Identity, revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents ||
            precedingCount < 0 || precedingCount > limits.MaximumPrecedingSteps)
            return FailMetadata("PhysicsEnvelopeMetadataLimit");
        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physical.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var extent = physicalExtents[index];
            if (extent is null) return FailMetadata("PhysicsEnvelopePhysicalExtentInvalid");
            physical[index] = extent;
        }
        var preceding = new DecodeStep[precedingCount];
        for (var index = 0; index < preceding.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var step = precedingSteps![index];
            if (step is null) return FailMetadata("PhysicsEnvelopeStepInvalid");
            preceding[index] = step;
        }
        token.ThrowIfCancellationRequested();
        DecodeResult<PhysicsEnvelopeDocument> Fail(string code) => new(null, frame.Identity, revision, physical,
            [.. preceding, Step()], [code], [], new Dictionary<string, string>());
        var expectedPath = "dc-object/" + frame.DirectoryIndex.ToString(CultureInfo.InvariantCulture) + "/" +
            frame.NativeType.ToString(CultureInfo.InvariantCulture) + "/" + frame.NativeId.ToString(CultureInfo.InvariantCulture);
        if (revision.IndexContentId != frame.Identity.IndexContentId || frame.Identity.Ordinal < 0 || frame.DirectoryIndex < 0 ||
            !StringComparer.Ordinal.Equals(frame.Identity.MemberPath, expectedPath) ||
            frame.PayloadExtent.Container.MemberPath is not null ||
            frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal ||
            frame.PayloadExtent.Offset < 0 || frame.PayloadExtent.Length != frame.RawPayload.Length ||
            frame.PayloadExtent.Offset > long.MaxValue - frame.PayloadExtent.Length)
            return Fail("PhysicsEnvelopeSourceMismatch");
        foreach (var extent in physical)
        {
            token.ThrowIfCancellationRequested();
            if (extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                extent.RelativeSource is null || extent.RelativeSource.Length > MaximumSourceNameCharacters ||
                string.IsNullOrWhiteSpace(extent.RelativeSource) ||
                extent.RelativeSource.Contains(':') ||
                extent.RelativeSource.StartsWith('/') || extent.RelativeSource.StartsWith('\\') ||
                extent.RelativeSource.Split('/', '\\').Any(part => part is "" or "." or ".."))
                return Fail("PhysicsEnvelopePhysicalExtentInvalid");
        }
        if (frame.NativeType != NativeType) return Fail("PhysicsEnvelopeTypeUnsupported");
        var bytes = frame.RawPayload.Span;
        if (bytes.Length < HeaderBytes || frame.ClassCount != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 || !bytes.Slice(4, 8).SequenceEqual("PHYSYS__"u8))
            return Fail("PhysicsEnvelopeClassUnsupported");
        var serializedVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (serializedVersion != 5) return Fail("PhysicsEnvelopeVersionUnsupported");
        var embeddedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (embeddedLength > limits.MaximumEmbeddedBytes) return Fail("PhysicsEnvelopeByteLimit");
        if (embeddedLength != bytes.Length - HeaderBytes) return Fail("PhysicsEnvelopeLengthMismatch");
        var embedded = frame.RawPayload[HeaderBytes..];
        var embeddedExtent = At(HeaderBytes, embedded.Length);
        PhysicsEmbeddedHeader? embeddedHeader = null;
        if (embedded.Length >= 8)
        {
            var rawWord = BinaryPrimitives.ReadUInt32BigEndian(embedded.Span);
            embeddedHeader = new(rawWord, rawWord & 0x3fffffff, (byte)(rawWord >> 30),
                embedded.Span.Slice(4, 4).ToArray(), At(HeaderBytes, 8));
        }
        var embeddedHash = Hash(embedded.Span, token);
        var payloadHash = Hash(bytes, token);
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["ClassCount"] = At(0, 4), ["ClassTag"] = At(4, 8),
            ["ClassVersion"] = At(12, 4), ["EmbeddedLength"] = At(16, 4), ["Embedded"] = embeddedExtent
        };
        DecodedExtent[] unknownRanges = embedded.IsEmpty ? [] : [embeddedExtent];
        var value = new PhysicsEnvelopeDocument(frame.Identity, revision, frame.NativeId, 1,
            serializedVersion, embeddedLength, frame.PayloadExtent, embeddedExtent, embedded, embeddedHash,
            embeddedHeader, fields, unknownRanges, frame.RawPayload);
        token.ThrowIfCancellationRequested();
        return new(value, frame.Identity, revision, physical, [.. preceding, Step(payloadHash)], [], [],
            new Dictionary<string, string>
            {
                ["EmbeddedObjects"] = "Opaque",
                ["EmbeddedLength"] = embeddedLength.ToString(CultureInfo.InvariantCulture)
            });

        DecodedExtent At(int offset, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + offset), length);
    }

    private static DecodeStep Step(string? hash = null) =>
        new(nameof(PhysicsEnvelopeReader), Version, "PHYSYS-v5; little-endian-length; opaque-embedded", hash);

    private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (!bytes.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(bytes.Length, 128 * 1024);
            hash.AppendData(bytes[..length]); bytes = bytes[length..];
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
