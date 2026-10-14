using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Textures;

/// <summary>Projects fields already retained by <see cref="TextureContextReader"/>.</summary>
public static class IblLightStoredFieldsProjection
{
    public const string Version = "ibl-light-stored-fields-v1";
    public const int MaximumPhysicalExtents = 8192;

    public static DecodeResult<IblLightStoredFields> Project(TextureIblLightV13Context context,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Identity);
        ArgumentNullException.ThrowIfNull(context.Revision);
        ArgumentNullException.ThrowIfNull(context.Extent);
        token.ThrowIfCancellationRequested();
        var extent = context.Extent;
        IReadOnlyList<SourceExtent> physical = Array.Empty<SourceExtent>();
        if (extent.Container is null || context.Identity.IndexContentId != context.Revision.IndexContentId ||
            context.Identity.IndexContentId != extent.Container.IndexContentId ||
            context.Identity.Ordinal != extent.Container.Ordinal || extent.Offset < 0 ||
            extent.Length < 115 || extent.Length > TextureContextReader.MaximumRecordBytes ||
            extent.Offset > long.MaxValue - extent.Length)
            return Fail("SourceMismatch");
        if (context.RawFixedFields83 is not { Length: 83 } || context.RawTrailingFields8 is not { Length: 8 })
            return Fail("InvalidFieldStorage");
        if (context.TextureReferences is null || context.OriginalPhysicalExtents is null)
            return Fail("MissingCollections");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(context.RawFixedFields83.AsSpan(75, 4));
        if (count > TextureContextReader.MaximumReferences || count != context.TextureReferences.Count)
            return Fail("ReferenceCountMismatch");
        var firstIndexOffset = checked(95 + 8 * (int)count);
        if (firstIndexOffset > extent.Length - 20) return Fail("TruncatedFields");
        for (var index = 0; index < (int)count; index++)
        {
            token.ThrowIfCancellationRequested();
            var reference = context.TextureReferences[index];
            if (reference is null || !Matches(reference.Extent, 95 + index * 8, 8))
                return Fail("ReferenceExtentMismatch");
        }
        var cursor = firstIndexOffset + 4;
        if (!ValidateString(context.OwnerName, ref cursor)) return Fail("InvalidOwnerName");
        if (!ValidateString(context.SourcePath, ref cursor)) return Fail("InvalidSourcePath");
        if (cursor != extent.Length - 8) return Fail("RecordBoundaryMismatch");

        var physicalCount = context.OriginalPhysicalExtents.Count;
        if (physicalCount < 0 || physicalCount > MaximumPhysicalExtents) return Fail("PhysicalExtentLimit");
        for (var index = 0; index < physicalCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var source = context.OriginalPhysicalExtents[index];
            if (source is null || source.Offset < 0 || source.Length < 0 ||
                source.Offset > long.MaxValue - source.Length)
                return Fail("PhysicalExtentMismatch");
        }

        // Take ownership of the mutable containers before exposing the projection.
        var fixedFields = context.RawFixedFields83.ToArray();
        var trailingFields = context.RawTrailingFields8.ToArray();
        var referenceArray = new TextureContextReference[(int)count];
        for (var index = 0; index < referenceArray.Length; index++) referenceArray[index] = context.TextureReferences[index];
        var physicalArray = new SourceExtent[physicalCount];
        for (var index = 0; index < physicalArray.Length; index++) physicalArray[index] = context.OriginalPhysicalExtents[index];
        var references = Array.AsReadOnly(referenceArray);
        physical = Array.AsReadOnly(physicalArray);
        var value = new IblLightStoredFields(context.Identity, context.Revision, extent,
            Byte(12), Bounds(13), Bounds(37),
            new TextureContextReference(Word(61), Word(65), At(61, 8)),
            Float(69), Float(73), Float(77), Byte(81), Byte(82), Float(83),
            new(count, At(87, 4)), new(Word(91), At(91, 4)), references,
            new(context.FollowingUInt32, At(firstIndexOffset, 4)), context.OwnerName, context.SourcePath,
            new(BinaryPrimitives.ReadUInt32LittleEndian(trailingFields.AsSpan(0, 4)), At((int)extent.Length - 8, 4)),
            new(BinaryPrimitives.ReadUInt32LittleEndian(trailingFields.AsSpan(4, 4)), At((int)extent.Length - 4, 4)),
            physical, fixedFields, trailingFields);
        var unresolved = new List<ResourceReference>(references.Count + 1);
        if (value.GetStoredResourceReference() is { } storedReference) unresolved.Add(storedReference);
        for (var index = 0; index < references.Count; index++)
            if (value.GetTextureResourceReference(index) is { } reference) unresolved.Add(reference);
        token.ThrowIfCancellationRequested();
        return new(value, context.Identity, context.Revision, physical, Steps(), Array.Empty<string>(),
            unresolved.AsReadOnly(), EmptyFields());

        DecodedExtent At(int offset, int length) => new(extent.Container, checked(extent.Offset + offset), length);
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(fixedFields.AsSpan(offset - 12, 4));
        IblFloat32 Float(int offset) => new(Word(offset), At(offset, 4));
        IblByte Byte(int offset) => new(fixedFields[offset - 12], At(offset, 1));
        IblStoredBounds Bounds(int offset) => new(Float(offset), Float(offset + 4), Float(offset + 8),
            Float(offset + 12), Float(offset + 16), Float(offset + 20));
        bool Matches(DecodedExtent? field, int offset, int length) => field is not null &&
            field.Container == extent.Container && offset >= 0 && length >= 0 && offset <= extent.Length - length &&
            field.Offset == extent.Offset + offset && field.Length == length;
        bool ValidateString(TextureContextString? field, ref int offset)
        {
            if (field is null || field.Text is null || field.Encoding != "ASCII" ||
                field.Text.Length > TextureContextReader.MaximumStringBytes ||
                !Matches(field.LengthExtent, offset, 4) || !Matches(field.ValueExtent, offset + 4, field.Text.Length))
                return false;
            foreach (var character in field.Text) if (character < 32 || character > 126) return false;
            if (field.ValueSha256 is not { Length: 64 } ||
                !field.ValueSha256.Equals(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(field.Text))),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            offset = checked(offset + 4 + field.Text.Length);
            return true;
        }
        DecodeResult<IblLightStoredFields> Fail(string code) => new(null, context.Identity, context.Revision,
            physical, Steps(), Array.AsReadOnly(new[] { code }), Array.Empty<ResourceReference>(),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["code"] = code }));
    }

    private static IReadOnlyList<DecodeStep> Steps() => Array.AsReadOnly(new[]
    {
        new DecodeStep(nameof(IblLightStoredFieldsProjection), Version, "IBLLIGHT-v13; retained-context")
    });

    private static IReadOnlyDictionary<string, string> EmptyFields() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}
