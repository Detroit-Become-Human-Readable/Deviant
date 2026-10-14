using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads a complete, single-class NODE v5 native object frame.</summary>
public sealed class NodeRecordReader
{
    public const string Version = "node-v5-1";
    public const uint NativeType = 2002;
    public const int PayloadBytes = 196;
    private const int BodyOffset = 16;

    public DecodeResult<SceneNodeRecord> Read(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested();
        var physical = physicalExtents.ToArray();
        var preceding = precedingSteps?.ToArray() ?? [];
        DecodeResult<SceneNodeRecord> Fail(string code) => new(null, frame.Identity, revision, physical,
            [.. preceding, new(nameof(NodeRecordReader), Version, "NODE-v5; little-endian")], [code], [],
            new Dictionary<string, string>());
        if (revision.IndexContentId != frame.Identity.IndexContentId || frame.Identity.MemberPath is null ||
            frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal ||
            frame.PayloadExtent.Offset < 0 || frame.PayloadExtent.Length != frame.RawPayload.Length ||
            frame.PayloadExtent.Offset > long.MaxValue - frame.PayloadExtent.Length)
            return Fail("NodeSourceIdentityMismatch");
        if (physical.Any(extent => extent.Offset < 0 || extent.Length < 0 ||
            extent.Offset > long.MaxValue - extent.Length || string.IsNullOrWhiteSpace(extent.RelativeSource) ||
            extent.RelativeSource.Contains(':') || extent.RelativeSource.StartsWith('/') ||
            extent.RelativeSource.StartsWith('\\') || extent.RelativeSource.Split('/', '\\').Any(part => part is "" or "." or "..")))
            return Fail("NodePhysicalExtentInvalid");
        if (frame.NativeType != NativeType) return Fail("NodeTypeUnsupported");
        var payload = frame.RawPayload.Span;
        if (payload.Length != PayloadBytes || Word(payload, 0) != 1 || frame.ClassCount != 1 ||
            !payload.Slice(4, 8).SequenceEqual("NODE    "u8))
            return Fail("NodeClassEnvelopeUnsupported");
        if (Word(payload, 12) != 5) return Fail("NodeVersionUnsupported");
        var value = ReadBody(frame, revision);
        token.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var references = new[] { value.Parent, value.FirstChild, value.NextSibling }.OfType<ResourceReference>().ToArray();
        return new(value, frame.Identity, revision, physical,
            [.. preceding, new(nameof(NodeRecordReader), Version, "NODE-v5; little-endian; reversed-matrix-words", hash)],
            [], references, new Dictionary<string, string>
            {
                ["Word172"] = value.Word172.ToString("X8", CultureInfo.InvariantCulture),
                ["Word176"] = value.Word176.ToString("X8", CultureInfo.InvariantCulture)
            });
    }

    // Each public profile validates its own envelope before sharing the established NODE v5 body.
    internal static SceneNodeRecord ReadBody(NativeObjectFrame frame, SourceRevision revision)
    {
        var body = frame.RawPayload.Span.Slice(BodyOffset, 180);
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["Flags"] = At(0, 4), ["Pivot"] = At(4, 12), ["StoredLocalTransform"] = At(16, 64),
            ["StoredWorldTransform"] = At(80, 64), ["Parent"] = At(144, 8),
            ["ParentAttachmentIndex"] = At(152, 4), ["FirstChild"] = At(156, 8),
            ["NextSibling"] = At(164, 8), ["Word172"] = At(172, 4), ["Word176"] = At(176, 4)
        };
        var parent = Reference(body, 144, frame.Identity);
        var firstChild = Reference(body, 156, frame.Identity);
        var nextSibling = Reference(body, 164, frame.Identity);
        return new SceneNodeRecord(frame.Identity, revision, frame.NativeId, Word(body, 0),
            new(Float(body, 4), Float(body, 8), Float(body, 12)), Matrix(body[16..80]), Matrix(body[80..144]),
            parent, firstChild, nextSibling, BinaryPrimitives.ReadInt32LittleEndian(body[152..]),
            Word(body, 172), Word(body, 176), frame.PayloadExtent, fields, frame.RawPayload);
        DecodedExtent At(int offset, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + BodyOffset + offset), length);
    }

    private static SourceTransform Matrix(ReadOnlySpan<byte> bytes)
    {
        var elements = new float[16];
        for (var index = 0; index < elements.Length; index++) elements[15 - index] = Float(bytes, index * 4);
        return new(elements, MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
    }

    private static ResourceReference? Reference(ReadOnlySpan<byte> bytes, int offset, ResourceIdentity owner)
    {
        var type = Word(bytes, offset); var id = Word(bytes, offset + 4);
        return type == 0 && id == 0 ? null : new(IdentifierNamespace.Native,
            id.ToString(CultureInfo.InvariantCulture), owner, type);
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static float Float(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
}
