using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Scenes;

namespace Deviant.Formats.Models;

/// <summary>Joins retained SKELETON v15 and NODEAR v14 documents through the typed EntityNode reference.</summary>
public sealed class NativeSkeletonNodeArrayBindingReader
{
    private const int MaximumNameEntries = 65_536;
    private const int MaximumMatrices = 8192;
    private const int MaximumChildLinks = 262_144;
    private const long MaximumMetadataBytes = 64L * 1024 * 1024;
    private const long FixedMetadataBytes = 4096;

    /// <summary>
    /// Binds only a typed EntityNode reference that agrees with both parsed documents. The caller supplies
    /// the revision leased for the skeleton frame; the skeleton DTO itself does not retain that revision.
    /// Before this call, reserve the explicit limits' bounded <see cref="NativeSkeletonNodeArrayBindingLimits.MaximumMetadataBytes"/>
    /// from the caller's shared budget. On success, reduce that reservation to
    /// <see cref="NativeSkeletonNodeArrayBindingDocument.AccountedMetadataBytes"/>. Raw payload views are borrowed;
    /// retain the skeleton and node-array source leases and documents for the full binding lifetime.
    /// </summary>
    public bool TryRead(NativeSkeletonDocument skeleton, SourceRevision skeletonRevision,
        NativeNodeArrayBodyDocument nodeArray, out NativeSkeletonNodeArrayBindingDocument? binding,
        out string? diagnostic, CancellationToken token = default) =>
        TryRead(skeleton, skeletonRevision, nodeArray, new NativeSkeletonNodeArrayBindingLimits(),
            out binding, out diagnostic, token);

