using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Formats.Scenes;

namespace Deviant.Formats.Models;

public sealed record NativeSkeletonNodeArrayBindingLimits(int MaximumMatrices = 8192,
    int MaximumPrimaryNameEntries = 65_536, int MaximumNameCandidates = 262_144,
    long MaximumMetadataBytes = 64L * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumMatrices is < 0 or > 8192 || MaximumPrimaryNameEntries is < 0 or > 65_536 ||
            MaximumNameCandidates is < 0 or > 1_048_576 || MaximumMetadataBytes is < 4096 or > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(NativeSkeletonNodeArrayBindingLimits));
    }
}

public enum NativeSkeletonNodeNameMatchStatus { Missing, Unique, Ambiguous }

/// <summary>A primary-name-table key join. Missing and duplicate keys remain explicit.</summary>
public sealed record NativeSkeletonNodeNameMatch(uint DescriptorKey,
    NativeSkeletonNodeNameMatchStatus Status, IReadOnlyList<SkeletonNameEntry> Entries);

/// <summary>Ordinal-correlated stored matrix, node record and descriptor with a key-only name result.</summary>
public sealed record NativeSkeletonNodeArrayBindingEntry(int Ordinal,
    SkeletonStoredMatrix StoredMatrix, NodeArrayBodyNode Node, NodeArrayBodyDescriptor Descriptor,
    NativeSkeletonNodeNameMatch NameMatch);

/// <summary>
/// Shared source join for SKELETON v15 and NODEAR v14. It preserves serialized values and extents;
/// it does not assign pose-space or skinning semantics.
/// </summary>
/// <param name="AccountedMetadataBytes">Conservative additional allocation estimate for the binding projection and scratch.</param>
public sealed record NativeSkeletonNodeArrayBindingDocument(ResourceIdentity SkeletonIdentity,
    ResourceIdentity NodeArrayIdentity, SourceRevision Revision, ResourceReference EntityNode,
    DecodedExtent SkeletonExtent, DecodedExtent NodeArrayExtent, DecodedExtent EntityNodeExtent,
    DecodedExtent PrimaryNameTableExtent, IReadOnlyList<NativeSkeletonNodeArrayBindingEntry> Entries,
    long AccountedMetadataBytes)
{
    [JsonIgnore]
    public int MatrixCount => Entries.Count;
}
