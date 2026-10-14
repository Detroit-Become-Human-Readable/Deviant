using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads embedded version14 arrays through their input callback sequence.</summary>
public sealed class NativeNodeArrayBodyReader
{
    public const string Version = "native-node-array-body-v14-1";

    public DecodeResult<NativeNodeArrayBodyDocument> Read(DecodeResult<NativeNodeArrayDocument> prefix,
        NativeNodeArrayBodyLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(prefix); token.ThrowIfCancellationRequested(); limits ??= new();
        ValidateLimits(limits);
        DecodeResult<NativeNodeArrayBodyDocument> MetadataFail(string code) => new(null, prefix.Identity, prefix.Revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (prefix.Extents is null || prefix.Steps is null) return MetadataFail("NodeArrayBodyMetadataInvalid");
        var extentCount = prefix.Extents.Count; var stepCount = prefix.Steps.Count;
        token.ThrowIfCancellationRequested();
        if (extentCount < 0 || extentCount > limits.MaximumPhysicalExtents || stepCount < 0 || stepCount > limits.MaximumPrecedingSteps)
            return MetadataFail("NodeArrayBodyMetadataLimit");
        var provenanceBytes = checked((extentCount + (long)stepCount) * 8 + 64);
        const long fixedValidationBytes = 4096;
        if (provenanceBytes + fixedValidationBytes > limits.MaximumMetadataBytes) return MetadataFail("NodeArrayBodyMemoryLimit");
        var physical = new SourceExtent[extentCount]; var steps = new DecodeStep[stepCount];
        for (var i = 0; i < physical.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var extent = prefix.Extents[i]; token.ThrowIfCancellationRequested();
            if (extent is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                extent.RelativeSource is null || extent.RelativeSource.Length > 4096 || string.IsNullOrWhiteSpace(extent.RelativeSource) ||
                !ValidHash(extent.PayloadSha256) ||
                extent.RelativeSource.Contains(':') || extent.RelativeSource.StartsWith('/') || extent.RelativeSource.StartsWith('\\') ||
                extent.RelativeSource.Split('/', '\\').Any(p => p is "" or "." or ".."))
                return MetadataFail("NodeArrayBodyPhysicalExtentInvalid");
            physical[i] = extent;
        }
        for (var i = 0; i < steps.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var step = prefix.Steps[i]; token.ThrowIfCancellationRequested();
            if (step is null || !ValidText(step.Decoder, false) || !ValidText(step.Version, false) ||
                !ValidText(step.Options, true) || !ValidHash(step.PayloadSha256)) return MetadataFail("NodeArrayBodyStepInvalid");
            steps[i] = step;
        }
        DecodeResult<NativeNodeArrayBodyDocument> Fail(string code) => new(null, prefix.Identity, prefix.Revision,
            physical, [.. steps, Step()], [code], [], new Dictionary<string, string>());
        if (prefix.Value is not { } owner) return Fail("NodeArrayPrefixUnavailable");
        if (!ValidOwner(prefix, owner, token)) return Fail("NodeArrayBodyPrefixMismatch");
        if (owner.BodyVersion != 14) return Fail("NodeArrayBodyVersionUnsupported");
        try
        {
            var r = new Cursor(owner, limits, token);
            r.Account(provenanceBytes + fixedValidationBytes);
            if (r.Word() != 14) return Fail("NodeArrayBodyVersionUnsupported");
            var primary = r.Count(limits.MaximumPrimaryNodes); var secondary = r.Count(limits.MaximumSecondaryRecords);
            r.Require(checked(primary * 72));
            r.Account(checked(primary * 320L + primary * 128L));
            var nodes = new NodeArrayBodyNode[primary]; long childrenTotal = 0;
            for (var i = 0; i < nodes.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var start = r.Position; var flags = r.Word();
                var components = r.Section("Components", 1, 40);
                var scalar1 = r.Word(); var parent = r.Word(); var childCount = r.Count(limits.MaximumChildReferences);
                childrenTotal = checked(childrenTotal + childCount);
                if (childrenTotal > limits.MaximumChildReferences) throw new InvalidDataException("NodeArrayBodyChildLimit");
                r.Require(checked(childCount * 4)); r.Account(checked(childCount * 4L + 24));
                var children = new uint[childCount];
                for (var j = 0; j < children.Length; j++) children[j] = r.Word();
                var scalar2 = r.Word();
                nodes[i] = new(i, flags, new(components.RawBytes, components.Extent), scalar1, parent,
                    Array.AsReadOnly(children), scalar2, r.Extent(start, r.Position - start));
            }
            var descriptors = new NodeArrayBodyDescriptor[primary];
            for (var i = 0; i < descriptors.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var at = r.Position;
                descriptors[i] = new(r.Word(), r.Half(), r.Half(), r.Word(), r.Extent(at, 12));
            }
            var optionalCount = r.Count(limits.MaximumOptionalBytes);
            var optional = r.Section("OptionalObject", optionalCount, 1);
            var compactCount = r.Count(limits.MaximumTableRecords); r.Require(checked(compactCount * 8));
            r.Account(checked(compactCount * 128L)); var compact = new NodeArrayCompactRecord[compactCount];
            for (var i = 0; i < compact.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var at = r.Position;
                compact[i] = new(r.Word(), r.Half(), r.Half(), r.Extent(at, 8));
            }
            var scalar340 = r.Word(); var unnamed = r.Byte(); var extraCount = r.Word();
            if (extraCount != 0) return Fail("NodeArrayBodyExtraObjectsUnsupported");
            var scalar348 = r.Word(); var scalar344 = r.Word(); var tableCount = r.Count(limits.MaximumTableRecords);
            NodeArrayRawSection[] tables = [r.Section("FiveWordRecords", tableCount, 20),
                r.Section("SecondaryWords", secondary, 4), r.Section("SecondaryFourWordRecords", secondary, 16),
                r.Section("SecondaryKeyIndexRecords", secondary, 8)];
            if (r.Position != owner.RawPayload.Length) return Fail("NodeArrayBodyEndpointMismatch");
            var diagnostics = CheckStructure(nodes, compact, limits, r, token);
            token.ThrowIfCancellationRequested();
            var hash = Hash(owner.EncodedBody.Span, token);
            r.Account(checked(diagnostics.Length * 32L + 1024));
            var codeSet = new HashSet<string>(StringComparer.Ordinal); var codes = new List<string>();
            foreach (var diagnostic in diagnostics)
            { token.ThrowIfCancellationRequested(); if (codeSet.Add(diagnostic.Code)) codes.Add(diagnostic.Code); }
            token.ThrowIfCancellationRequested(); var codeArray = codes.ToArray(); token.ThrowIfCancellationRequested();
            var value = new NativeNodeArrayBodyDocument(owner, Array.AsReadOnly(nodes), Array.AsReadOnly(descriptors),
                Array.AsReadOnly(compact), optional, scalar340, unnamed, scalar348, scalar344,
                Array.AsReadOnly(tables), Array.AsReadOnly(diagnostics), r.Accounted, owner.BodyExtent);
            return new(value, owner.Identity, owner.Revision, physical, [.. steps, Step(hash)],
                codeArray, [], new Dictionary<string, string>
                {
                    ["OptionalObject"] = $"{optional.Extent.Offset}:{optional.Extent.Length}",
                    ["RemainingTables"] = "Stored words; roles unassigned"
                });
        }
        catch (InvalidDataException ex) { return Fail(ex.Message); }
        catch (OverflowException) { return Fail("NodeArrayBodySizeOverflow"); }
    }

    private static bool ValidOwner(DecodeResult<NativeNodeArrayDocument> input, NativeNodeArrayDocument o, CancellationToken token)
    {
        if (o.Identity != input.Identity || o.Revision != input.Revision || o.Identity.Ordinal < 0 ||
            string.IsNullOrWhiteSpace(o.Identity.IndexContentId) || o.Identity.IndexContentId.Length > 4096 ||
            o.Revision.IndexContentId != o.Identity.IndexContentId || o.Identity.MemberPath is not { Length: <= 4096 } member ||
            o.InheritedNode is null || o.InheritedNode.Identity != o.Identity || o.InheritedNode.Revision != o.Revision ||
            o.Version != 4 || o.RawPayload.Length < 212 || o.RawPayload.Length > 64 * 1024 * 1024) return false;
        var pieces = member.Split('/');
        if (pieces.Length != 4 || pieces[0] != "dc-object" || pieces[2] != "2138" ||
            !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directoryIndex) || directoryIndex < 0 ||
            pieces[1] != directoryIndex.ToString(CultureInfo.InvariantCulture) ||
            pieces[3] != o.InheritedNode.NativeId.ToString(CultureInfo.InvariantCulture)) return false;
        var p = o.InheritedNode.PayloadExtent;
        if (p is null || p.Container is null || p.Container.MemberPath is not null || p.Container.IndexContentId != o.Identity.IndexContentId ||
            p.Container.Ordinal != o.Identity.Ordinal || p.Offset < 0 || p.Length != o.RawPayload.Length || p.Offset > long.MaxValue - p.Length ||
            !SameBytes(o.InheritedNode.RawPayload, o.RawPayload, token)) return false;
        if (o.FieldExtents is null || o.InheritedNode.FieldExtents is null) return false;
        var headerFields = o.FieldExtents.Count; token.ThrowIfCancellationRequested();
        var inheritedFields = o.InheritedNode.FieldExtents.Count; token.ThrowIfCancellationRequested();
        if (headerFields != 3 || inheritedFields != 10) return false;
        foreach (var (name, at, length) in new[] { ("ClassCount", 0, 4), ("Version", 204, 4), ("BodyVersion", 208, 4) })
        {
            var found = o.FieldExtents.TryGetValue(name, out var field); token.ThrowIfCancellationRequested();
            if (!found || field != new DecodedExtent(p.Container, p.Offset + at, length)) return false;
        }
        foreach (var (name, at, length) in new[] { ("Flags", 16, 4), ("Pivot", 20, 12), ("StoredLocalTransform", 32, 64),
            ("StoredWorldTransform", 96, 64), ("Parent", 160, 8), ("ParentAttachmentIndex", 168, 4), ("FirstChild", 172, 8),
            ("NextSibling", 180, 8), ("Word172", 188, 4), ("Word176", 192, 4) })
        {
            var found = o.InheritedNode.FieldExtents.TryGetValue(name, out var field); token.ThrowIfCancellationRequested();
            if (!found || field != new DecodedExtent(p.Container, p.Offset + at, length)) return false;
        }
        var b = o.RawPayload.Span;
        var envelope = BinaryPrimitives.ReadUInt32LittleEndian(b) == 2 && b.Slice(4, 8).SequenceEqual("NODE    "u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..]) == 5 && b.Slice(196, 8).SequenceEqual("NODEAR  "u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(b[204..]) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(b[208..]) == o.BodyVersion &&
            o.InheritedPrefixExtent == new DecodedExtent(p.Container, p.Offset, 196) &&
            o.ClassHeaderExtent == new DecodedExtent(p.Container, p.Offset + 196, 12) &&
            o.BodyExtent == new DecodedExtent(p.Container, p.Offset + 208, b.Length - 208);
        if (!envelope) return false;
        // Reuse the established body decoder with the actual derived identity/type; no ordinary-node admission is changed.
        var f = new NativeObjectFrame(o.Identity, directoryIndex, 2138, o.InheritedNode.NativeId,
            new(p.Container, 0, 0), new(p.Container, 0, 0), p, [], o.RawPayload, 0, 0, 0, 2);
        var expected = NodeRecordReader.ReadBody(f, o.Revision); var actual = o.InheritedNode;
        return actual.Flags == expected.Flags && SameFloat(actual.Pivot.X, expected.Pivot.X) &&
            SameFloat(actual.Pivot.Y, expected.Pivot.Y) && SameFloat(actual.Pivot.Z, expected.Pivot.Z) &&
            SameTransform(actual.StoredLocalTransform, expected.StoredLocalTransform, token) && SameTransform(actual.StoredWorldTransform, expected.StoredWorldTransform, token) &&
            actual.Parent == expected.Parent && actual.FirstChild == expected.FirstChild && actual.NextSibling == expected.NextSibling &&
            actual.ParentAttachmentIndex == expected.ParentAttachmentIndex && actual.Word172 == expected.Word172 && actual.Word176 == expected.Word176;
    }