    public bool TryRead(NativeSkeletonDocument skeleton, SourceRevision skeletonRevision,
        NativeNodeArrayBodyDocument nodeArray, NativeSkeletonNodeArrayBindingLimits limits,
        out NativeSkeletonNodeArrayBindingDocument? binding, out string? diagnostic,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(skeletonRevision);
        ArgumentNullException.ThrowIfNull(nodeArray);
        ArgumentNullException.ThrowIfNull(limits);
        token.ThrowIfCancellationRequested();
        limits.Validate();
        binding = null; diagnostic = null;

        if (!TryValidateIdentityJoin(skeleton, skeletonRevision, nodeArray, out var rootContainer,
                out var entityNodeExtent, out var joinFailure))
            return Failure(out binding, out diagnostic, joinFailure);
        if (skeleton.Version != 15 || skeleton.MeshPrefix.Version != 29 ||
            nodeArray.BodyVersion != 14 || nodeArray.Owner.Version != 4 || nodeArray.Owner.BodyVersion != 14)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingProfileUnsupported");
        if (!ValidExtent(skeleton.Extent, rootContainer) || !ValidExtent(nodeArray.Extent, rootContainer) ||
            nodeArray.Extent != nodeArray.Owner.BodyExtent ||
            !ValidExtent(nodeArray.Owner.InheritedNode.PayloadExtent, rootContainer))
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingExtentInvalid");

        var matrixList = skeleton.Matrices;
        var nodeList = nodeArray.Nodes;
        var descriptorList = nodeArray.Descriptors;
        var tables = skeleton.NameTables;
        if (matrixList is null || nodeList is null || descriptorList is null || tables is null || tables.Count != 2)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingMetadataInvalid");
        token.ThrowIfCancellationRequested();
        var matrixCount = matrixList.Count;
        var nodeCount = nodeList.Count;
        var descriptorCount = descriptorList.Count;
        var primaryTable = tables[0];
        token.ThrowIfCancellationRequested();
        if (matrixCount < 0 || matrixCount > limits.MaximumMatrices || nodeCount < 0 ||
            nodeCount > limits.MaximumMatrices || descriptorCount < 0 || descriptorCount > limits.MaximumMatrices)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingCountLimit");
        if (matrixCount != nodeCount || nodeCount != descriptorCount)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingCountMismatch");
        if (primaryTable is null || primaryTable.Ordinal != 0 || primaryTable.Entries is null ||
            !ValidExtent(primaryTable.Extent, rootContainer) || !Within(skeleton.Extent, primaryTable.Extent))
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingPrimaryNameTableInvalid");
        var nameEntries = primaryTable.Entries;
        var nameCount = nameEntries.Count;
        if (nameCount < 0 || nameCount > Math.Min(limits.MaximumPrimaryNameEntries, MaximumNameEntries))
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingNameCountLimit");

        long estimate;
        try
        {
            estimate = checked(FixedMetadataBytes + matrixCount * 192L + nodeCount * 192L +
                descriptorCount * 96L + nameCount * 96L);
        }
        catch (OverflowException) { return Failure(out binding, out diagnostic, "NativeSkeletonBindingSizeOverflow"); }
        if (estimate > limits.MaximumMetadataBytes) return Failure(out binding, out diagnostic, "NativeSkeletonBindingMetadataLimit");

        var matrices = new SkeletonStoredMatrix[matrixCount];
        var nodes = new NodeArrayBodyNode[nodeCount];
        var descriptors = new NodeArrayBodyDescriptor[descriptorCount];
        var namesByKey = new Dictionary<uint, List<SkeletonNameEntry>>(Math.Min(nameCount, 2048));
        for (var index = 0; index < matrixCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var matrix = matrixList[index];
            if (matrix is null || matrix.Ordinal != index || matrix.RawBytes.Length != 64 ||
                matrix.Extent.Length != 64 || !ValidExtent(matrix.Extent, rootContainer) ||
                !Within(skeleton.Extent, matrix.Extent))
                return Failure(out binding, out diagnostic, "NativeSkeletonBindingMatrixExtentInvalid");
            matrices[index] = matrix;
        }
        for (var index = 0; index < nodeCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var node = nodeList[index];
            if (node is null || node.Ordinal != index || node.Components is null ||
                node.Components.RawBytes.Length != 40 || node.Components.Extent.Length != 40 ||
                !ValidExtent(node.Extent, rootContainer) || !ValidExtent(node.Components.Extent, rootContainer) ||
                !Within(nodeArray.Extent, node.Extent) || !Within(node.Extent, node.Components.Extent))
                return Failure(out binding, out diagnostic, "NativeSkeletonBindingNodeExtentInvalid");
            nodes[index] = node;
        }
        for (var index = 0; index < descriptorCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var descriptor = descriptorList[index];
            if (descriptor is null || descriptor.Extent.Length != 12 || !ValidExtent(descriptor.Extent, rootContainer) ||
                !Within(nodeArray.Extent, descriptor.Extent))
                return Failure(out binding, out diagnostic, "NativeSkeletonBindingDescriptorExtentInvalid");
            descriptors[index] = descriptor;
        }

        long childLinks = 0;
        for (var index = 0; index < nameCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = nameEntries[index];
            if (entry is null || entry.Ordinal != index || entry.ByteCount < 0 ||
                entry.StoredBytes.Length != entry.ByteCount || entry.NameExtent.Length != entry.ByteCount ||
                entry.Extent.Length != 6L + entry.ByteCount ||
                !ValidExtent(entry.Extent, rootContainer) || !ValidExtent(entry.NameExtent, rootContainer) ||
                entry.NameExtent.Offset != entry.Extent.Offset + 6 ||
                !Within(primaryTable.Extent, entry.Extent) || !Within(entry.Extent, entry.NameExtent))
                return Failure(out binding, out diagnostic, "NativeSkeletonBindingNameExtentInvalid");
            if (!namesByKey.TryGetValue(entry.Key, out var matchingNames))
                namesByKey.Add(entry.Key, matchingNames = []);
            matchingNames.Add(entry);
        }
        if (!TryValidateHierarchy(nodes, token, out childLinks))
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingHierarchyInvalid");
        if (nodeArray.StructuralDiagnostics is null)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingMetadataInvalid");
        foreach (var issue in nodeArray.StructuralDiagnostics)
        {
            token.ThrowIfCancellationRequested();
            if (issue.Code is "NodeArrayParentIndexInvalid" or "NodeArrayChildIndexInvalid" or
                "NodeArrayChildDuplicate" or "NodeArrayParentChildMismatch" or "NodeArrayIndexCycle" or
                "NodeArrayStoredParentCycle")
                return Failure(out binding, out diagnostic, "NativeSkeletonBindingHierarchyInvalid");
        }

        long nameCandidateCount = 0;
        foreach (var descriptor in descriptors)
        {
            token.ThrowIfCancellationRequested();
            if (namesByKey.TryGetValue(descriptor.Key, out var matches))
                nameCandidateCount = checked(nameCandidateCount + matches.Count);
        }
        if (nameCandidateCount > limits.MaximumNameCandidates)
            return Failure(out binding, out diagnostic, "NativeSkeletonBindingNameCandidateLimit");
        try
        {
            estimate = checked(estimate + childLinks * 4L + nameCandidateCount * 16L +
                matrixCount * 160L + 1024);
        }
        catch (OverflowException) { return Failure(out binding, out diagnostic, "NativeSkeletonBindingSizeOverflow"); }
        if (estimate > limits.MaximumMetadataBytes) return Failure(out binding, out diagnostic, "NativeSkeletonBindingMetadataLimit");

        var rows = new NativeSkeletonNodeArrayBindingEntry[matrixCount];
        for (var index = 0; index < rows.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var descriptor = descriptors[index];
            var matches = namesByKey.TryGetValue(descriptor.Key, out var found) ? found : [];
            var status = matches.Count switch
            {
                0 => NativeSkeletonNodeNameMatchStatus.Missing,
                1 => NativeSkeletonNodeNameMatchStatus.Unique,
                _ => NativeSkeletonNodeNameMatchStatus.Ambiguous
            };
            rows[index] = new(index, matrices[index], nodes[index], descriptor,
                new(descriptor.Key, status, Array.AsReadOnly(matches.ToArray())));
        }
        token.ThrowIfCancellationRequested();
        binding = new(skeleton.Identity, nodeArray.Identity, skeletonRevision,
            skeleton.EntityNode!, skeleton.Extent, nodeArray.Extent, entityNodeExtent,
            primaryTable.Extent, Array.AsReadOnly(rows), estimate);
        return true;

    }

