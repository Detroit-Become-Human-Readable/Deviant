using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkeletonNodeArrayBindingReaderTests
{
    [Fact]
    public void BindsSameOrdinalStoredInputsAndJoinsUniqueNameByKey()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, descriptorKeys: [9]);

        Assert.True(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, body,
            out var binding, out var diagnostic), diagnostic);
        var result = Assert.IsType<NativeSkeletonNodeArrayBindingDocument>(binding);
        var row = Assert.Single(result.Entries);
        Assert.Equal(skeleton.Identity, result.SkeletonIdentity);
        Assert.Equal(body.Identity, result.NodeArrayIdentity);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(0, row.Ordinal);
        Assert.Equal(row.Ordinal, row.StoredMatrix.Ordinal);
        Assert.Equal(row.Ordinal, row.Node.Ordinal);
        Assert.Equal(9u, row.Descriptor.Key);
        Assert.Equal(NativeSkeletonNodeNameMatchStatus.Unique, row.NameMatch.Status);
        Assert.Equal(2, Assert.Single(row.NameMatch.Entries).Ordinal);
        Assert.Equal(skeleton.Matrices[0].Extent, row.StoredMatrix.Extent);
        Assert.Equal(body.Nodes[0].Components.Extent, row.Node.Components.Extent);
        Assert.Equal(body.Descriptors[0].Extent, row.Descriptor.Extent);
        Assert.Equal(skeleton.NameTables[0].Entries[2].NameExtent, row.NameMatch.Entries[0].NameExtent);
        Assert.Equal(new ResourceReference(IdentifierNamespace.Native, "22", skeleton.Identity, 2138), result.EntityNode);
        Assert.True(skeleton.NameTables[0].Entries[2].StoredBytes.Span.SequenceEqual(
            row.NameMatch.Entries[0].StoredBytes.Span));
    }

    [Theory]
    [InlineData(7u, NativeSkeletonNodeNameMatchStatus.Ambiguous, 2)]
    [InlineData(99u, NativeSkeletonNodeNameMatchStatus.Missing, 0)]
    public void ReportsDuplicateAndMissingPrimaryNamesWithoutOrdinalGuessing(uint descriptorKey,
        NativeSkeletonNodeNameMatchStatus expectedStatus, int expectedNames)
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [descriptorKey]);

        Assert.True(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, body,
            out var binding, out var diagnostic), diagnostic);
        var match = Assert.Single(Assert.IsType<NativeSkeletonNodeArrayBindingDocument>(binding).Entries).NameMatch;
        Assert.Equal(descriptorKey, match.DescriptorKey);
        Assert.Equal(expectedStatus, match.Status);
        Assert.Equal(expectedNames, match.Entries.Count);
        if (expectedStatus == NativeSkeletonNodeNameMatchStatus.Ambiguous)
            Assert.Equal(new[] { 0, 1 }, match.Entries.Select(entry => entry.Ordinal));
    }

    [Fact]
    public void RejectsEntityReferenceThatDoesNotIdentifyTheSuppliedNodeArray()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [9]);
        var wrong = skeleton with { EntityNode = skeleton.EntityNode! with { Id = "23" } };

        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(wrong, revision, body,
            out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingEntityNodeMismatch", diagnostic);

        var wrongTypeReference = skeleton.EntityNode! with { ResourceType = 2002 };
        var wrongType = skeleton with { EntityNode = wrongTypeReference,
            MeshPrefix = skeleton.MeshPrefix with { Entity = skeleton.MeshPrefix.Entity with { Node = wrongTypeReference } } };
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(wrongType, revision, body,
            out binding, out diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingEntityNodeMismatch", diagnostic);
    }

    [Fact]
    public void RequiresCallerSuppliedSkeletonRevisionToMatchNodeOwnerRevision()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [9]);

        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton,
            revision with { PhysicalSourceStamp = "other-stamp" }, body, out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingRevisionMismatch", diagnostic);
    }

    [Fact]
    public void RejectsForgedNodeMemberIdentityAndSourceContainerMismatch()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [9]);
        var owner = body.Owner;
        var forged = body with { Owner = owner with
        {
            Identity = owner.Identity with { MemberPath = "dc-object/9/2138/23" }
        } };

        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, forged,
            out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingNodeIdentityMismatch", diagnostic);

        var otherOrdinal = body with { Owner = owner with
        {
            Identity = owner.Identity with { Ordinal = owner.Identity.Ordinal + 1 }
        } };
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, otherOrdinal,
            out binding, out diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingNodeIdentityMismatch", diagnostic);
    }

    [Fact]
    public void RejectsUnequalCountsAndInconsistentParentChildHierarchy()
    {
        var (skeleton, revision) = ReadSkeleton();
        var twoNodes = ReadNodeArray(skeleton, revision, [9, 9], nodes: 2);
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, twoNodes,
            out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingCountMismatch", diagnostic);

        var invalid = ReadNodeArray(skeleton, revision, [9], children: [0]);
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, invalid,
            out binding, out diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingHierarchyInvalid", diagnostic);
    }

    [Fact]
    public void RejectsDuplicateChildOwnershipAndParentCycle()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [9], parent: 0, children: [0]);

        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, body,
            out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingHierarchyInvalid", diagnostic);

        var (twoMatrixSkeleton, twoMatrixRevision) = ReadSkeleton(2);
        var duplicateChildOwner = ReadNodeArray(twoMatrixSkeleton, twoMatrixRevision, [9, 9], nodes: 2,
            parents: [uint.MaxValue, 0], childLists: [[1], [1]]);
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(twoMatrixSkeleton, twoMatrixRevision,
            duplicateChildOwner, out binding, out diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingHierarchyInvalid", diagnostic);

        var twoNodeCycle = ReadNodeArray(twoMatrixSkeleton, twoMatrixRevision, [9, 9], nodes: 2,
            parents: [1, 0], childLists: [[1], [0]]);
        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(twoMatrixSkeleton, twoMatrixRevision,
            twoNodeCycle, out binding, out diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingHierarchyInvalid", diagnostic);
    }

    [Fact]
    public void EnforcesAllocationLimitsBeforeProducingBindingRows()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [7]);
        var limits = new NativeSkeletonNodeArrayBindingLimits(MaximumNameCandidates: 1);

        Assert.False(new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton, revision, body, limits,
            out var binding, out var diagnostic));
        Assert.Null(binding);
        Assert.Equal("NativeSkeletonBindingNameCandidateLimit", diagnostic);
    }

    [Fact]
    public void PropagatesCancellationBeforeBindingWork()
    {
        var (skeleton, revision) = ReadSkeleton();
        var body = ReadNodeArray(skeleton, revision, [9]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonNodeArrayBindingReader().TryRead(
            skeleton, revision, body, out _, out _, cancelled.Token));
    }

    private static (NativeSkeletonDocument Skeleton, SourceRevision Revision) ReadSkeleton(int matrixCount = 1)
    {
        var sample = NativeSkeletonReaderTests.Create(false);
        var frame = sample.Frame;
        if (matrixCount != 1)
        {
            var source = frame.RawPayload.ToArray();
            var matrixOffset = sample.SkeletonAt + 20;
            var afterMatrix = matrixOffset + 64;
            var repeated = source.AsSpan(matrixOffset, 64).ToArray();
            var expanded = new byte[checked(source.Length + (matrixCount - 1) * 64)];
            source.AsSpan(0, afterMatrix).CopyTo(expanded);
            for (var index = 0; index < matrixCount - 1; index++)
                repeated.CopyTo(expanded.AsSpan(afterMatrix + index * 64, 64));
            source.AsSpan(afterMatrix).CopyTo(expanded.AsSpan(afterMatrix + (matrixCount - 1) * 64));
            BinaryPrimitives.WriteUInt32LittleEndian(expanded.AsSpan(sample.SkeletonAt + 16), (uint)matrixCount);
            frame = frame with { RawPayload = expanded,
                PayloadExtent = frame.PayloadExtent with { Length = expanded.Length } };
        }
        Assert.True(new NativeSkeletonReader().TryRead(frame, sample.Entity,
            out var skeleton, out var diagnostic), diagnostic);
        return (skeleton!, new(frame.Identity.IndexContentId, "generated-source-stamp"));
    }

    private static NativeNodeArrayBodyDocument ReadNodeArray(NativeSkeletonDocument skeleton,
        SourceRevision revision, uint[] descriptorKeys, int nodes = 1, uint parent = uint.MaxValue,
        uint[]? children = null, uint[]? parents = null, uint[][]? childLists = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var inherited = new byte[208];
        BinaryPrimitives.WriteUInt32LittleEndian(inherited, 2);
        "NODE    "u8.CopyTo(inherited.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(inherited.AsSpan(12), 5);
        "NODEAR  "u8.CopyTo(inherited.AsSpan(196));
        BinaryPrimitives.WriteUInt32LittleEndian(inherited.AsSpan(204), 4);
        writer.Write(inherited);
        writer.Write(14u); writer.Write((uint)nodes); writer.Write(0u);
        for (var index = 0; index < nodes; index++)
        {
            var nodeChildren = childLists is null ? children : childLists[index];
            writer.Write(0u);
            for (var component = 0; component < 10; component++) writer.Write((float)(component + index));
            writer.Write(0u); writer.Write(parents is null ? parent : parents[index]);
            writer.Write((uint)(nodeChildren?.Length ?? 0));
            if (nodeChildren is not null) foreach (var child in nodeChildren) writer.Write(child);
            writer.Write(0u);
        }
        for (var index = 0; index < descriptorKeys.Length; index++)
        {
            writer.Write(descriptorKeys[index]); writer.Write((ushort)index); writer.Write((ushort)0); writer.Write(0u);
        }
        writer.Write(0u); writer.Write(0u); // optional section and compact records
        writer.Write(1u); writer.Write((byte)0); writer.Write(0u); writer.Write(2u); writer.Write(3u); writer.Write(0u);

        var payload = stream.ToArray();
        var container = skeleton.Matrices[0].Extent.Container;
        var directory = 9;
        var nodeIdentity = new ResourceIdentity(skeleton.Identity.IndexContentId, skeleton.Identity.Ordinal,
            $"dc-object/{directory}/2138/22");
        var frame = new NativeObjectFrame(nodeIdentity, directory, 2138, 22,
            new(container, 24, 8), new(container, 512, 13), new(container, 525, payload.Length),
            new byte[13], payload, 0, 0, 0, 2);
        var prefix = new NativeNodeArrayReader().Read(frame, revision, []);
        Assert.NotNull(prefix.Value);
        var body = new NativeNodeArrayBodyReader().Read(prefix);
        return Assert.IsType<NativeNodeArrayBodyDocument>(body.Value);
    }
}
