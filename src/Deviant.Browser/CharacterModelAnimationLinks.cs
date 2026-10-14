using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Models;

namespace Deviant.Browser;

public sealed record CharacterModelDirectReference(ResourceIdentity SourceIdentity, string SourceField,
    ResourceReference Reference, DecodedExtent ReferenceExtent);

public sealed record CharacterModelAnimationLink(ResourceIdentity SourceIdentity, ResourceIdentity AnimationIdentity,
    string SourceField, ResourceReference Reference, DecodedExtent ReferenceExtent,
    DecodedExtent AnimationClassExtent, uint AnimationVersion);

/// <summary>Resolves stored model references to validated animation classes in the same native directory.</summary>
public static class CharacterModelAnimationLinks
{
    public const string Profile = "model-direct-animation-links-v1-animdata14";
    public const int MaximumDirectReferences = 8192;
    public const int MaximumTargetPayloadBytes = 16 * 1024 * 1024;
    public const uint SupportedAnimationVersion = 14;
    private const int MinimumAnimationPrefixBytes = 28;

    public static IReadOnlyList<CharacterModelDirectReference> ReadReferences(NativeObjectFrame owner,
        SceneEntityRecord entity, NativeMeshPrefixDocument? mesh = null, DecodedExtent? legacyFixedFields = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Identity != owner.Identity || mesh is not null && mesh.Identity != owner.Identity)
            throw new ArgumentException("The reference projection belongs to another model member.");
        var result = new List<CharacterModelDirectReference>();
        foreach (var field in new[] { "Node", "Reference1", "Reference2", "Reference3" })
            if (entity.FieldExtents.TryGetValue(field, out var extent)) Add("ENTITY." + field, extent);
        if (mesh is not null)
        {
            for (var index = 0; index < mesh.Lods.Count; index++)
            {
                var extent = mesh.Lods[index].Extent;
                Add($"MESH.Lod{index}", extent with { Length = 8 });
            }
            Add("MESH.FixedReference0", mesh.FieldExtents["FixedReference0"]);
            Add("MESH.FixedReference1", mesh.FieldExtents["FixedReference1"]);
            foreach (var entry in mesh.ContextEntries) Add($"MESH.Context[{entry.Ordinal}]", entry.ReferenceExtent);
            for (var index = 0; index < mesh.ExtraReferences.Count; index++)
                Add($"MESH.ExtraReference[{index}]", mesh.ExtraReferences[index].Extent);
        }
        else if (legacyFixedFields is { } fixedFields)
        {
            Add("MESH.FixedReference0", fixedFields with { Offset = checked(fixedFields.Offset + 8), Length = 8 });
            Add("MESH.FixedReference1", fixedFields with { Offset = checked(fixedFields.Offset + 16), Length = 8 });
        }
        return result.AsReadOnly();

