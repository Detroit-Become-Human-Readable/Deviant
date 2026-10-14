using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;

namespace Deviant.Browser;

public sealed record NativeAnimationFbxSamplingOptions(int MaximumKeys = 2_000_000,
    double MaximumSamplesPerSecond = 480, double MaximumRotationErrorRadians = 0.0001,
    double MaximumTranslationError = 0.00002, double MaximumScaleError = 0.00002);

/// <summary>Keeps sampled animation buffers charged to their owning session through export.</summary>
public sealed class NativeModelAnimationFbxInputs : IDisposable
{
    private List<IDisposable>? reservations;
    private FbxSkeletonExportInput? skeleton;
    private FbxAnimationExportInput? animation;
    private NativeModelAnimationFbxInputs(FbxSkeletonExportInput skeleton, FbxAnimationExportInput animation,
        List<IDisposable> reservations, AssetMemoryBudget budget)
    { this.skeleton = skeleton; this.animation = animation; this.reservations = reservations; MemoryBudget = budget; }
    public AssetMemoryBudget MemoryBudget { get; }
    public FbxSkeletonExportInput Skeleton => skeleton ?? throw new ObjectDisposedException(nameof(NativeModelAnimationFbxInputs));
    public FbxAnimationExportInput Animation => animation ?? throw new ObjectDisposedException(nameof(NativeModelAnimationFbxInputs));
    public void Dispose()
    {
        var leases = Interlocked.Exchange(ref reservations, null);
        if (leases is null) return;
        skeleton = null; animation = null;
        for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
    }