    private static bool SameFloat(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);
    private static bool ValidText(string? text, bool allowEmpty) => text is not null && text.Length <= 4096 && (allowEmpty || !string.IsNullOrWhiteSpace(text));
    private static bool ValidHash(string? hash) => hash is null || hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static bool SameTransform(SourceTransform? a, SourceTransform b, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (a is null || a.Layout != b.Layout || a.VectorSide != b.VectorSide || a.Elements is null) return false;
        var count = a.Elements.Count; token.ThrowIfCancellationRequested(); if (count != 16) return false;
        for (var i = 0; i < 16; i++)
        { token.ThrowIfCancellationRequested(); var actual = a.Elements[i]; token.ThrowIfCancellationRequested(); if (!SameFloat(actual, b.Elements[i])) return false; }
        return true;
    }
    private static bool SameBytes(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b, CancellationToken token)
    {
        if (a.Equals(b)) return true; if (a.Length != b.Length) return false;
        for (var at = 0; at < a.Length; at += 64 * 1024)
        { token.ThrowIfCancellationRequested(); var n = Math.Min(64 * 1024, a.Length - at); if (!a.Span.Slice(at, n).SequenceEqual(b.Span.Slice(at, n))) return false; }
        token.ThrowIfCancellationRequested(); return true;
    }

