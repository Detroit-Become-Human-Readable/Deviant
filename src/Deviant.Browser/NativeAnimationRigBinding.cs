using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

public sealed record NativeAnimationTrackBinding(uint Key, int TrackOrdinal, int NodeOrdinal);
public sealed record NativeAnimationJointBinding(uint Key, int NodeOrdinal, int? ParentOrdinal);

/// <summary>Maps keyed clip tracks into the validated source node hierarchy.</summary>
public sealed class NativeAnimationRigBinding
{
    private readonly NativeSkeletonNodeArrayBindingDocument rig;
    private readonly AnimationWideSamples clip;
    private readonly IReadOnlyDictionary<int, int> tracksByNode;
    private readonly int rootTrack;
    private readonly float translationScale;
    private readonly NativeAnimationReferencePose? referencePose;

    public NativeAnimationRigBinding(NativeSkeletonNodeArrayBindingDocument rig, AnimationWideSamples clip,
        ResourceIdentity clipIdentity, SourceRevision clipRevision, NativeAnimationReferencePose referencePose)
        : this(rig, clip, clipIdentity, clipRevision,
            AnimationRetargeting.TranslationRatio(clip.SourceHeight, referencePose.Height), referencePose) { }

    public NativeAnimationRigBinding(NativeSkeletonNodeArrayBindingDocument rig, AnimationWideSamples clip,
        ResourceIdentity clipIdentity, SourceRevision clipRevision, float translationScale,
        NativeAnimationReferencePose? referencePose = null)
    {
        ArgumentNullException.ThrowIfNull(rig); ArgumentNullException.ThrowIfNull(clip);
        if (rig.Revision != clipRevision || clipIdentity.IndexContentId != rig.Revision.IndexContentId ||
            rig.Entries.Count is < 1 or > 8192 || rig.MatrixCount != rig.Entries.Count)
            throw new InvalidDataException("AnimationRigSourceMismatch");
        this.rig = rig; this.clip = clip; ClipIdentity = clipIdentity;
        if (!float.IsFinite(translationScale) || translationScale <= 0)
            throw new InvalidDataException("AnimationTranslationRatioUnavailable");
        this.translationScale = translationScale;
        if (referencePose is not null && (referencePose.Identity != rig.NodeArrayIdentity || referencePose.Revision != rig.Revision ||
            referencePose.References.Count != rig.MatrixCount || referencePose.Parents.Count != rig.MatrixCount))
            throw new InvalidDataException("AnimationReferenceSourceMismatch");
        this.referencePose = referencePose;
        var keyed = rig.Entries.GroupBy(x => x.Descriptor.Key).ToDictionary(g => g.Key, g => g.ToArray());
        var resolved = new List<NativeAnimationTrackBinding>(); var missing = new List<uint>(); rootTrack = -1;
        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            var key = clip.Tracks[i].Key;
            if (key == 0)
            {
                if ((clip.Tracks[i].Flags & 0x40) != 0) throw new InvalidDataException("AnimationRelativeOwnerUnsupported");
                rootTrack = i; continue;
            }
            if (!keyed.TryGetValue(key, out var matches) || matches.Length != 1) { missing.Add(key); continue; }
            resolved.Add(new(key, i, matches[0].Ordinal));
        }
        Tracks = resolved.AsReadOnly(); MissingKeys = missing.AsReadOnly();
        if (missing.Count != 0 || resolved.Count == 0) throw new InvalidDataException("AnimationRigKeysUnavailable");
        tracksByNode = resolved.ToDictionary(x => x.NodeOrdinal, x => x.TrackOrdinal);
        for (int ordinal = 0; ordinal < rig.Entries.Count; ordinal++)
        {
            var entry = rig.Entries[ordinal];
            if (entry.Ordinal != ordinal || entry.Ordinal != entry.Node.Ordinal ||
                entry.Node.StoredParent != uint.MaxValue && entry.Node.StoredParent >= rig.Entries.Count ||
                referencePose is not null && referencePose.Parents[ordinal] !=
                    (entry.Node.StoredParent == uint.MaxValue ? (uint)rig.MatrixCount : entry.Node.StoredParent))
                throw new InvalidDataException("AnimationRigHierarchyInvalid");
        }
        Joints = Array.AsReadOnly(rig.Entries.Select(entry => new NativeAnimationJointBinding(entry.Descriptor.Key,
            entry.Ordinal, entry.Node.StoredParent == uint.MaxValue ? null : checked((int)entry.Node.StoredParent))).ToArray());
    }
    public ResourceIdentity ClipIdentity { get; }
    public ResourceIdentity SkeletonIdentity => rig.SkeletonIdentity;
    public SourceRevision Revision => rig.Revision;
    public IReadOnlyList<NativeAnimationJointBinding> Joints { get; }
    public IReadOnlyList<NativeAnimationTrackBinding> Tracks { get; }
    public IReadOnlyList<uint> MissingKeys { get; }
    public AnimationVersion14Timing Timing => clip.Timing;
    public float TranslationScale => translationScale;

    public Matrix4x4 RootLocalAt(float seconds)
    {
        if (rootTrack < 0) return Matrix4x4.Identity;
        var value = clip.Sample(seconds, rootTrack);
        var root = Matrix4x4.CreateScale(value.Scale.X, value.Scale.Y, value.Scale.Z) *
            Matrix4x4.CreateFromQuaternion(new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W)) *
            Matrix4x4.CreateTranslation(value.Translation.X, value.Translation.Y, value.Translation.Z);
        if (!Finite(root)) throw new InvalidDataException("AnimationRootPoseNonfinite");
        return root;
    }

    public Matrix4x4[] LocalPose(float seconds)
    {
        var locals = new Matrix4x4[rig.MatrixCount];
        foreach (var entry in rig.Entries)
        {
            var q = entry.Node.Components.QuaternionXyzw();
            var t = entry.Node.Components.Translation(); var scale = entry.Node.Components.BasisScale();
            var reference = referencePose?.References[entry.Ordinal] ??
                new AnimationTransformSample(new(q.X, q.Y, q.Z, q.W), new(t, 0), new(scale, 1));
            q = new(reference.Rotation.X, reference.Rotation.Y, reference.Rotation.Z, reference.Rotation.W);
            t = new(reference.Translation.X, reference.Translation.Y, reference.Translation.Z);
            scale = new(reference.Scale.X, reference.Scale.Y, reference.Scale.Z);
            if (tracksByNode.TryGetValue(entry.Ordinal, out var track))
            {
                var value = clip.Sample(seconds, track);
                if ((clip.Tracks[track].Flags & 0x40) != 0)
                    value = AnimationPoseComposition.Relative(value, reference, translationScale);
                q = new(value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W);
                t = new(value.Translation.X, value.Translation.Y, value.Translation.Z);
                scale = new(value.Scale.X, value.Scale.Y, value.Scale.Z);
            }
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
            if (!Finite(local)) throw new InvalidDataException("AnimationPoseNonfinite");
            locals[entry.Ordinal] = local;
        }
        return locals;
    }

    public Matrix4x4[] GlobalPose(float seconds, bool inPlace, CancellationToken token = default)
    {
        var locals = LocalPose(seconds); var globals = new Matrix4x4[locals.Length]; var done = new bool[locals.Length];
        var root = inPlace ? Matrix4x4.Identity : RootLocalAt(seconds);
        var path = new Stack<int>();
        for (int ordinal = 0; ordinal < globals.Length; ordinal++)
        {
            token.ThrowIfCancellationRequested(); int current = ordinal; var visited = new HashSet<int>();
            while (current >= 0 && !done[current])
            {
                if (!visited.Add(current)) throw new InvalidDataException("AnimationRigHierarchyCycle");
                path.Push(current); var parent = rig.Entries[current].Node.StoredParent;
                current = parent == uint.MaxValue ? -1 : checked((int)parent);
            }
            while (path.TryPop(out current))
            {
                var parent = rig.Entries[current].Node.StoredParent;
                globals[current] = locals[current] * (parent == uint.MaxValue ? root : globals[(int)parent]); done[current] = true;
            }
        }
        return globals;
    }
    private static bool Finite(Matrix4x4 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