    /// <summary>The mapping is the caller's qualified source-node ordinal to exported joint key join.
    /// Source owner motion is folded into every hierarchy root; bind geometry and bind matrices are retained.</summary>
    public static async Task<NativeModelAnimationFbxInputs> CreateAsync(NativeAnimationRigBinding binding,
        FbxSkeletonExportInput skeleton, IReadOnlyDictionary<int, string> jointKeysBySourceOrdinal,
        string clipName, AssetMemoryBudget budget, Func<CancellationToken, Task> validateClipSourceRevision,
        NativeAnimationFbxSamplingOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(jointKeysBySourceOrdinal); ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(validateClipSourceRevision); ArgumentException.ThrowIfNullOrWhiteSpace(clipName);
        token.ThrowIfCancellationRequested(); options ??= new();
        if (options.MaximumKeys is < 2 or > 2_000_000 || !double.IsFinite(options.MaximumSamplesPerSecond) ||
            options.MaximumSamplesPerSecond is < 30 or > 1920 ||
            !Positive(options.MaximumRotationErrorRadians) || !Positive(options.MaximumTranslationError) || !Positive(options.MaximumScaleError))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (binding.SkeletonIdentity != skeleton.Identity || binding.Revision != skeleton.Revision ||
            binding.ClipIdentity.IndexContentId != skeleton.Revision.IndexContentId ||
            binding.Joints.Count != skeleton.Joints.Count || jointKeysBySourceOrdinal.Count != binding.Joints.Count)
            throw new InvalidDataException("AnimationFbxSourceMismatch");
        using var mappingReservation = budget.Reserve(checked(binding.Joints.Count * 8192L + 8192), "FBX animation mapping and sampling scratch");
        var joints = skeleton.Joints.ToDictionary(joint => joint.Key, StringComparer.Ordinal);
        var keys = new string[binding.Joints.Count];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in binding.Joints)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)node.NodeOrdinal >= keys.Length || !jointKeysBySourceOrdinal.TryGetValue(node.NodeOrdinal, out var key) ||
                string.IsNullOrWhiteSpace(key) || !unique.Add(key) || !joints.TryGetValue(key, out var joint))
                throw new InvalidDataException("AnimationFbxJointMappingInvalid");
            if (node.ParentOrdinal is { } parent)
            {
                if (!jointKeysBySourceOrdinal.TryGetValue(parent, out var parentKey) || joint.ParentKey != parentKey)
                    throw new InvalidDataException("AnimationFbxHierarchyMismatch");
            }
            else if (joint.ParentKey is not null) throw new InvalidDataException("AnimationFbxHierarchyMismatch");
            keys[node.NodeOrdinal] = key;
        }
        var hierarchy = binding.Joints.ToDictionary(node => node.NodeOrdinal);
        foreach (var node in binding.Joints)
        {
            var seen = new HashSet<int>(); int? current = node.NodeOrdinal;
            while (current is { } ordinal)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(ordinal) || !hierarchy.TryGetValue(ordinal, out var ancestor))
                    throw new InvalidDataException("AnimationFbxHierarchyMismatch");
                current = ancestor.ParentOrdinal;
            }
        }
        var timing = binding.Timing;
        double duration = timing.Duration;
        if (!Positive(duration) || !float.IsFinite(timing.Period) || timing.Period <= 0 || timing.Count < 2)
            throw new InvalidDataException("AnimationFbxTimingInvalid");
        if (1d / timing.Period > options.MaximumSamplesPerSecond)
            throw new InvalidDataException("AnimationFbxSamplingRateLimit");
        int maximumFrames = options.MaximumKeys / keys.Length;
        if (maximumFrames < 2) throw new InvalidDataException("AnimationFbxKeyLimit");
        await skeleton.ValidateSourceRevision(token).ConfigureAwait(false);
        await validateClipSourceRevision(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var reservations = new List<IDisposable>();
        var frames = new SortedDictionary<double, SampledFrame>();
        try
        {
            double step = Math.Min(timing.Period, 1d / 30);
            long regularCount = checked((long)Math.Ceiling(duration / step));
            if (regularCount + 1 > maximumFrames) throw new InvalidDataException("AnimationFbxKeyLimit");
            using var timelineReservation = budget.Reserve(checked(Math.Min(maximumFrames, regularCount + timing.Count + 1) * 128L), "FBX animation sampling timeline");
            var times = new SortedSet<double> { 0, duration };
            // Include native frame boundaries, so a segment never hides a source interpolation kink.
            for (int i = 1; i < timing.Count; i++) { token.ThrowIfCancellationRequested(); double t = i * (double)timing.Period; AddTime(t); }
            for (long i = 1; i < regularCount; i++) { token.ThrowIfCancellationRequested(); double t = i * step; AddTime(t); }
            if (times.Count > maximumFrames) throw new InvalidDataException("AnimationFbxKeyLimit");
            SampledFrame? previous = null;
            foreach (var time in times)
            {
                var current = Retain(time);
                if (previous is not null) Refine(previous, current, 0);
                previous = current;
            }
            var tracks = new FbxJointAnimationTrack[keys.Length];
            for (int node = 0; node < keys.Length; node++)
            {
                token.ThrowIfCancellationRequested();
                tracks[node] = new(keys[node], Array.AsReadOnly(frames.Values.Select(frame =>
                    new FbxJointAnimationKey(frame.Time, frame.Transforms[node])).ToArray()));
            }
            await skeleton.ValidateSourceRevision(token).ConfigureAwait(false);
            await validateClipSourceRevision(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var animation = new FbxAnimationExportInput(binding.ClipIdentity, binding.Revision, clipName,
                skeleton.Identity, skeleton.Revision, duration, Array.AsReadOnly(tracks), async cancellation =>
                {
                    await skeleton.ValidateSourceRevision(cancellation).ConfigureAwait(false);
                    await validateClipSourceRevision(cancellation).ConfigureAwait(false);
                });
            return new(skeleton, animation, reservations, budget);

            void AddTime(double time)
            {
                if (time >= duration || times.Contains(time)) return;
                if (times.Count >= maximumFrames) throw new InvalidDataException("AnimationFbxKeyLimit");
                times.Add(time);
            }

            SampledFrame Retain(double time)
            {
                if (frames.TryGetValue(time, out var existing)) return existing;
                if (frames.Count >= maximumFrames) throw new InvalidDataException("AnimationFbxKeyLimit");
                var lease = budget.Reserve(checked(keys.Length * 512L + 256), "FBX animation sampled keys");
                try { var frame = Sample(time); frames.Add(time, frame); reservations.Add(lease); return frame; }
                catch { lease.Dispose(); throw; }
            }
            SampledFrame Sample(double time)
            {
                token.ThrowIfCancellationRequested();
                var locals = binding.LocalPose((float)time); var owner = binding.RootLocalAt((float)time);
                var transforms = new SourceTransform[keys.Length]; var values = new LocalTrs[keys.Length];
                if (locals.Length != keys.Length) throw new InvalidDataException("AnimationFbxJointMappingInvalid");
                foreach (var node in binding.Joints)
                {
                    token.ThrowIfCancellationRequested();
                    var matrix = locals[node.NodeOrdinal];
                    if (node.ParentOrdinal is null) matrix *= owner;
                    values[node.NodeOrdinal] = Decompose(matrix);
                    transforms[node.NodeOrdinal] = new(Array.AsReadOnly(new[] { matrix.M11,matrix.M12,matrix.M13,matrix.M14,
                        matrix.M21,matrix.M22,matrix.M23,matrix.M24,matrix.M31,matrix.M32,matrix.M33,matrix.M34,
                        matrix.M41,matrix.M42,matrix.M43,matrix.M44 }), MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
                }
                return new(time, transforms, values);
            }
            void Refine(SampledFrame first, SampledFrame last, int depth)
            {
                token.ThrowIfCancellationRequested(); bool matches = true;
                foreach (double fraction in new[] { .25, .5, .75 })
                {
                    var actual = Sample(first.Time + (last.Time - first.Time) * fraction);
                    for (int node = 0; node < keys.Length; node++)
                    {
                        var a = first.Values[node]; var b = last.Values[node]; var c = actual.Values[node];
                        var rotation = a.Euler + ShortDelta(a.Euler, b.Euler) * (float)fraction;
                        var predicted = EulerRotation(rotation);
                        if (RotationError(predicted, c.Rotation) > options.MaximumRotationErrorRadians ||
                            Vector3.Distance(Vector3.Lerp(a.Translation, b.Translation, (float)fraction), c.Translation) > options.MaximumTranslationError ||
                            Vector3.Distance(Vector3.Lerp(a.Scale, b.Scale, (float)fraction), c.Scale) > options.MaximumScaleError)
                        { matches = false; break; }
                    }
                    if (!matches) break;
                }
                if (matches) return;
                if (depth >= 16 || last.Time - first.Time <= 1d / options.MaximumSamplesPerSecond)
                    throw new InvalidDataException("AnimationFbxInterpolationTolerance");
                var middle = Retain((first.Time + last.Time) / 2);
                Refine(first, middle, depth + 1); Refine(middle, last, depth + 1);
            }
        }
        catch
        {
            for (var i = reservations.Count - 1; i >= 0; i--) reservations[i].Dispose();
            throw;
        }
    }
    private sealed record SampledFrame(double Time, SourceTransform[] Transforms, LocalTrs[] Values);
    private readonly record struct LocalTrs(Vector3 Translation, Vector3 Scale, Quaternion Rotation, Vector3 Euler);
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static LocalTrs Decompose(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation) ||
            !Finite(scale) || !Finite(translation) || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f)
            throw new InvalidDataException("AnimationFbxTransformInvalid");
        rotation = Quaternion.Normalize(rotation);
        var rebuilt = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        var a = Elements(matrix); var b = Elements(rebuilt);
        for (int i = 0; i < 16; i++)
            if (!float.IsFinite(a[i]) || Math.Abs(a[i] - b[i]) > 0.0001 * Math.Max(1, Math.Max(Math.Abs(a[i]), Math.Abs(b[i]))))
                throw new InvalidDataException("AnimationFbxTransformShear");
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        var y = MathF.Asin(Math.Clamp(-m.M13, -1f, 1f));
        float x, z;
        if (MathF.Abs(MathF.Cos(y)) > 1e-6f) { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = 0; z = MathF.Atan2(-m.M21, m.M22); }
        var euler = new Vector3(x, y, z) * (180f / MathF.PI);
        if (RotationError(EulerRotation(euler), rotation) > 0.0002) throw new InvalidDataException("AnimationFbxEulerInvalid");
        return new(translation, scale, rotation, euler);
    }
    private static float[] Elements(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector3 ShortDelta(Vector3 a, Vector3 b) => new(Wrap(b.X - a.X), Wrap(b.Y - a.Y), Wrap(b.Z - a.Z));
    private static float Wrap(float value) => (float)(value - 360d * Math.Round(value / 360d));
    private static Quaternion EulerRotation(Vector3 degrees)
    {
        var radians = degrees * (MathF.PI / 180f);
        return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationX(radians.X) * Matrix4x4.CreateRotationY(radians.Y) * Matrix4x4.CreateRotationZ(radians.Z));
    }
    private static double RotationError(Quaternion a, Quaternion b)
    {
        double dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
        double aa = (double)a.X*a.X + (double)a.Y*a.Y + (double)a.Z*a.Z + (double)a.W*a.W;
        double bb = (double)b.X*b.X + (double)b.Y*b.Y + (double)b.Z*b.Z + (double)b.W*b.W;
        return 2 * Math.Acos(Math.Clamp(Math.Abs(dot) / Math.Sqrt(aa * bb), 0, 1));
    }
}