    private static NodeArrayBodyDiagnostic[] CheckStructure(NodeArrayBodyNode[] nodes, NodeArrayCompactRecord[] compact,
        NativeNodeArrayBodyLimits limits, Cursor r, CancellationToken token)
    {
        r.Account(checked(nodes.Length * 20L + limits.MaximumDiagnostics * 128L));
        var diagnostics = new List<NodeArrayBodyDiagnostic>(); bool capped = false;
        void Add(string code, int? ordinal = null, uint? index = null)
        {
            if (capped) return;
            if (diagnostics.Count >= limits.MaximumDiagnostics - 1)
            { diagnostics.Add(new("NodeArrayBodyDiagnosticLimit")); capped = true; return; }
            diagnostics.Add(new(code, ordinal, index));
        }
        var parents = Enumerable.Repeat(-1, nodes.Length).ToArray();
        var storedParents = Enumerable.Repeat(-1, nodes.Length).ToArray(); var states = new byte[nodes.Length];
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node.StoredParent != uint.MaxValue && node.StoredParent > nodes.Length) Add("NodeArrayParentIndexInvalid", node.Ordinal, node.StoredParent);
            if (node.StoredParent < nodes.Length) storedParents[node.Ordinal] = (int)node.StoredParent;
            for (var i = 0; i < 10; i++) if (!float.IsFinite(node.Components.GetValue(i)))
            { Add("NodeArrayComponentsNonFinite", node.Ordinal); break; }
            foreach (var child in node.Children)
            {
                token.ThrowIfCancellationRequested();
                if (child >= nodes.Length) { Add("NodeArrayChildIndexInvalid", node.Ordinal, child); continue; }
                if (parents[child] >= 0) Add("NodeArrayChildDuplicate", node.Ordinal, child);
                else parents[child] = node.Ordinal;
                if (nodes[child].StoredParent != node.Ordinal) Add("NodeArrayParentChildMismatch", node.Ordinal, child);
            }
        }
        for (var i = 0; i < nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (storedParents[i] != parents[i]) Add("NodeArrayParentChildMismatch", i);
        }
        CheckCycles(parents, "NodeArrayIndexCycle"); Array.Clear(states);
        CheckCycles(storedParents, "NodeArrayStoredParentCycle");
        foreach (var item in compact)
        {
            token.ThrowIfCancellationRequested();
            if (item.Index16A > nodes.Length) Add("NodeArrayCompactIndexInvalid", index: item.Index16A);
        }
        return diagnostics.ToArray();
        void CheckCycles(int[] links, string code)
        {
            for (var start = 0; start < nodes.Length; start++)
            {
                token.ThrowIfCancellationRequested(); if (states[start] != 0) continue;
                var current = start;
                while (current >= 0 && states[current] == 0)
                { token.ThrowIfCancellationRequested(); states[current] = 1; current = links[current]; }
                if (current >= 0 && states[current] == 1) Add(code, current);
                current = start;
                while (current >= 0 && states[current] == 1)
                { token.ThrowIfCancellationRequested(); states[current] = 2; current = links[current]; }
            }
        }
    }

    private static void ValidateLimits(NativeNodeArrayBodyLimits l)
    {
        if (l.MaximumPrimaryNodes is < 0 or > 65_536 || l.MaximumSecondaryRecords is < 0 or > 65_536 ||
            l.MaximumChildReferences is < 0 or > 1_048_576 || l.MaximumTableRecords is < 0 or > 65_536 ||
            l.MaximumOptionalBytes is < 0 or > 64 * 1024 * 1024 || l.MaximumMetadataBytes is < 0 or > 256L * 1024 * 1024 ||
            l.MaximumDiagnostics is < 1 or > 65_536 || l.MaximumPhysicalExtents is < 0 or > 262_144 ||
            l.MaximumPrecedingSteps is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(l));
    }
    private static DecodeStep Step(string? hash = null) => new(nameof(NativeNodeArrayBodyReader), Version,
        "NODEAR-v4; body-v14; little-endian; borrowed-A1-owner", hash);
    private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (!bytes.IsEmpty) { token.ThrowIfCancellationRequested(); var n = Math.Min(bytes.Length, 64 * 1024); hash.AppendData(bytes[..n]); bytes = bytes[n..]; }
        token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private sealed class Cursor(NativeNodeArrayDocument owner, NativeNodeArrayBodyLimits limits, CancellationToken token)
    {
        public int Position { get; private set; } = 208;
        public long Accounted { get; private set; }
        public void Account(long bytes) { Accounted = checked(Accounted + bytes); if (Accounted > limits.MaximumMetadataBytes) throw new InvalidDataException("NodeArrayBodyMemoryLimit"); }
        public void Require(int n) { token.ThrowIfCancellationRequested(); if (n < 0 || n > owner.RawPayload.Length - Position) throw new InvalidDataException("NodeArrayBodyTruncated"); }
        public uint Word() { Require(4); var value = BinaryPrimitives.ReadUInt32LittleEndian(owner.RawPayload.Span[Position..]); Position += 4; return value; }
        public ushort Half() { Require(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(owner.RawPayload.Span[Position..]); Position += 2; return value; }
        public byte Byte() { Require(1); return owner.RawPayload.Span[Position++]; }
        public int Count(int cap) { var n = Word(); if (n > cap) throw new InvalidDataException("NodeArrayBodyCountLimit"); return (int)n; }
        public DecodedExtent Extent(int at, int n) => new(owner.InheritedNode.PayloadExtent.Container, checked(owner.InheritedNode.PayloadExtent.Offset + at), n);
        public NodeArrayRawSection Section(string name, int count, int stride) { var n = checked(count * stride); Require(n); var at = Position; Position += n; return new(name, count, stride, Extent(at, n), owner.RawPayload.Slice(at, n)); }
    }
}