        void Add(string field, DecodedExtent extent)
        {
            token.ThrowIfCancellationRequested();
            if (!TryReadPair(owner, extent, out var type, out var id) || type == 0 && id == 0) return;
            if (result.Count >= MaximumDirectReferences)
                throw new InvalidDataException("The model reference projection exceeds its configured limit.");
            result.Add(new(owner.Identity, field, new(IdentifierNamespace.Native,
                id.ToString(CultureInfo.InvariantCulture), owner.Identity, type), extent));
        }
    }

    public static IReadOnlyList<CharacterModelAnimationLink> Resolve(NativeObjectDirectoryDocument directory,
        ResourceIdentity modelIdentity, IReadOnlyList<CharacterModelDirectReference> references,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(modelIdentity);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > MaximumDirectReferences)
            throw new InvalidDataException("The model reference projection exceeds its configured limit.");
        token.ThrowIfCancellationRequested();
        var resolver = new NativeReferenceResolver(directory);
        var owners = directory.Objects.Where(frame => frame.Identity == modelIdentity && frame.NativeType is 2009 or 2013)
            .Take(2).ToArray();
        if (owners.Length != 1) return [];
        var owner = owners[0];
        var frames = directory.Objects.ToDictionary(frame => frame.Identity);
        var validated = new Dictionary<ResourceIdentity, DecodedExtent?>();
        var output = new List<CharacterModelAnimationLink>();
        foreach (var stored in references)
        {
            token.ThrowIfCancellationRequested();
            if (stored.SourceIdentity != modelIdentity || stored.Reference.Owner != modelIdentity ||
                stored.Reference.Namespace != IdentifierNamespace.Native ||
                !TryReadPair(owner, stored.ReferenceExtent, out var type, out var id) ||
                stored.Reference.ResourceType != type || stored.Reference.Id != id.ToString(CultureInfo.InvariantCulture)) continue;
            var resolution = resolver.Resolve(stored.Reference);
            if (resolution.Status != ReferenceStatus.Unique || resolution.Candidates.Count != 1) continue;
            var identity = resolution.Candidates[0];
            if (!validated.TryGetValue(identity, out var classExtent))
            {
                classExtent = frames.TryGetValue(identity, out var frame) &&
                    TryValidateAnimationClass(directory.Identity, frame, token, out var extent) ? extent : null;
                validated.Add(identity, classExtent);
            }
            if (classExtent is null) continue;
            output.Add(new(modelIdentity, identity, stored.SourceField, stored.Reference,
                stored.ReferenceExtent, classExtent, SupportedAnimationVersion));
        }
        return output.Distinct().ToArray();
    }

    public static long EstimateRetention(CharacterModelBindingResult binding) => checked(
        binding.DirectReferences.Sum(reference => 512L + reference.SourceField.Length * 2L +
            reference.SourceIdentity.Key.Length * 2L + reference.Reference.Id.Length * 2L) +
        binding.AnimationLinks.Sum(link => 768L + link.SourceField.Length * 2L +
            link.SourceIdentity.Key.Length * 2L + link.AnimationIdentity.Key.Length * 2L + link.Reference.Id.Length * 2L));

    private static bool TryReadPair(NativeObjectFrame owner, DecodedExtent extent, out uint type, out uint id)
    {
        type = 0; id = 0;
        if (extent.Container != owner.PayloadExtent.Container || extent.Length != 8 ||
            extent.Offset < owner.PayloadExtent.Offset || owner.PayloadExtent.Length != owner.RawPayload.Length) return false;
        var relative = extent.Offset - owner.PayloadExtent.Offset;
        if (relative > owner.RawPayload.Length - 8L) return false;
        var bytes = owner.RawPayload.Span.Slice((int)relative, 8);
        type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        return true;
    }

    private static bool TryValidateAnimationClass(ResourceIdentity container, NativeObjectFrame frame,
        CancellationToken token, out DecodedExtent? extent)
    {
        extent = null;
        if (frame.Identity.IndexContentId != container.IndexContentId || frame.Identity.Ordinal != container.Ordinal ||
            frame.PayloadExtent.Container != container || frame.RawPayload.Length > MaximumTargetPayloadBytes ||
            frame.Identity.MemberPath != string.Create(CultureInfo.InvariantCulture,
                $"dc-object/{frame.DirectoryIndex}/{frame.NativeType}/{frame.NativeId}") ||
            frame.ClassCount != 3 || !new NativeCommonPrefixReader().TryRead(frame, out var prefix, out _, token) ||
            prefix is null || prefix.ClassCount != 3) return false;
        var offset = prefix.NextClassOffset;
        if (offset > frame.RawPayload.Length - 12 - MinimumAnimationPrefixBytes) return false;
        var bytes = frame.RawPayload.Span[offset..];
        if (!bytes[..8].SequenceEqual("ANIMDATA"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != SupportedAnimationVersion) return false;
        extent = new(container, checked(frame.PayloadExtent.Offset + offset), 12);
        return true;
    }
}
