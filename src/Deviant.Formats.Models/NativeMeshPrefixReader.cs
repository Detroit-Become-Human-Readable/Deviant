using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Models;

public sealed class NativeMeshPrefixReader
{
    public const int MaximumPayloadBytes = 8 * 1024 * 1024;
    public const int MaximumContextEntries = 1024;
    public const int MaximumNameBytes = 4096;
    public const int MaximumRetainedNameBytes = 1024 * 1024;
    public const int MaximumExtraReferences = 4096;
    public const int MaximumSourceNameCharacters = 4096;

    public bool TryRead(NativeObjectFrame frame, SceneEntityRecord entity,
        out NativeMeshPrefixDocument? document, out string? diagnostic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(entity);
        document = null; diagnostic = null; token.ThrowIfCancellationRequested();
        if (frame.RawPayload.Length > MaximumPayloadBytes)
        { diagnostic = "NativeMeshPrefixPayloadLimit"; return false; }
        if (frame.NativeType == 2009 && frame.ClassCount == 4 || frame.NativeType == 2013 && frame.ClassCount == 5)
        {
            if (!ValidSource(frame, entity))
            { diagnostic = "NativeMeshPrefixSourceMismatch"; return false; }
        }
        else { diagnostic = "NativeMeshPrefixProfileUnsupported"; return false; }
        if (!new NativeCommonPrefixReader().TryRead(frame, out var common, out diagnostic, token)) return false;
        try
        {
            if (entity.ClassCount != common!.ClassCount || entity.NextClassOffset != common.NextClassOffset + 49 ||
                entity.EntityExtent.Offset != frame.PayloadExtent.Offset + common.NextClassOffset ||
                entity.EntityExtent.Length != 49 || !MatchesEntity(frame, entity, common.NextClassOffset, token))
                throw new InvalidDataException("NativeMeshPrefixSourceMismatch");
            var reader = new NativeMeshByteReader(frame.RawPayload, frame.PayloadExtent);
            reader.Skip(entity.NextClassOffset);
            var parsed = NativeMeshPrefixCore.Read(reader, frame.Identity, token);
            var packedAt = reader.Position; var packed = reader.Word();
            var mode = (byte)(packed >> 24); var count = (int)(packed & 0xffffff);
            if (mode > 3) throw new InvalidDataException("NativeMeshContextModeUnsupported");
            if (count > MaximumContextEntries) throw new InvalidDataException("NativeMeshContextCountLimit");

            // Validate the complete variable section before allocating retained names or reference arrays.
            var entriesAt = reader.Position;
            long nameBytes = 0, blobBytes = 0, secondaryBytes = 0;
            for (var entry = 0; entry < count; entry++)
            {
                token.ThrowIfCancellationRequested();
                var length = reader.Word();
                if (length > MaximumNameBytes) throw new InvalidDataException("NativeMeshContextNameLimit");
                nameBytes += length;
                if (nameBytes > MaximumRetainedNameBytes) throw new InvalidDataException("NativeMeshContextNameBudget");
                reader.Skip((int)length); reader.Skip(8);
                if (mode == 0) continue;
                blobBytes += reader.Word() & 0xffffff;
                if (mode == 2) secondaryBytes += reader.Word();
                if (blobBytes > MaximumPayloadBytes || secondaryBytes > MaximumPayloadBytes ||
                    blobBytes + secondaryBytes > MaximumPayloadBytes)
                    throw new InvalidDataException("NativeMeshContextBlobLimit");
            }
            var entriesEnd = reader.Position;
            var blob = reader.Skip(checked((int)blobBytes));
            var secondaryBlob = reader.Skip(checked((int)secondaryBytes));
            var refsCountAt = reader.Position; var referenceCount = reader.Word();
            if (referenceCount > MaximumExtraReferences) throw new InvalidDataException("NativeMeshExtraReferenceLimit");
            var refsExtent = reader.Skip(checked((int)referenceCount * 8));
            var finalExtent = reader.Skip(31);
            var end = reader.Position;

            var copy = new NativeMeshByteReader(frame.RawPayload, frame.PayloadExtent);
            copy.Skip(entriesAt);
            var entries = new NativeMeshContextEntry[count];
            long copiedNameBytes = 0, copiedBlobBytes = 0, copiedSecondaryBytes = 0;
            for (var entry = 0; entry < entries.Length; entry++)
            {
                token.ThrowIfCancellationRequested();
                var start = copy.Position; var lengthWord = copy.Word();
                if (lengthWord > MaximumNameBytes) throw new InvalidDataException("NativeMeshContextNameLimit");
                copiedNameBytes += lengthWord;
                if (copiedNameBytes > MaximumRetainedNameBytes) throw new InvalidDataException("NativeMeshContextNameBudget");
                var length = (int)lengthWord;
                var nameAt = copy.Position; var name = copy.Take(length).ToArray();
                var refAt = copy.Position; var reference = copy.Reference(frame.Identity);
                uint? control = null, secondary = null;
                DecodedExtent? controlExtent = null, secondaryExtent = null;
                if (mode != 0)
                {
                    var at = copy.Position; control = copy.Word(); controlExtent = copy.Extent(at, 4);
                    copiedBlobBytes += control.Value & 0xffffff;
                    if (mode == 2)
                    {
                        at = copy.Position; secondary = copy.Word(); secondaryExtent = copy.Extent(at, 4);
                        copiedSecondaryBytes += secondary.Value;
                    }
                }
                entries[entry] = new(entry, name, reference, control, secondary,
                    copy.Extent(start, copy.Position - start), copy.Extent(nameAt, length), copy.Extent(refAt, 8),
                    controlExtent, secondaryExtent);
            }
            if (copy.Position != entriesEnd || copiedNameBytes != nameBytes || copiedBlobBytes != blobBytes || copiedSecondaryBytes != secondaryBytes)
                throw new InvalidDataException("NativeMeshPrefixEndMismatch");
            copy.Skip(checked((int)(blobBytes + secondaryBytes)));
            if (copy.Word() != referenceCount) throw new InvalidDataException("NativeMeshPrefixEndMismatch");
            var references = new NativeMeshPrefixReference[referenceCount];
            for (var i = 0; i < references.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var at = copy.Position;
                references[i] = new(copy.Reference(frame.Identity), copy.Extent(at, 8));
            }
            var finalWord = copy.Word(); var finalFlag = copy.Byte();
            ulong rawSix = 0;
            for (var i = 0; i < 6; i++) rawSix |= (ulong)copy.Byte() << (8 * i);
            var finalWords = new uint[5];
            for (var i = 0; i < finalWords.Length; i++) finalWords[i] = copy.Word();
            if (copy.Position != end) throw new InvalidDataException("NativeMeshPrefixEndMismatch");
            token.ThrowIfCancellationRequested();
            var fields = new Dictionary<string, DecodedExtent>(parsed.FieldExtents)
            {
                ["PackedContextControl"] = reader.Extent(packedAt, 4),
                ["ContextEntries"] = reader.Extent(entriesAt, entriesEnd - entriesAt),
                ["ContextBlob"] = blob, ["SecondaryContextBlob"] = secondaryBlob,
                ["ExtraReferenceCount"] = reader.Extent(refsCountAt, 4), ["ExtraReferences"] = refsExtent,
                ["FinalFields"] = finalExtent
            };
            document = new(frame.Identity, entity, 29, parsed.Lods, parsed.NativeFlags, parsed.GroupIndex,
                parsed.FixedFields, packed, mode, entries, blob, secondaryBlob, references,
                new(finalWord, finalFlag, rawSix, finalWords, finalExtent), reader.Extent(parsed.Start, end - parsed.Start),
                end, reader.Extent(end, frame.RawPayload.Length - end), fields);
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshSizeOverflow"; return false; }
    }

    private static bool ValidSource(NativeObjectFrame frame, SceneEntityRecord entity)
    {
        if (frame.Identity is null || frame.PayloadExtent is null || frame.PayloadExtent.Container is null ||
            frame.HeaderExtent is null || frame.DirectoryEntryExtent is null || entity.Identity is null || entity.EntityExtent is null ||
            string.IsNullOrWhiteSpace(frame.Identity.IndexContentId) || frame.Identity.IndexContentId.Length > MaximumSourceNameCharacters ||
            frame.Identity.Ordinal < 0 || frame.DirectoryIndex < 0 || frame.Identity.MemberPath is null ||
            frame.Identity.MemberPath.Length > MaximumSourceNameCharacters || frame.PayloadExtent.Container.MemberPath is not null ||
            frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal ||
            !ValidExtent(frame.PayloadExtent) || frame.PayloadExtent.Length != frame.RawPayload.Length ||
            !ValidExtent(frame.HeaderExtent) || !ValidExtent(frame.DirectoryEntryExtent) ||
            frame.HeaderExtent.Container != frame.PayloadExtent.Container || frame.DirectoryEntryExtent.Container != frame.PayloadExtent.Container ||
            entity.Identity != frame.Identity || entity.NativeType != frame.NativeType || entity.NativeId != frame.NativeId ||
            entity.EntityExtent.Container != frame.PayloadExtent.Container || !ValidExtent(entity.EntityExtent) ||
            entity.OtherReferences is null || entity.OtherReferences.Count > 3 || entity.FieldExtents is null || entity.FieldExtents.Count != 6)
            return false;
        var expected = string.Create(CultureInfo.InvariantCulture,
            $"dc-object/{frame.DirectoryIndex}/{frame.NativeType}/{frame.NativeId}");
        return StringComparer.Ordinal.Equals(expected, frame.Identity.MemberPath);
    }

    private static bool ValidExtent(DecodedExtent extent) => extent.Offset >= 0 && extent.Length >= 0 &&
        extent.Offset <= long.MaxValue - extent.Length;

    private static bool MatchesEntity(NativeObjectFrame frame, SceneEntityRecord entity, int start, CancellationToken token)
    {
        var bytes = frame.RawPayload.Span;
        if (start < 0 || start > bytes.Length - 49 || !bytes.Slice(start, 8).SequenceEqual("ENTITY  "u8) ||
            Word(bytes, start + 8) != 7) return false;
        var body = start + 12;
        var expectedFields = new (string Key, int Offset, int Length)[]
        { ("Node", 0, 8), ("Reference1", 8, 8), ("Reference2", 16, 8), ("Reference3", 24, 8), ("Word32", 32, 4), ("Flag36", 36, 1) };
        foreach (var field in expectedFields)
        {
            token.ThrowIfCancellationRequested();
            if (!entity.FieldExtents.TryGetValue(field.Key, out var extent) || extent is null ||
                extent.Container != frame.PayloadExtent.Container || extent.Offset != frame.PayloadExtent.Offset + body + field.Offset ||
                extent.Length != field.Length) return false;
        }
        if (!Matches(entity.Node, Word(bytes, body), Word(bytes, body + 4), frame.Identity)) return false;
        var referenceIndex = 0;
        for (var i = 0; i < 3; i++)
        {
            token.ThrowIfCancellationRequested();
            var at = body + 8 + 8 * i; var type = Word(bytes, at); var id = Word(bytes, at + 4);
            if (type == 0 && id == 0) continue;
            if (referenceIndex >= entity.OtherReferences.Count || !Matches(entity.OtherReferences[referenceIndex++], type, id, frame.Identity)) return false;
        }
        return referenceIndex == entity.OtherReferences.Count && entity.StoredWord == Word(bytes, body + 32) &&
            entity.StoredFlag == bytes[body + 36];
    }

    private static bool Matches(ResourceReference? reference, uint type, uint id, ResourceIdentity owner) =>
        type == 0 && id == 0 ? reference is null : reference is not null && reference.Namespace == IdentifierNamespace.Native &&
        reference.Owner == owner && reference.ResourceType == type &&
        StringComparer.Ordinal.Equals(reference.Id, id.ToString(CultureInfo.InvariantCulture));

    private static uint Word(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
}
