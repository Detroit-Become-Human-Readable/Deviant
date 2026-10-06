using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Textures;

public sealed record TextureContextString(string Text, string Encoding, DecodedExtent LengthExtent,
    DecodedExtent ValueExtent, string ValueSha256);

public sealed record TextureContextReference(uint Type, uint Id, DecodedExtent Extent)
{
    public bool IsBound => Type != 0 && Type != uint.MaxValue && Id != 0 && Id != uint.MaxValue;
}

public sealed record TextureIblLightV13Context(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent Extent, IReadOnlyList<TextureContextReference> TextureReferences, uint FollowingUInt32,
    TextureContextString OwnerName, TextureContextString SourcePath, byte[] RawFixedFields83,
    byte[] RawTrailingFields8, IReadOnlyList<SourceExtent> OriginalPhysicalExtents)
{
    public TextureContextReference? TextureReference => TextureReferences.FirstOrDefault();
}

/// <summary>Reads the stored context fields of an exactly bounded IBLLIGHT v13 class record.</summary>
public sealed class TextureContextReader
{
    public const string Version = "texture-context-ibl13-v1";
    public const int MaximumStringBytes = 4096;
    public const int MaximumRecordBytes = 16 * 1024;
    public const int MaximumReferences = 256;

    public DecodeResult<TextureIblLightV13Context> ParseIblLightV13(ReadOnlySpan<byte> body,
        int tagOffset, int recordEndOffset, ResourceIdentity identity, SourceRevision revision,
        DecodedExtent bodyExtent, IReadOnlyList<SourceExtent> physicalExtents,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(bodyExtent);
        ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested();
        var extents = physicalExtents.ToArray();
        if (identity.IndexContentId != revision.IndexContentId ||
            identity.Ordinal != bodyExtent.Container.Ordinal ||
            identity.IndexContentId != bodyExtent.Container.IndexContentId ||
            bodyExtent.Offset < 0 || bodyExtent.Length != body.Length ||
            bodyExtent.Offset > long.MaxValue - bodyExtent.Length ||
            extents.Any(item => item.Offset < 0 || item.Length < 0 || item.Offset > long.MaxValue - item.Length))
            return Fail("SourceMismatch");
        if (tagOffset < 0 || recordEndOffset < tagOffset || recordEndOffset > body.Length ||
            recordEndOffset - tagOffset > MaximumRecordBytes)
            return Fail("InvalidBounds");
        var record = body.Slice(tagOffset, recordEndOffset - tagOffset);
        if (record.Length < 12 || !record[..8].SequenceEqual("IBLLIGHT"u8)) return Fail("InvalidTag");
        if (Word(record, 8) != 13) return Fail("UnsupportedVersion");
        if (record.Length < 115) return Fail("TruncatedFields");
        var referenceCount = Word(record, 87);
        if (referenceCount > MaximumReferences) return Fail("ReferenceCountLimit");
        var cursor = checked(95 + (int)referenceCount * 8 + 4);
        var followingOffset = cursor - 4;
        if (cursor > record.Length) return Fail("TruncatedReferences");
        if (!TryReadString(record, ref cursor, out var owner)) return Fail("InvalidOwnerName");
        if (!TryReadString(record, ref cursor, out var path)) return Fail("InvalidSourcePath");
        if (record.Length - cursor != 8) return Fail("RecordBoundaryMismatch");
        token.ThrowIfCancellationRequested();
        var references = new TextureContextReference[referenceCount];
        for (var index = 0; index < references.Length; index++)
        {
            var offset = 95 + index * 8;
            references[index] = new TextureContextReference(Word(record, offset), Word(record, offset + 4), At(offset, 8));
        }
        var document = new TextureIblLightV13Context(identity, revision, At(0, record.Length), references,
            Word(record, followingOffset), StringField(owner!), StringField(path!), record.Slice(12, 83).ToArray(),
            record.Slice(cursor, 8).ToArray(), extents);
        return new(document, identity, revision, extents,
            [new(nameof(TextureContextReader), Version, "IBLLIGHT-v13; bounded-byte-strings", Hash(record))],
            [], [], new Dictionary<string, string> { ["referenceStatus"] = references.Any(item => item.IsBound) ? "StoredPair" : "Unbound" });

        DecodedExtent At(int offset, int length) => new(bodyExtent.Container,
            checked(bodyExtent.Offset + tagOffset + offset), length);
        TextureContextString StringField(StringBytes value) => new(value.Text, "ASCII",
            At(value.LengthOffset, 4), At(value.ValueOffset, value.Length), value.Sha256);
        DecodeResult<TextureIblLightV13Context> Fail(string code) => new(null, identity, revision, extents,
            [new(nameof(TextureContextReader), Version, "IBLLIGHT-v13; bounded-byte-strings")],
            [code], [], new Dictionary<string, string> { ["code"] = code });
    }

    private sealed record StringBytes(string Text, int LengthOffset, int ValueOffset, int Length, string Sha256);

    private static bool TryReadString(ReadOnlySpan<byte> record, ref int cursor, out StringBytes? value)
    {
        value = null;
        if (cursor > record.Length - 4) return false;
        var lengthOffset = cursor;
        var count = Word(record, cursor);
        cursor += 4;
        if (count > MaximumStringBytes || count > record.Length - cursor) return false;
        var length = checked((int)count);
        var bytes = record.Slice(cursor, length);
        foreach (var item in bytes) if (item < 32 || item > 126) return false;
        value = new(Encoding.ASCII.GetString(bytes), lengthOffset, cursor, length, Hash(bytes));
        cursor += length;
        return true;
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