    private static bool Failure(out NativeSkeletonNodeArrayBindingDocument? binding,
        out string? diagnostic, string code)
    { binding = null; diagnostic = code; return false; }

    private static bool TryValidateIdentityJoin(NativeSkeletonDocument skeleton, SourceRevision skeletonRevision,
        NativeNodeArrayBodyDocument nodeArray, out ResourceIdentity rootContainer,
        out DecodedExtent entityNodeExtent, out string failure)
    {
        rootContainer = default!;
        entityNodeExtent = default!;
        failure = "NativeSkeletonBindingNodeIdentityMismatch";
        if (skeleton.Identity is null || string.IsNullOrWhiteSpace(skeleton.Identity.IndexContentId) ||
            skeleton.Identity.IndexContentId.Length > 4096 || skeleton.Identity.Ordinal < 0 ||
            skeletonRevision.IndexContentId != skeleton.Identity.IndexContentId ||
            string.IsNullOrWhiteSpace(skeletonRevision.PhysicalSourceStamp) || skeletonRevision.PhysicalSourceStamp.Length > 4096 ||
            !TryMember(skeleton.Identity, 2013, out _)) return false;
        if (nodeArray.Owner is not { } owner) return false;
        if (owner.Revision != skeletonRevision)
        { failure = "NativeSkeletonBindingRevisionMismatch"; return false; }
        if (skeleton.EntityNode is not { } reference ||
            reference.Namespace != IdentifierNamespace.Native || reference.ResourceType != 2138 ||
            reference.Owner != skeleton.Identity || !uint.TryParse(reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var referencedId) ||
            reference.Id != referencedId.ToString(CultureInfo.InvariantCulture))
        { failure = "NativeSkeletonBindingEntityNodeMismatch"; return false; }
        if (skeleton.MeshPrefix is null || skeleton.MeshPrefix.Identity != skeleton.Identity)
            return false;
        if (skeleton.MeshPrefix.Entity is not { } entity || entity.Identity != skeleton.Identity || entity.Node != reference)
        { failure = "NativeSkeletonBindingEntityNodeMismatch"; return false; }

        rootContainer = new(skeleton.Identity.IndexContentId, skeleton.Identity.Ordinal);
        if (entity.FieldExtents is null || !entity.FieldExtents.TryGetValue("Node", out var nodeExtent) || nodeExtent is null)
            return false;
        entityNodeExtent = nodeExtent;
        if (entityNodeExtent.Length != 8 || !ValidExtent(entityNodeExtent, rootContainer) ||
            !ValidExtent(entity.EntityExtent, rootContainer) || !Within(entity.EntityExtent, entityNodeExtent)) return false;

        if (nodeArray.Identity is null ||
            nodeArray.Identity.IndexContentId != skeleton.Identity.IndexContentId ||
            nodeArray.Identity.Ordinal != skeleton.Identity.Ordinal ||
            owner.Identity != nodeArray.Identity || owner.Revision != nodeArray.Revision ||
            owner.InheritedNode is not { } inherited || inherited.Identity != nodeArray.Identity ||
            inherited.Revision != nodeArray.Revision || inherited.NativeId != referencedId ||
            !TryMember(nodeArray.Identity, 2138, out var memberId) || memberId != referencedId)
            return false;
        return true;
    }

