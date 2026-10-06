using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class SceneHierarchyBuilderTests
{
    [Fact]
    public void ComposesLocalThenParentAndNeverUsesTheStoredWorldCache()
    {
        var parentMatrix = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 20, 30);
        var parent = SceneFixture.Frame(0, 11, parentMatrix, child: 12);
        var child = SceneFixture.Frame(1, 12, Matrix4x4.CreateTranslation(2, 0, 0), parent: 11);
        var hierarchy = Build(parent, child);
        Assert.Empty(hierarchy.Diagnostics);
        Assert.Single(hierarchy.Roots);
        var actual = hierarchy.Nodes[1];
        Assert.Equal(parent.Identity, actual.ParentIdentity);
        Assert.Equal(child.Identity, Assert.Single(hierarchy.Nodes[0].Children));
        Assert.True(SceneTransformMath.TryRead(actual.WorldTransform!, out var world));
        var origin = Vector3.Transform(Vector3.Zero, world);
        Assert.InRange(Vector3.Distance(origin, new(10, 22, 30)), 0, 0.0001f);
        Assert.NotEqual(actual.Record.StoredWorldTransform, actual.WorldTransform);
    }

    [Fact]
    public void UsesDeclaredSiblingOrderInsteadOfDirectoryOrder()
    {
        var root = SceneFixture.Frame(0, 11, Matrix4x4.Identity, child: 13);
        var last = SceneFixture.Frame(1, 12, Matrix4x4.Identity, parent: 11);
        var first = SceneFixture.Frame(2, 13, Matrix4x4.Identity, parent: 11, sibling: 12);
        var result = Build(root, last, first);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { first.Identity, last.Identity }, result.Nodes[0].Children);
    }

    [Fact]
    public void MissingAndAmbiguousParentsDoNotBecomeRootsAtTheOrigin()
    {
        var missing = SceneFixture.Frame(0, 11, Matrix4x4.Identity, parent: 99);
        var missingResult = Build(missing);
        Assert.Empty(missingResult.Roots);
        Assert.Null(missingResult.Nodes[0].WorldTransform);
        Assert.Equal(SceneTransformStatus.MissingParent, missingResult.Nodes[0].TransformStatus);
        var a = SceneFixture.Frame(1, 99, Matrix4x4.Identity);
        var b = SceneFixture.Frame(2, 99, Matrix4x4.Identity);
        var ambiguous = Build(missing, a, b).Nodes[0];
        Assert.Equal(SceneTransformStatus.AmbiguousParent, ambiguous.TransformStatus);
        Assert.Equal(2, ambiguous.ParentResolution!.Candidates.Count);
        Assert.Null(ambiguous.WorldTransform);
    }

    [Fact]
    public void ReportsCyclesAndUnavailableParentAttachments()
    {
        var a = SceneFixture.Frame(0, 11, Matrix4x4.Identity, parent: 12, child: 12);
        var b = SceneFixture.Frame(1, 12, Matrix4x4.Identity, parent: 11, child: 11);
        var cyclic = Build(a, b);
        Assert.All(cyclic.Nodes, node => Assert.Equal(SceneTransformStatus.Cycle, node.TransformStatus));
        Assert.All(cyclic.Nodes, node => Assert.Null(node.WorldTransform));
        var root = SceneFixture.Frame(0, 11, Matrix4x4.Identity, child: 12);
        var attached = SceneFixture.Frame(1, 12, Matrix4x4.Identity, parent: 11, attachment: 2);
        var child = Build(root, attached).Nodes[1];
        Assert.Equal(SceneTransformStatus.UnsupportedAttachment, child.TransformStatus);
        Assert.Null(child.WorldTransform);
        Assert.NotNull(child.LocalTransform);
    }

    [Fact]
    public void PivotAdjustmentChangesTranslationAndPreservesTheMatrixBasis()
    {
        var rotation = Matrix4x4.CreateRotationZ(MathF.PI / 2);
        var frame = SceneFixture.Frame(0, 11, rotation, flags: 9, pivot: new(2, 0, 0));
        var node = SceneFixture.Read([frame])[0];
        Assert.True(SceneTransformMath.TryGetLocal(node, out var adjusted));
        Assert.InRange(Vector3.Distance(adjusted.Translation, new(2, -2, 0)), 0, 0.0001f);
        Assert.Equal(rotation.M12, adjusted.M12);
        Assert.Equal(rotation.M21, adjusted.M21);
        var singular = node with { StoredLocalTransform = SceneTransformMath.ToSource(Matrix4x4.CreateScale(0, 1, 1)) };
        Assert.False(SceneTransformMath.TryGetLocal(singular, out _));
        var subnormal = node with { StoredLocalTransform = SceneTransformMath.ToSource(Matrix4x4.CreateScale(1e-20f, 1, 1)) };
        Assert.False(SceneTransformMath.TryGetLocal(subnormal, out _));
    }

    [Fact]
    public void ReportsSiblingCyclesAmongRootsWithoutLooping()
    {
        var first = SceneFixture.Frame(0, 11, Matrix4x4.Identity, sibling: 12);
        var second = SceneFixture.Frame(1, 12, Matrix4x4.Identity, sibling: 11);
        var result = Build(first, second);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SiblingCycle");
        Assert.Equal(2, result.Nodes.Count);
    }

    [Fact]
    public void RejectsCrossRevisionInputsAndHonorsLimitsAndCancellation()
    {
        var frame = SceneFixture.Frame(0, 11, Matrix4x4.Identity);
        var records = SceneFixture.Read([frame]);
        var directory = SceneFixture.Directory(frame);
        var builder = new SceneHierarchyBuilder();
        Assert.Throws<ArgumentException>(() => builder.Build(directory,
            [records[0] with { Revision = new("generated-index", "other-revision") }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(directory, records, new(MaximumNodes: 0)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => builder.Build(directory, records, token: cancel.Token));
        var child = SceneFixture.Frame(1, 12, Matrix4x4.Identity, parent: 11);
        var limited = builder.Build(SceneFixture.Directory(frame, child), SceneFixture.Read([child, frame]), new(MaximumDepth: 1));
        Assert.Equal(SceneTransformStatus.DepthLimit, limited.Nodes[0].TransformStatus);
    }

    [Fact]
    public void NativeReferencesCannotResolveAcrossCompanions()
    {
        var frame = SceneFixture.Frame(0, 11, Matrix4x4.Identity);
        var resolver = new NativeReferenceResolver(SceneFixture.Directory(frame));
        var reference = new ResourceReference(IdentifierNamespace.Native, "11", new("generated-index", 99), 2002);
        Assert.Equal(ReferenceStatus.Unresolved, resolver.Resolve(reference).Status);
        Assert.Empty(resolver.Resolve(reference).Candidates);
        Assert.Equal(frame.Identity, Assert.Single(resolver.Resolve(reference with { Owner = frame.Identity }).Candidates));
    }

    private static SceneHierarchy Build(params NativeObjectFrame[] frames) =>
        new SceneHierarchyBuilder().Build(SceneFixture.Directory(frames), SceneFixture.Read(frames));
}
