using System.Globalization;
using System.Numerics;
using System.Text;
using Deviant.Assets;

namespace Deviant.Export.Models;

public sealed partial class FbxGeometryExporter
{
    // FBX 7.4 KTime ticks per second (FBX SDK KTIME_ONE_SECOND).
    private const long AnimationTicksPerSecond = 46_186_158_000;
    private sealed record FbxAnimationCurve(long Id, string Channel, long[] Times, float[] Values);
    private sealed record FbxAnimationChannels(long Id, long JointId, string Property, FbxAnimationCurve[] Curves);
    private sealed record FbxAnimationPlan(long StackId, long LayerId, FbxAnimationExportInput Source,
        long Stop, List<FbxAnimationChannels> Channels);

    private static long EstimateAnimationPlanningBytes(FbxAnimationExportInput? animation,
        FbxSkeletonExportInput? skeleton, FbxGeometryExportOptions options, CancellationToken token)
    {
        if (animation is null) return 0;
        ArgumentNullException.ThrowIfNull(animation.Identity);
        ArgumentNullException.ThrowIfNull(animation.Revision);
        ArgumentNullException.ThrowIfNull(animation.Tracks);
        ArgumentNullException.ThrowIfNull(animation.ValidateSourceRevision);
        if (skeleton is null || animation.SkeletonIdentity != skeleton.Identity || animation.SkeletonRevision != skeleton.Revision ||
            animation.Identity.IndexContentId != animation.Revision.IndexContentId ||
            animation.Revision != skeleton.Revision)
            throw AnimationError("AnimationIdentityMismatch", "Clip and skeleton require matching source revisions and a qualified skeleton identity.");
        if (string.IsNullOrWhiteSpace(animation.Name) || animation.Name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(animation.Name) > 1024)
            throw AnimationError("AnimationNameInvalid", "Clip names require printable text within 1024 UTF-8 bytes.");
        var stop = AnimationTime(animation.DurationSeconds);
        if (stop <= 0 || animation.Tracks.Count == 0 || animation.Tracks.Count > skeleton.Joints.Count)
            throw AnimationError("AnimationTracksInvalid", "Animation requires a positive duration and bounded joint tracks.");
        long keys = 0;
        foreach (var track in animation.Tracks)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(track.Keys);
            keys = checked(keys + track.Keys.Count);
            if (keys > options.MaximumAnimationKeys)
                throw AnimationError("AnimationKeyLimit", "Animation samples exceed the configured key limit.");
        }
        // Nine channels, time/value snapshots and writer AST metadata; caller buffers remain owned by caller.
        return checked(keys * 256L + animation.Tracks.Count * 16_384L + 8192);
    }

    private static FbxPlan AddAnimationPlan(FbxPlan plan, FbxAnimationExportInput? animation,
        FbxSkeletonExportInput? skeleton, GeometryAsset asset, CancellationToken token)
    {
        if (animation is null) return plan;
        var limbs = plan.Models.Where(model => model.Type == "LimbNode").ToArray();
        var joints = skeleton!.Joints;
        if (limbs.Length != joints.Count) throw AnimationError("AnimationTracksInvalid", "Exported joint targets are incomplete.");
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < joints.Count; i++) ids.Add(joints[i].Key, limbs[i].Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextId = plan.NextId;
        var stackId = nextId++; var layerId = nextId++;
        var channels = new List<FbxAnimationChannels>();
        var stop = AnimationTime(animation.DurationSeconds);
        foreach (var track in animation.Tracks)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(track.JointKey) || !seen.Add(track.JointKey) || !ids.TryGetValue(track.JointKey, out var jointId))
                throw AnimationError("AnimationJointInvalid", "Each animated joint must occur once and exist in the qualified skeleton.");
            if (track.Keys.Count < 2) throw AnimationError("AnimationTimeInvalid", "Joint tracks require first and last samples.");
            var times = new long[track.Keys.Count];
            var values = Enumerable.Range(0, 9).Select(_ => new float[track.Keys.Count]).ToArray();
            double previousSeconds = -1;
            for (var i = 0; i < track.Keys.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var key = track.Keys[i];
                ArgumentNullException.ThrowIfNull(key);
                var time = AnimationTime(key.TimeSeconds);
                if (key.TimeSeconds <= previousSeconds || key.TimeSeconds > animation.DurationSeconds ||
                    (i > 0 && time <= times[i - 1]))
                    throw AnimationError("AnimationTimeInvalid", "Joint times must increase strictly within the clip and remain distinct in FBX ticks.");
                previousSeconds = key.TimeSeconds;
                times[i] = time;
                ArgumentNullException.ThrowIfNull(key.LocalTransform);
                var trs = ConvertTransform(key.LocalTransform, asset.Coordinates, track.JointKey);
                var rotation = trs.EulerDegrees;
                if (i > 0) rotation = new(Unwrap(rotation.X, values[3][i - 1]), Unwrap(rotation.Y, values[4][i - 1]), Unwrap(rotation.Z, values[5][i - 1]));
                values[0][i] = trs.Translation.X; values[1][i] = trs.Translation.Y; values[2][i] = trs.Translation.Z;
                values[3][i] = rotation.X; values[4][i] = rotation.Y; values[5][i] = rotation.Z;
                values[6][i] = trs.Scale.X; values[7][i] = trs.Scale.Y; values[8][i] = trs.Scale.Z;
            }
            if (track.Keys[0].TimeSeconds != 0 || track.Keys[^1].TimeSeconds != animation.DurationSeconds)
                throw AnimationError("AnimationTimeInvalid", "Every sampled joint track must cover exactly zero through the declared duration.");
            string[] properties = ["Lcl Translation", "Lcl Rotation", "Lcl Scaling"];
            string[] axis = ["X", "Y", "Z"];
            for (var group = 0; group < 3; group++)
            {
                var nodeId = nextId++;
                var curves = new FbxAnimationCurve[3];
                for (var lane = 0; lane < 3; lane++) curves[lane] = new(nextId++, axis[lane], times, values[group * 3 + lane]);
                channels.Add(new(nodeId, jointId, properties[group], curves));
            }
        }
        return plan with { NextId = nextId, Animation = new(stackId, layerId, animation, stop, channels) };
    }

    private static float Unwrap(float value, float previous)
    {
        var result = value + 360d * Math.Round((previous - (double)value) / 360d);
        if (!double.IsFinite(result) || result > float.MaxValue || result < -float.MaxValue)
            throw AnimationError("AnimationValueInvalid", "Unwrapped rotation exceeds the finite channel range.");
        return (float)result;
    }

    private static long AnimationTime(double seconds)
    {
        var ticks = seconds * AnimationTicksPerSecond;
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(ticks) || ticks >= long.MaxValue)
            throw AnimationError("AnimationTimeInvalid", "Animation seconds must be finite, nonnegative and representable by FBX KTime.");
        return checked((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
    }

    private static FbxGeometryExportException AnimationError(string code, string message) =>
        Error(code, message, [new(code, "animation", message)]);
    private static int AnimationObjectCount(FbxAnimationPlan? animation) => animation is null ? 0 : checked(2 + animation.Channels.Count * 4);
    private static FbxBinary7400Node PTime(string name, long ticks) => new("P",
        [FbxBinary7400Property.String(name), FbxBinary7400Property.String("KTime"), FbxBinary7400Property.String("Time"),
         FbxBinary7400Property.String(""), FbxBinary7400Property.Int64(ticks)]);
    private static void AddBinaryAnimationSettings(List<FbxBinary7400Node> properties, FbxAnimationPlan? animation)
    {
        if (animation is null) return;
        properties.Add(PInt("TimeMode", "enum", "", 6)); // 30 fps display; key times retain KTime precision.
        properties.Add(PTime("TimeSpanStart", 0)); properties.Add(PTime("TimeSpanStop", animation.Stop));
    }
    private static void AddAnimationDefinitions(List<FbxBinary7400Node> definitions, FbxAnimationPlan? animation)
    {
        if (animation is null) return;
        AddDefinition(definitions, "AnimationStack", 1); AddDefinition(definitions, "AnimationLayer", 1);
        AddDefinition(definitions, "AnimationCurveNode", animation.Channels.Count);
        AddDefinition(definitions, "AnimationCurve", animation.Channels.Count * 3);
    }
    private static FbxBinary7400Property AnimationArray<T>(IReadOnlyList<T> values, byte type, uint elementBytes,
        Action<BinaryWriter, T> write) => FbxBinary7400Property.Array(type, checked((uint)values.Count), elementBytes, (writer, token) =>
        {
            for (var i = 0; i < values.Count; i++) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); write(writer, values[i]); }
        });
    private static IEnumerable<FbxBinary7400Node> BuildBinaryAnimation(FbxAnimationPlan? animation, CancellationToken token)
    {
        if (animation is null) yield break;
        yield return new("AnimationStack", [FbxBinary7400Property.Int64(animation.StackId),
            FbxBinary7400Property.String($"{animation.Source.Name}\0\u0001AnimStack"), FbxBinary7400Property.String("")],
            [new("Properties70", children: [PTime("LocalStart", 0), PTime("LocalStop", animation.Stop), PTime("ReferenceStart", 0), PTime("ReferenceStop", animation.Stop),
                PString("DeviantAnimationIdentity", IdentityText(animation.Source.Identity)), PString("DeviantAnimationRevision", RevisionText(animation.Source.Revision)),
                PString("DeviantAnimationSkeleton", IdentityText(animation.Source.SkeletonIdentity))])]);
        yield return new("AnimationLayer", [FbxBinary7400Property.Int64(animation.LayerId), FbxBinary7400Property.String("BaseLayer\0\u0001AnimLayer"), FbxBinary7400Property.String("")],
            [new("Properties70", children: [PDouble("Weight", "Number", "", 100), PInt("BlendMode", "enum", "", 1)])]);
        foreach (var channels in animation.Channels)
        {
            token.ThrowIfCancellationRequested();
            yield return new("AnimationCurveNode", [FbxBinary7400Property.Int64(channels.Id), FbxBinary7400Property.String($"{ChannelName(channels.Property)}\0\u0001AnimCurveNode"), FbxBinary7400Property.String("")],
                [new("Properties70", children: channels.Curves.Select(curve => PDouble("d|" + curve.Channel, "Number", "", curve.Values[0])).ToArray())]);
            foreach (var curve in channels.Curves)
                yield return new("AnimationCurve", [FbxBinary7400Property.Int64(curve.Id), FbxBinary7400Property.String($"{curve.Id}\0\u0001AnimCurve"), FbxBinary7400Property.String("")],
                [new("Default", [FbxBinary7400Property.Double(curve.Values[0])]), new("KeyVer", [FbxBinary7400Property.Int32(4008)]),
                 new("KeyTime", [AnimationArray(curve.Times, (byte)'l', 8, (writer, value) => writer.Write(value))]),
                 new("KeyValueFloat", [AnimationArray(curve.Values, (byte)'f', 4, (writer, value) => writer.Write(value))]),
                 new("KeyAttrFlags", [AnimationArray(new[] { 4 }, (byte)'i', 4, (writer, value) => writer.Write(value))]),
                 new("KeyAttrDataFloat", [AnimationArray(new float[4], (byte)'f', 4, (writer, value) => writer.Write(value))]),
                 new("KeyAttrRefCount", [AnimationArray(new[] { curve.Times.Length }, (byte)'i', 4, (writer, value) => writer.Write(value))])]);
        }
    }
    private static string ChannelName(string property) => property switch { "Lcl Translation" => "T", "Lcl Rotation" => "R", _ => "S" };
    private static IEnumerable<(string Type, long Child, long Parent, string? Property)> AnimationConnections(FbxAnimationPlan? animation)
    {
        if (animation is null) yield break;
        yield return ("OO", animation.LayerId, animation.StackId, null);
        foreach (var channels in animation.Channels)
        {
            yield return ("OO", channels.Id, animation.LayerId, null);
            yield return ("OP", channels.Id, channels.JointId, channels.Property);
            foreach (var curve in channels.Curves) yield return ("OP", curve.Id, channels.Id, "d|" + curve.Channel);
        }
    }
    private static IEnumerable<FbxBinary7400Node> BuildBinaryAnimationConnections(FbxAnimationPlan? animation) =>
        AnimationConnections(animation).Select(connection => new FbxBinary7400Node("C", connection.Property is null
            ? [FbxBinary7400Property.String(connection.Type), FbxBinary7400Property.Int64(connection.Child), FbxBinary7400Property.Int64(connection.Parent)]
            : [FbxBinary7400Property.String(connection.Type), FbxBinary7400Property.Int64(connection.Child), FbxBinary7400Property.Int64(connection.Parent), FbxBinary7400Property.String(connection.Property)]));
    private static FbxBinary7400Node BuildBinaryTakes(FbxAnimationPlan? animation) => animation is null
        ? new("Takes", children: [new("Current", [FbxBinary7400Property.String("")])])
        : new("Takes", children: [new("Current", [FbxBinary7400Property.String(animation.Source.Name)]),
            new("Take", [FbxBinary7400Property.String(animation.Source.Name)], [new("FileName", [FbxBinary7400Property.String("")]),
                new("LocalTime", [FbxBinary7400Property.Int64(0), FbxBinary7400Property.Int64(animation.Stop)]),
                new("ReferenceTime", [FbxBinary7400Property.Int64(0), FbxBinary7400Property.Int64(animation.Stop)])])]);

    private static void WriteAnimationSettings(TextWriter writer, FbxAnimationPlan? animation)
    {
        if (animation is null) return;
        WriteScalarProperty(writer, "TimeMode", "enum", "", "6");
        WriteScalarProperty(writer, "TimeSpanStart", "KTime", "Time", "0");
        WriteScalarProperty(writer, "TimeSpanStop", "KTime", "Time", animation.Stop.ToString(CultureInfo.InvariantCulture));
    }
    private static void WriteAnimationDefinitions(TextWriter writer, FbxAnimationPlan? animation)
    {
        if (animation is null) return;
        WriteDefinition(writer, "AnimationStack", 1); WriteDefinition(writer, "AnimationLayer", 1);
        WriteDefinition(writer, "AnimationCurveNode", animation.Channels.Count); WriteDefinition(writer, "AnimationCurve", animation.Channels.Count * 3);
    }
    private static void WriteAnimation(TextWriter writer, FbxAnimationPlan? animation, CancellationToken token)
    {
        if (animation is null) return;
        writer.WriteLine($"\tAnimationStack: {animation.StackId}, \"AnimStack::{Escape(animation.Source.Name)}\", \"\" {{ Properties70: {{");
        foreach (var property in new[] { "LocalStart", "ReferenceStart" }) WriteScalarProperty(writer, property, "KTime", "Time", "0");
        foreach (var property in new[] { "LocalStop", "ReferenceStop" }) WriteScalarProperty(writer, property, "KTime", "Time", animation.Stop.ToString(CultureInfo.InvariantCulture));
        WriteProperty(writer, "DeviantAnimationIdentity", "KString", "", IdentityText(animation.Source.Identity));
        WriteProperty(writer, "DeviantAnimationRevision", "KString", "", RevisionText(animation.Source.Revision));
        WriteProperty(writer, "DeviantAnimationSkeleton", "KString", "", IdentityText(animation.Source.SkeletonIdentity));
        writer.WriteLine("} }");
        writer.WriteLine($"\tAnimationLayer: {animation.LayerId}, \"AnimLayer::BaseLayer\", \"\" {{ Properties70: {{");
        WriteScalarProperty(writer, "Weight", "Number", "", "100"); WriteScalarProperty(writer, "BlendMode", "enum", "", "1"); writer.WriteLine("} }");
        foreach (var channels in animation.Channels)
        {
            token.ThrowIfCancellationRequested();
            writer.WriteLine($"\tAnimationCurveNode: {channels.Id}, \"AnimCurveNode::{ChannelName(channels.Property)}\", \"\" {{ Properties70: {{");
            foreach (var curve in channels.Curves) WriteScalarProperty(writer, "d|" + curve.Channel, "Number", "", Format(curve.Values[0]));
            writer.WriteLine("} }");
            foreach (var curve in channels.Curves)
            {
                writer.WriteLine($"\tAnimationCurve: {curve.Id}, \"AnimCurve::{curve.Id}\", \"\" {{ Default: {Format(curve.Values[0])} KeyVer: 4008");
                writer.WriteLine($"\t\tKeyTime: *{curve.Times.Length} {{ a: ");
                WriteValues(writer, curve.Times.Length, i => curve.Times[i].ToString(CultureInfo.InvariantCulture), token); writer.WriteLine(" }");
                writer.WriteLine($"\t\tKeyValueFloat: *{curve.Values.Length} {{ a: ");
                WriteValues(writer, curve.Values.Length, i => Format(curve.Values[i]), token); writer.WriteLine(" }");
                writer.WriteLine($"\t\tKeyAttrFlags: *1 {{ a: 4 }} KeyAttrDataFloat: *4 {{ a: 0,0,0,0 }} KeyAttrRefCount: *1 {{ a: {curve.Times.Length} }}");
                writer.WriteLine("\t}");
            }
        }
    }
    private static void WriteAnimationConnections(TextWriter writer, FbxAnimationPlan? animation)
    {
        foreach (var (type, child, parent, property) in AnimationConnections(animation))
            writer.WriteLine($"\tC: \"{type}\",{child},{parent}" + (property is null ? "" : $",\"{Escape(property)}\""));
    }
    private static void WriteTakes(TextWriter writer, FbxAnimationPlan? animation)
    {
        if (animation is null) { writer.WriteLine("Takes: { Current: \"\" }"); return; }
        writer.WriteLine($"Takes: {{ Current: \"{Escape(animation.Source.Name)}\" Take: \"{Escape(animation.Source.Name)}\" {{ FileName: \"\" LocalTime: 0,{animation.Stop} ReferenceTime: 0,{animation.Stop} }} }}");
    }
}
