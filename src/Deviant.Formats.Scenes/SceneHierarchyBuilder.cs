using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record SceneHierarchyLimits(int MaximumNodes = 131_072, int MaximumDepth = 4096);

public sealed class SceneHierarchyBuilder
{
    public SceneHierarchy Build(NativeObjectDirectoryDocument directory, IReadOnlyList<SceneNodeRecord> records,
        SceneHierarchyLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(records);
        limits ??= new();
        if (limits.MaximumNodes is < 1 or > 131_072 || limits.MaximumDepth is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (records.Count > limits.MaximumNodes) throw new ArgumentException("The node limit was exceeded.", nameof(records));
        token.ThrowIfCancellationRequested();
        var resolver = new NativeReferenceResolver(directory);
        var frames = directory.Objects.ToDictionary(frame => frame.Identity);
        var nodes = new Dictionary<ResourceIdentity, WorkNode>();
        var diagnostics = new List<SceneStructureDiagnostic>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (record.Revision != directory.Revision || !frames.TryGetValue(record.Identity, out var frame) ||
                frame.NativeType != NodeRecordReader.NativeType || frame.NativeId != record.NativeId ||
                frame.PayloadExtent != record.PayloadExtent || !nodes.TryAdd(record.Identity, new(record)))
                throw new ArgumentException("Node records do not match the supplied directory.", nameof(records));
        }

        foreach (var node in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            node.ParentResolution = Resolve(node, node.Record.Parent, "Parent");
            node.FirstChildResolution = Resolve(node, node.Record.FirstChild, "FirstChild");
            node.NextSiblingResolution = Resolve(node, node.Record.NextSibling, "NextSibling");
            node.Parent = Target(node.ParentResolution);
            node.FirstChild = Target(node.FirstChildResolution);
            node.NextSibling = Target(node.NextSiblingResolution);
            if (node.Parent is not null) node.Parent.Children.Add(node);
            if (SceneTransformMath.TryGetLocal(node.Record, out var local)) node.Local = local;
            else diagnostics.Add(new("InvalidLocalTransform", node.Record.Identity));
        }

        foreach (var node in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (node.NextSibling is { } sibling && sibling.Parent?.Record.Identity != node.Parent?.Record.Identity)
                diagnostics.Add(new("SiblingParentMismatch", node.Record.Identity, "NextSibling"));
            var ordered = new List<WorkNode>();
            var seen = new HashSet<ResourceIdentity>();
            var current = node.FirstChild;
            while (current is not null)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(current.Record.Identity))
                {
                    diagnostics.Add(new("SiblingCycle", current.Record.Identity, "NextSibling"));
                    break;
                }
                if (current.Parent != node)
                {
                    diagnostics.Add(new("ChildParentMismatch", node.Record.Identity, "FirstChild"));
                    break;
                }
                ordered.Add(current);
                current = current.NextSibling;
            }
            if (ordered.Count != node.Children.Count)
                diagnostics.Add(new("ChildListIncomplete", node.Record.Identity, "FirstChild"));
            ordered.AddRange(node.Children.Where(child => !seen.Contains(child.Record.Identity)));
            node.Children = ordered;
        }

        var siblingCompleted = new HashSet<WorkNode>();
        foreach (var start in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (siblingCompleted.Contains(start)) continue;
            var seen = new HashSet<WorkNode>();
            var current = start;
            while (current is not null && !siblingCompleted.Contains(current))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(current))
                {
                    diagnostics.Add(new("SiblingCycle", current.Record.Identity, "NextSibling"));
                    break;
                }
                current = current.NextSibling;
            }
            siblingCompleted.UnionWith(seen);
        }

        foreach (var start in nodes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (start.Completed) continue;
            var pending = new List<WorkNode>();
            var positions = new Dictionary<WorkNode, int>();
            var current = start;
            while (current is not null && !current.Completed)
            {
                token.ThrowIfCancellationRequested();
                if (positions.TryGetValue(current, out var cycleStart))
                {
                    foreach (var member in pending.Skip(cycleStart))
                    {
                        member.Status = SceneTransformStatus.Cycle;
                        member.Completed = true;
                        diagnostics.Add(new("ParentCycle", member.Record.Identity, "Parent"));
                    }
                    break;
                }
                if (pending.Count >= limits.MaximumDepth)
                {
                    foreach (var member in pending)
                    {
                        member.Status = SceneTransformStatus.DepthLimit;
                        member.Completed = true;
                        diagnostics.Add(new("HierarchyDepthLimit", member.Record.Identity));
                    }
                    break;
                }
                positions.Add(current, pending.Count);
                pending.Add(current);
                current = current.Parent;
            }
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                var node = pending[index];
                if (node.Completed) continue;
                node.Status = TransformStatus(node);
                if (node.Status == SceneTransformStatus.Available)
                {
                    var world = node.Parent?.World is { } parentWorld
                        ? node.Local!.Value * parentWorld : node.Local!.Value;
                    if (SceneTransformMath.IsFinite(world)) node.World = world;
                    else
                    {
                        node.Status = SceneTransformStatus.Invalid;
                        diagnostics.Add(new("InvalidWorldTransform", node.Record.Identity));
                    }
                }
                node.Completed = true;
            }
        }

        token.ThrowIfCancellationRequested();
        var result = records.Select(record => nodes[record.Identity]).Select(node => new SceneHierarchyNode(
            node.Record, node.ParentResolution, node.FirstChildResolution, node.NextSiblingResolution,
            node.Parent?.Record.Identity, node.Children.Select(child => child.Record.Identity).ToArray(),
            node.Local is { } local ? SceneTransformMath.ToSource(local) : null,
            node.World is { } world ? SceneTransformMath.ToSource(world) : null, node.Status)).ToArray();
        return new(directory.Identity, directory.Revision, result,
            records.Where(record => record.Parent is null).Select(record => record.Identity).ToArray(), diagnostics.ToArray());

        ReferenceResolution? Resolve(WorkNode node, ResourceReference? reference, string field)
        {
            if (reference is null) return null;
            var resolution = resolver.Resolve(reference);
            if (resolution.Status != ReferenceStatus.Unique)
                diagnostics.Add(new("NodeReference" + resolution.Status, node.Record.Identity, field));
            else if (!nodes.ContainsKey(resolution.Candidates[0]))
                diagnostics.Add(new("NodeReferenceTypeUnsupported", node.Record.Identity, field));
            return resolution;
        }

        WorkNode? Target(ReferenceResolution? resolution) => resolution is { Status: ReferenceStatus.Unique, Candidates.Count: 1 }
            ? nodes.GetValueOrDefault(resolution.Candidates[0]) : null;
    }

    private static SceneTransformStatus TransformStatus(WorkNode node)
    {
        if (node.Local is null) return SceneTransformStatus.Invalid;
        if (node.Record.Parent is null) return SceneTransformStatus.Available;
        if (node.ParentResolution?.Status == ReferenceStatus.Ambiguous) return SceneTransformStatus.AmbiguousParent;
        if (node.ParentResolution?.Status != ReferenceStatus.Unique) return SceneTransformStatus.MissingParent;
        if (node.Parent is null) return SceneTransformStatus.UnsupportedParent;
        if (node.Record.ParentAttachmentIndex >= 0) return SceneTransformStatus.UnsupportedAttachment;
        return node.Parent.World is null ? SceneTransformStatus.ParentUnavailable : SceneTransformStatus.Available;
    }

    private sealed class WorkNode(SceneNodeRecord record)
    {
        public SceneNodeRecord Record { get; } = record;
        public ReferenceResolution? ParentResolution, FirstChildResolution, NextSiblingResolution;
        public WorkNode? Parent, FirstChild, NextSibling;
        public List<WorkNode> Children = [];
        public Matrix4x4? Local, World;
        public SceneTransformStatus Status;
        public bool Completed;
    }
}
