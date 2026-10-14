using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

/// <summary>Expands the NODEAR v14 reference rows and applies its keyed ground adjustment.</summary>
public sealed class NativeAnimationReferencePose
{
    private const uint HeightKey = 0x47D23906;
    private const uint GroundKeyA = 1252226046;
    private const uint GroundKeyB = 2101515373;

    private NativeAnimationReferencePose(NativeNodeArrayBodyDocument body, AnimationTransformSample[] references,
        uint[] parents, float height, float groundOffset, int heightNode, bool hasHeight)
    {
        Identity = body.Identity; Revision = body.Revision;
        References = Array.AsReadOnly(references); Parents = Array.AsReadOnly(parents);
        Height = height; GroundOffset = groundOffset; HeightNode = heightNode; HasHeight = hasHeight;
    }

    public ResourceIdentity Identity { get; }
    public SourceRevision Revision { get; }
    public IReadOnlyList<AnimationTransformSample> References { get; }
    /// <summary>Immediate parents after the native reader assignments; Count is the root sentinel.</summary>
    public IReadOnlyList<uint> Parents { get; }
    public float Height { get; }
    public float GroundOffset { get; }
    public int HeightNode { get; }
    public bool HasHeight { get; }
    public long AccountedMetadataBytes => checked(References.Count * 100L + 4096);

    /// <param name="qualifiedGlobals">Source-qualified NODEAR global reference matrices, in primary-node order.</param>
    /// <param name="ownerGlobal">Source-qualified owner global matrix used by metadata indices outside the primary array.</param>
    /// <remarks>Reserve Count*100+4096 bytes from the owning session before calling and retain that reservation with this result.</remarks>
    public static NativeAnimationReferencePose Create(NativeNodeArrayBodyDocument body,
        IReadOnlyList<Matrix4x4> qualifiedGlobals, Matrix4x4? ownerGlobal = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(qualifiedGlobals);
        token.ThrowIfCancellationRequested();
        int count = body.Nodes.Count;
        var container = body.Extent.Container;
        if (body.BodyVersion != 14 || body.Identity.IndexContentId != body.Revision.IndexContentId ||
            container.IndexContentId != body.Identity.IndexContentId || container.Ordinal != body.Identity.Ordinal ||
            count is < 1 or > 65_536 || body.Descriptors.Count != count || body.RemainingTables.Count > 16 ||
            qualifiedGlobals.Count != count)
            throw new InvalidDataException("AnimationReferenceMetadataInvalid");
        var references = new AnimationTransformSample[count]; var parents = new uint[count];
        int childReferences = 0;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = body.Nodes[i];
            if (node.Ordinal != i || node.Components.RawBytes.Length != 40 ||
                node.Components.Extent.Container != container || node.Components.Extent.Length != 40 || !Finite(qualifiedGlobals[i]) ||
                node.StoredParent != uint.MaxValue && node.StoredParent > count ||
                node.Children.Count > 262_144 - childReferences)
                throw new InvalidDataException("AnimationReferenceMetadataInvalid");
            childReferences += node.Children.Count;
            var q = node.Components.NativeFirstBlock;
            var t = new Vector4(node.Components.VectorAt16, 0);
            var s = new Vector4(node.Components.VectorAt32, 1);
            if (!Finite(q) || !Finite(t) || !Finite(s)) throw new InvalidDataException("AnimationReferenceNonfinite");
            references[i] = new(q, t, s);
            parents[i] = node.StoredParent == uint.MaxValue ? (uint)count : node.StoredParent;
            foreach (uint child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                if (child >= count) throw new InvalidDataException("AnimationReferenceHierarchyInvalid");
                parents[child] = (uint)i;
            }
        }
        if (ownerGlobal is { } owner && !Finite(owner)) throw new InvalidDataException("AnimationReferenceNonfinite");
        var sections = body.RemainingTables.Where(section => section.Name == "FiveWordRecords").ToArray();
        if (sections.Length != 1 || sections[0].StoredStride != 20 || sections[0].Count is < 0 or > 65_536 ||
            sections[0].RawBytes.Length != (long)sections[0].Count * 20 || sections[0].Extent.Container != container ||
            sections[0].Extent.Length != sections[0].RawBytes.Length)
            throw new InvalidDataException("AnimationReferenceMetadataInvalid");
        var section = sections[0]; int firstA = -1, firstB = -1;
        for (int i = 0; i < section.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var row = section.RawBytes.Span.Slice(i * 20, 20);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(row);
            if (key == GroundKeyA && firstA < 0) firstA = i;
            if (key == GroundKeyB && firstB < 0) firstB = i;
        }
        int Find(uint key)
        {
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (body.Descriptors[i].Key == key) return i;
            }
            return -1;
        }
        int Toe(string toe, string fallback)
        {
            int index = Find(NameKey(toe)); return index < 0 ? Find(NameKey(fallback)) : index;
        }
        int heightNode = Find(HeightKey);
        bool hasHeight = heightNode >= 0 && Find(NameKey("L_Foot")) >= 0 && Toe("L_Toe", "L_ToeBase") >= 0 &&
            Find(NameKey("R_Foot")) >= 0 && Toe("R_Toe", "R_ToeBase") >= 0 && (firstA >= 0 || firstB >= 0);
        float offset = 0;
        if (hasHeight)
        {
            float PointY(int rowIndex)
            {
                token.ThrowIfCancellationRequested();
                var row = section.RawBytes.Span.Slice(rowIndex * 20, 20);
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                var point = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(row[8..]),
                    BinaryPrimitives.ReadSingleLittleEndian(row[12..]), BinaryPrimitives.ReadSingleLittleEndian(row[16..]));
                if (!Finite(new Vector4(point, 0))) throw new InvalidDataException("AnimationReferenceNonfinite");
                Matrix4x4 matrix = index < count ? qualifiedGlobals[(int)index] :
                    ownerGlobal ?? throw new InvalidDataException("AnimationReferenceOwnerGlobalUnavailable");
                // Match the native add grouping: ((x*row0 + y*row1) + z*row2) + row3.
                float y = ((point.X * matrix.M12 + point.Y * matrix.M22) + point.Z * matrix.M32) + matrix.M42;
                if (!float.IsFinite(y)) throw new InvalidDataException("AnimationReferenceNonfinite");
                return y;
            }
            float shift = firstA >= 0 ? PointY(firstA) : PointY(firstB);
            if (firstA >= 0 && firstB >= 0) shift = MathF.Min(shift, PointY(firstB));
            if (MathF.Abs(shift) >= 1e-5f)
            {
                offset = shift; uint group = parents[heightNode];
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (parents[i] != group) continue;
                    var t = references[i].Translation; t.Y -= shift;
                    if (!Finite(t)) throw new InvalidDataException("AnimationReferenceNonfinite");
                    references[i] = references[i] with { Translation = t };
                }
            }
        }
        float height = hasHeight ? MathF.Max(0, references[heightNode].Translation.Y) : 0;
        token.ThrowIfCancellationRequested();
        return new(body, references, parents, height, offset, heightNode, hasHeight);
    }

    private static uint NameKey(string name)
    {
        uint crc = uint.MaxValue;
        foreach (char value in name)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
    private static bool Finite(Matrix4x4 m) => Finite(new Vector4(m.M11, m.M12, m.M13, m.M14)) &&
        Finite(new Vector4(m.M21, m.M22, m.M23, m.M24)) && Finite(new Vector4(m.M31, m.M32, m.M33, m.M34)) &&
        Finite(new Vector4(m.M41, m.M42, m.M43, m.M44));
}