    private static bool TryMember(ResourceIdentity identity, uint requiredType, out uint id)
    {
        id = 0;
        var member = identity.MemberPath;
        if (string.IsNullOrEmpty(member) || member.Length > 4096) return false;
        var parts = member.Split('/');
        if (parts.Length != 4 || parts[0] != "dc-object" ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directory) ||
            parts[1] != directory.ToString(CultureInfo.InvariantCulture) ||
            !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var type) ||
            type != requiredType || parts[2] != requiredType.ToString(CultureInfo.InvariantCulture) ||
            !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
            parts[3] != id.ToString(CultureInfo.InvariantCulture)) return false;
        return true;
    }

    private static bool TryValidateHierarchy(IReadOnlyList<NodeArrayBodyNode> nodes, CancellationToken token,
        out long childLinks)
    {
        childLinks = 0;
        var childOwners = new int[nodes.Count]; Array.Fill(childOwners, -1);
        var parents = new int[nodes.Count]; Array.Fill(parents, -1);
        for (var parentIndex = 0; parentIndex < nodes.Count; parentIndex++)
        {
            token.ThrowIfCancellationRequested();
            var parent = nodes[parentIndex];
            if (parent.StoredParent != uint.MaxValue)
            {
                if (parent.StoredParent >= nodes.Count || parent.StoredParent == parentIndex) return false;
                parents[parentIndex] = (int)parent.StoredParent;
            }
            if (parent.Children is null) return false;
            var childCount = parent.Children.Count;
            childLinks = checked(childLinks + childCount);
            if (childLinks > MaximumChildLinks) return false;
            for (var childIndex = 0; childIndex < childCount; childIndex++)
            {
                token.ThrowIfCancellationRequested();
                var child = parent.Children[childIndex];
                if (child >= nodes.Count || childOwners[child] >= 0 ||
                    nodes[(int)child].StoredParent != (uint)parentIndex) return false;
                childOwners[child] = parentIndex;
            }
        }
        for (var child = 0; child < nodes.Count; child++)
        {
            token.ThrowIfCancellationRequested();
            var expectedParent = parents[child];
            if (expectedParent < 0)
            {
                if (childOwners[child] >= 0 || nodes[child].StoredParent != uint.MaxValue) return false;
            }
            else if (childOwners[child] != expectedParent) return false;
        }
        var state = new byte[nodes.Count];
        for (var start = 0; start < nodes.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            if (state[start] == 2) continue;
            var current = start;
            while (current >= 0 && state[current] == 0)
            {
                token.ThrowIfCancellationRequested(); state[current] = 1; current = parents[current];
            }
            if (current >= 0 && state[current] == 1) return false;
            current = start;
            while (current >= 0 && state[current] == 1)
            { token.ThrowIfCancellationRequested(); state[current] = 2; current = parents[current]; }
        }
        return true;
    }

    private static bool ValidExtent(DecodedExtent extent, ResourceIdentity root) =>
        extent is not null && extent.Container == root && extent.Offset >= 0 && extent.Length >= 0 &&
        extent.Offset <= long.MaxValue - extent.Length;

    private static bool Within(DecodedExtent parent, DecodedExtent child) =>
        child.Offset >= parent.Offset && child.Offset <= parent.Offset + parent.Length &&
        child.Length <= parent.Offset + parent.Length - child.Offset;
}
