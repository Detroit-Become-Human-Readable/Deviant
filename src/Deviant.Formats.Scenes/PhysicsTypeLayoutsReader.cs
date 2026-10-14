using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads layout metadata and links within one embedded TYPE table.</summary>
public sealed class PhysicsTypeLayoutsReader
{
    public const string Version = "physics-type-layouts-v1";

    /// <summary>Input metadata must remain available and unchanged during this synchronous read.</summary>
    public DecodeResult<PhysicsTypeLayoutsDocument> Read(PhysicsTaggedMetadataDocument metadata, PhysicsTypeDeclarationsDocument declarations,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        PhysicsTypeLayoutLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(declarations); ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested(); limits ??= new(); ValidateLimits(limits);
        DecodeResult<PhysicsTypeLayoutsDocument> Early(string code) => new(null, metadata.Identity, metadata.Revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (!ValidIdentity(metadata)) return Early("PhysicsLayoutSourceMismatch");
        var physicalCount = physicalExtents.Count; token.ThrowIfCancellationRequested();
        var stepCount = precedingSteps?.Count ?? 0; token.ThrowIfCancellationRequested();
        var chunkCount = metadata.Chunks.Count; token.ThrowIfCancellationRequested();
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents || stepCount < 0 || stepCount > limits.MaximumPrecedingSteps ||
            chunkCount < 0 || chunkCount > limits.MaximumChunks) return Early("PhysicsLayoutContextLimit");
        var budget = new Budget(limits.MaximumMetadataBytes);
        if (!budget.Reserve(4096L + 8L * (physicalCount + stepCount) + 384L * chunkCount +
            TextBytes(metadata.Identity.IndexContentId) + TextBytes(metadata.Identity.MemberPath) +
            TextBytes(metadata.Revision.PhysicalSourceStamp) + TextBytes(metadata.EmbeddedSha256))) return Early("PhysicsLayoutContextLimit");
        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physicalCount; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = physicalExtents[index]; token.ThrowIfCancellationRequested();
            if (extent is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                !ValidSourceName(extent.RelativeSource) || !OptionalHash(extent.PayloadSha256)) return Early("PhysicsLayoutPhysicalExtentInvalid");
            if (!budget.Reserve(128 + TextBytes(extent.RelativeSource) + TextBytes(extent.PayloadSha256))) return Early("PhysicsLayoutContextLimit");
            physical[index] = extent;
        }
        var steps = new DecodeStep[stepCount + 1];
        for (var index = 0; index < stepCount; index++)
        {
            token.ThrowIfCancellationRequested(); var step = precedingSteps![index]; token.ThrowIfCancellationRequested();
            if (step is null || !ValidText(step.Decoder) || !ValidText(step.Version) || !ValidText(step.Options, true) || !OptionalHash(step.PayloadSha256))
                return Early("PhysicsLayoutContextInvalid");
            if (!budget.Reserve(128 + TextBytes(step.Decoder) + TextBytes(step.Version) + TextBytes(step.Options) + TextBytes(step.PayloadSha256)))
                return Early("PhysicsLayoutContextLimit");
            steps[index] = step;
        }
        steps[^1] = Step();
        var chunks = new Snapshot[chunkCount];
        var previousChildEnds = new long[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            token.ThrowIfCancellationRequested(); var chunk = metadata.Chunks[index]; token.ThrowIfCancellationRequested();
            if (chunk is null || chunk.RawHeader is not { Length: 8 }) return Early("PhysicsLayoutChunkInvalid");
            var header = chunk.RawHeader.ToArray(); token.ThrowIfCancellationRequested();
            var word = BinaryPrimitives.ReadUInt32BigEndian(header); var tag = Encoding.ASCII.GetString(header, 4, 4);
            if (chunk.Ordinal != index || chunk.RawBigEndianWord != word || chunk.RawFlags != word >> 30 || chunk.DeclaredLength != (word & 0x3fffffff) ||
                chunk.DeclaredLength < 8 || chunk.Tag != tag || chunk.Depth < 0 || chunk.Depth > limits.MaximumDepth ||
                !ValidExtent(chunk.HeaderExtent, metadata.EmbeddedExtent) || !ValidExtent(chunk.BodyExtent, metadata.EmbeddedExtent) ||
                chunk.HeaderExtent.Length != 8 || chunk.BodyExtent.Offset != chunk.HeaderExtent.Offset + 8 || chunk.BodyExtent.Length != chunk.DeclaredLength - 8)
                return Early("PhysicsLayoutChunkInvalid");
            if (index == 0)
            {
                if (tag != "TAG0" || chunk.RawFlags != 0 || chunk.ParentOrdinal is not null || chunk.Depth != 0 ||
                    chunk.HeaderExtent.Offset != metadata.EmbeddedExtent.Offset || chunk.DeclaredLength != metadata.EmbeddedExtent.Length)
                    return Early("PhysicsLayoutProfileUnsupported");
            }
            else
            {
                if (chunk.ParentOrdinal is not { } parent || parent < 0 || parent >= index || chunks[parent].Flags != 0 ||
                    chunk.Depth != chunks[parent].Depth + 1 || !Inside(chunk.HeaderExtent.Offset, chunk.DeclaredLength, chunks[parent].BodyExtent) ||
                    chunk.HeaderExtent.Offset < previousChildEnds[parent]) return Early("PhysicsLayoutChunkInvalid");
                previousChildEnds[parent] = chunk.HeaderExtent.Offset + chunk.DeclaredLength;
            }
            byte[]? body = null; string? bodyHash = null;
            if (tag is "FSTR" or "TBDY")
            {
                if (chunk.RawFlags != 1 || chunk.MetadataBody is not { } borrowed || borrowed.Length != chunk.BodyExtent.Length || !HashShape(chunk.MetadataBodySha256))
                    return Early("PhysicsLayoutLeafInvalid");
                if (borrowed.Length > limits.MaximumLeafBytes || !budget.Reserve(512L + borrowed.Length)) return Early("PhysicsLayoutLeafLimit");
                body = borrowed.ToArray(); token.ThrowIfCancellationRequested(); bodyHash = Hash(body, token);
                if (!string.Equals(bodyHash, chunk.MetadataBodySha256, StringComparison.OrdinalIgnoreCase)) return Early("PhysicsLayoutLeafHashMismatch");
            }
            else if (tag is "TSTR" or "TNA1")
            {
                if (!HashShape(chunk.MetadataBodySha256) || chunk.MetadataBody is not { } inputBody || inputBody.Length != chunk.BodyExtent.Length ||
                    inputBody.Length > limits.MaximumLeafBytes) return Early("PhysicsLayoutDeclarationContextInvalid");
                bodyHash = chunk.MetadataBodySha256;
            }
            chunks[index] = new(index, chunk.ParentOrdinal, chunk.Depth, tag, (byte)(word >> 30), word, chunk.HeaderExtent, chunk.BodyExtent, header, body, bodyHash);
        }
        token.ThrowIfCancellationRequested();
        Snapshot? type = null, fstr = null, tbdy = null;
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.Tag == "TYPE") { if (type is not null) return Early("PhysicsLayoutProfileUnsupported"); type = chunk; }
            if (chunk.Tag == "FSTR") { if (fstr is not null) return Early("PhysicsLayoutProfileUnsupported"); fstr = chunk; }
            if (chunk.Tag == "TBDY") { if (tbdy is not null) return Early("PhysicsLayoutProfileUnsupported"); tbdy = chunk; }
        }
        if (type is null || fstr is null || tbdy is null || type.Flags != 0 || type.Parent != 0 || type.Depth != 1 ||
            fstr.Parent != type.Ordinal || tbdy.Parent != type.Ordinal || fstr.Ordinal >= tbdy.Ordinal)
            return Early("PhysicsLayoutProfileUnsupported");

        Snapshot? tstr = null, tna = null;
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.Tag == "TSTR") { if (tstr is not null) return Early("PhysicsLayoutProfileUnsupported"); tstr = chunk; }
            if (chunk.Tag == "TNA1") { if (tna is not null) return Early("PhysicsLayoutProfileUnsupported"); tna = chunk; }
        }
        if (tstr is null || tna is null || tstr.Parent != type.Ordinal || tna.Parent != type.Ordinal || tstr.Ordinal >= tna.Ordinal ||
            !ValidDeclarationContext(metadata, declarations, type, tstr, tna, limits, budget, token, out var finalSlots))
            return Early("PhysicsLayoutDeclarationContextInvalid");
        var scan = new Scan(metadata.Identity, type.Ordinal, fstr, tbdy, finalSlots, limits, budget, token);
        scan.Parse(); scan.Resolve(); token.ThrowIfCancellationRequested();
        var document = new PhysicsTypeLayoutsDocument(metadata.Identity, metadata.Revision, metadata.EmbeddedExtent, metadata.EmbeddedSha256,
            type.Ordinal, finalSlots, Leaf(fstr), Leaf(tbdy), scan.Names.ToArray(), scan.Records.ToArray(), scan.TableFullyVisited,
            scan.Diagnostics.Count == 0 ? PhysicsTypeLayoutCoverage.CompleteStructure : PhysicsTypeLayoutCoverage.Partial,
            scan.Unknown.ToArray(), budget.Used);
        token.ThrowIfCancellationRequested();
        return new(document, metadata.Identity, metadata.Revision, physical, steps, scan.Diagnostics.ToArray(), [], new Dictionary<string, string>());
    }

    private static bool ValidDeclarationContext(PhysicsTaggedMetadataDocument source, PhysicsTypeDeclarationsDocument input,
        Snapshot type, Snapshot strings, Snapshot declarations, PhysicsTypeLayoutLimits limits, Budget budget,
        CancellationToken token, out int count)
    {
        count = 0;
        if (input.Identity != source.Identity || input.Revision != source.Revision || input.EmbeddedExtent != source.EmbeddedExtent ||
            input.InheritedEmbeddedSha256 != source.EmbeddedSha256 || input.TypeChunkOrdinal != type.Ordinal || input.InitialSlotCount != 1 ||
            input.DeclarationTable is null || input.StringTable is null || input.FinalSlotCount is not { } field || input.Declarations is null ||
            !Enum.IsDefined(input.Coverage) || !MatchesLeaf(input.DeclarationTable, declarations) || !MatchesLeaf(input.StringTable, strings)) return false;
        var rows = input.Declarations.Count; token.ThrowIfCancellationRequested();
        if (rows < 0 || rows >= limits.MaximumRecords || !budget.Reserve(8L * rows + 512)) return false;
        for (var index = 0; index < rows; index++)
        {
            token.ThrowIfCancellationRequested(); var row = input.Declarations[index]; token.ThrowIfCancellationRequested();
            if (row is null || row.Identity is null || row.Identity.Owner != source.Identity || row.Identity.TypeChunkOrdinal != type.Ordinal || row.Identity.Slot != index + 1)
                return false;
        }
        var cursor = 0;
        if (!PhysicsTypeIntegerReader.TryRead(input.DeclarationTable.RawBody.Span, ref cursor, out var bits, out var width, out _) ||
            field.RawBits != bits || field.NativeInt32 != unchecked((int)bits) || field.Extent is null || field.Extent.Container != declarations.BodyExtent.Container ||
            field.Extent.Offset != declarations.BodyExtent.Offset || field.Extent.Length != width ||
            !field.RawEncoding.Span.SequenceEqual(input.DeclarationTable.RawBody.Span[..width])) return false;
        count = unchecked((int)bits);
        if (count < 1 || count > limits.MaximumRecords || rows != count - 1) return false;
        var declarationHash = Hash(input.DeclarationTable.RawBody.Span, token);
        var stringHash = Hash(input.StringTable.RawBody.Span, token);
        return declarationHash == declarations.Hash && stringHash == strings.Hash;
    }
    private static bool MatchesLeaf(PhysicsTypeDeclarationLeaf input, Snapshot source) => input.ChunkOrdinal == source.Ordinal &&
        input.HeaderExtent == source.HeaderExtent && input.BodyExtent == source.BodyExtent && input.RawBigEndianWord == source.Word &&
        input.RawHeader.Span.SequenceEqual(source.Header) && input.RawBody.Length == source.BodyExtent.Length && input.Sha256 == source.Hash;
    private static PhysicsTypeDeclarationLeaf Leaf(Snapshot item) => new(item.Ordinal, item.HeaderExtent, item.BodyExtent, item.Word, item.Hash!, item.Header, item.Body!);
    private static DecodeStep Step() => new(nameof(PhysicsTypeLayoutsReader), Version, "TBDY; raw-gated-values; local-layout-links");
    private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token) { token.ThrowIfCancellationRequested(); var result = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); token.ThrowIfCancellationRequested(); return result; }
    private static bool HashShape(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool OptionalHash(string? hash) => hash is null || HashShape(hash);
    private static bool ValidText(string? text, bool empty = false) => text is not null && text.Length <= 4096 && (empty || !string.IsNullOrWhiteSpace(text));
    private static long TextBytes(string? text) => text is null ? 0 : 24L + 2L * text.Length;
    private static bool ValidSourceName(string? text) => ValidText(text) && !text!.Contains(':') && !text.StartsWith('/') && !text.StartsWith('\\') &&
        !text.Split('/', '\\').Any(part => part is "" or "." or "..");
    private static bool Inside(long offset, long length, DecodedExtent parent) => offset >= parent.Offset && length >= 0 && offset <= long.MaxValue - length &&
        parent.Offset >= 0 && parent.Length >= 0 && parent.Offset <= long.MaxValue - parent.Length && offset + length <= parent.Offset + parent.Length;
    private static bool ValidExtent(DecodedExtent? extent, DecodedExtent parent) => extent is not null && extent.Container == parent.Container && Inside(extent.Offset, extent.Length, parent);
    private static bool ValidIdentity(PhysicsTaggedMetadataDocument input)
    {
        if (input.Identity is not { } id || input.Revision is not { } revision || input.EmbeddedExtent is not { } extent || input.Chunks is null ||
            !ValidText(id.IndexContentId) || id.Ordinal < 0 || !ValidText(id.MemberPath) || id.IndexContentId != revision.IndexContentId ||
            !ValidText(revision.PhysicalSourceStamp) || extent.Container is null || extent.Container.MemberPath is not null ||
            extent.Container.IndexContentId != id.IndexContentId || extent.Container.Ordinal != id.Ordinal ||
            extent.Offset < 0 || extent.Length < 8 || extent.Offset > long.MaxValue - extent.Length || !HashShape(input.EmbeddedSha256) ||
            input.Coverage != PhysicsTaggedMetadataCoverage.CompleteFraming) return false;
        var parts = id.MemberPath!.Split('/');
        return parts.Length == 4 && parts[0] == "dc-object" && parts[2] == "2201" &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directory) && directory >= 0 && parts[1] == directory.ToString(CultureInfo.InvariantCulture) &&
            uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) && parts[3] == nativeId.ToString(CultureInfo.InvariantCulture);
    }
    private static void ValidateLimits(PhysicsTypeLayoutLimits l)
    {
        if (l.MaximumChunks < 0 || l.MaximumChunks > 4096 || l.MaximumDepth < 0 || l.MaximumDepth > 64 || l.MaximumLeafBytes < 0 || l.MaximumLeafBytes > 1024 * 1024 ||
            l.MaximumStrings < 0 || l.MaximumStrings > 65536 || l.MaximumStringBytes < 0 || l.MaximumStringBytes > 4096 || l.MaximumRecords < 1 || l.MaximumRecords > 65536 ||
            l.MaximumMembersPerRecord < 0 || l.MaximumMembersPerRecord > 65536 || l.MaximumMembers < 0 || l.MaximumMembers > 65536 ||
            l.MaximumMetadataBytes < 0 || l.MaximumMetadataBytes > 64L * 1024 * 1024 || l.MaximumPhysicalExtents < 0 || l.MaximumPhysicalExtents > 262144 ||
            l.MaximumPrecedingSteps < 0 || l.MaximumPrecedingSteps > 4096) throw new ArgumentOutOfRangeException(nameof(l));
    }
    private sealed class Budget(long maximum) { public long Used { get; private set; } public bool Reserve(long bytes) { if (bytes < 0 || bytes > maximum - Used) return false; Used += bytes; return true; } }
    private sealed record Snapshot(int Ordinal, int? Parent, int Depth, string Tag, byte Flags, uint Word,
        DecodedExtent HeaderExtent, DecodedExtent BodyExtent, byte[] Header, byte[]? Body, string? Hash);

    private sealed class Scan(ResourceIdentity owner, int table, Snapshot strings, Snapshot layouts, int slotCount,
        PhysicsTypeLayoutLimits limits, Budget budget, CancellationToken token)
    {
        public List<PhysicsTypeName> Names { get; } = []; public List<PhysicsTypeLayoutRecord> Records { get; } = [];
        public List<string> Diagnostics { get; } = []; public List<DecodedExtent> Unknown { get; } = [];
        public bool TableFullyVisited { get; private set; }
        private int cursor, membersTotal;
        private DecodedExtent At(Snapshot source, int offset, int length) => new(source.BodyExtent.Container, source.BodyExtent.Offset + offset, length);
        private void Diagnostic(string code) { if (!Diagnostics.Contains(code)) Diagnostics.Add(code); }
        private void Partial(string code, Snapshot source, int start) { Diagnostic(code); if (start < source.Body!.Length) Unknown.Add(At(source, start, source.Body.Length - start)); }
        private bool Integer(out PhysicsTypeInteger value)
        {
            token.ThrowIfCancellationRequested(); var start = cursor;
            if (!budget.Reserve(128)) { Diagnostic("PhysicsLayoutMetadataLimit"); value = null!; return false; }
            if (!PhysicsTypeIntegerReader.TryRead(layouts.Body, ref cursor, out var bits, out var width, out var code)) { Diagnostic(code!); value = null!; return false; }
            value = new(bits, unchecked((int)bits), At(layouts, start, width), layouts.Body!.AsMemory(start, width)); return true;
        }
        private PhysicsLayoutReference Reference(PhysicsTypeInteger index) => new(index,
            index.NativeInt32 == 0 ? PhysicsLayoutReferenceStatus.NoLinkRequested : PhysicsLayoutReferenceStatus.Unresolved, []);
        public void Parse()
        {
            var bytes = strings.Body!; var start = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                token.ThrowIfCancellationRequested(); if (bytes[index] != 0) continue;
                if (index > start)
                {
                    var length = index - start;
                    if (Names.Count >= limits.MaximumStrings || length > limits.MaximumStringBytes || !budget.Reserve(256L + 2L * length))
                    { Partial("PhysicsLayoutStringLimit", strings, start); Partial("PhysicsLayoutRecordsUnvisited", layouts, 0); return; }
                    var printable = true; for (var at = start; at < index; at++) if (bytes[at] < 32 || bytes[at] > 126) printable = false;
                    Names.Add(new(Names.Count, printable ? Encoding.ASCII.GetString(bytes, start, length) : null, At(strings, start, length), bytes.AsMemory(start, length)));
                }
                start = index + 1;
            }
            if (start < bytes.Length) Partial("PhysicsLayoutStringTail", strings, start);
            while (cursor < layouts.Body!.Length)
            {
                token.ThrowIfCancellationRequested(); var recordStart = cursor;
                if (layouts.Body[cursor] == 0) { TableFullyVisited = true; Partial("PhysicsLayoutSkippedTail", layouts, cursor); return; }
                if (Records.Count >= limits.MaximumRecords || !budget.Reserve(768)) { Partial("PhysicsLayoutRecordLimit", layouts, recordStart); return; }
                if (!Integer(out var slot) || !Integer(out var parent) || !Integer(out var mask)) { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                if (slot.NativeInt32 <= 0 || slot.NativeInt32 >= slotCount || !ValidLink(parent)) { Partial("PhysicsLayoutSlotInvalid", layouts, recordStart); return; }
                if ((mask.RawBits & ~0x7fUL) != 0) { Partial("PhysicsLayoutMaskUnsupported", layouts, recordStart); return; }
                var gated = new List<PhysicsLayoutGatedValue>(); PhysicsLayoutReference? subtype = null;
                foreach (var bit in new byte[] { 1, 2, 4, 8, 16 })
                {
                    token.ThrowIfCancellationRequested(); if ((mask.RawBits & bit) == 0) continue;
                    if (!budget.Reserve(96)) { Partial("PhysicsLayoutMetadataLimit", layouts, recordStart); return; }
                    var values = new PhysicsTypeInteger[bit == 8 ? 2 : 1];
                    for (var index = 0; index < values.Length; index++) if (!Integer(out values[index])) { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                    gated.Add(new(bit, values));
                    if (bit == 2) { if (!ValidLink(values[0])) { Partial("PhysicsLayoutSubtypeInvalid", layouts, recordStart); return; } subtype = Reference(values[0]); }
                }
                PhysicsTypeInteger? memberCount = null, interfaceCount = null;
                var members = Array.Empty<PhysicsLayoutMember>(); var interfaces = Array.Empty<PhysicsLayoutInterface>();
                if ((mask.RawBits & 32) != 0)
                {
                    if (!Integer(out memberCount)) { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                    var size = Math.Max(0, memberCount.NativeInt32);
                    if (size > limits.MaximumMembersPerRecord || size > limits.MaximumMembers - membersTotal || !budget.Reserve(256L * size))
                    { Partial("PhysicsLayoutMemberLimit", layouts, recordStart); return; }
                    members = new PhysicsLayoutMember[size];
                    for (var index = 0; index < size; index++)
                    {
                        token.ThrowIfCancellationRequested(); var memberStart = cursor;
                        if (!Integer(out var name) || !Integer(out var flags) || !Integer(out var third) || !Integer(out var target))
                        { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                        var nameIndex = (ushort)name.RawBits; var rawFlags = (ushort)flags.RawBits; var projected = (ushort)third.RawBits;
                        if (nameIndex >= Names.Count || !ValidLink(target)) { Partial("PhysicsLayoutMemberInvalid", layouts, recordStart); return; }
                        if ((rawFlags & 0x10) != 0) { Partial("PhysicsLayoutMemberOrderUnsupported", layouts, recordStart); return; }
                        var effective = (ushort)((rawFlags & 0x30) == 0 ? rawFlags | 0x20 : rawFlags);
                        members[index] = new(index, name, Names[nameIndex], flags, rawFlags, effective, third, projected,
                            (uint)(projected | ((uint)effective << 16)), Reference(target), At(layouts, memberStart, cursor - memberStart));
                    }
                    membersTotal += size;
                }
                if ((mask.RawBits & 64) != 0)
                {
                    if (!Integer(out interfaceCount)) { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                    var size = Math.Max(0, interfaceCount.NativeInt32);
                    if (size > limits.MaximumMembersPerRecord || size > limits.MaximumMembers - membersTotal || !budget.Reserve(192L * size))
                    { Partial("PhysicsLayoutInterfaceLimit", layouts, recordStart); return; }
                    interfaces = new PhysicsLayoutInterface[size];
                    for (var index = 0; index < size; index++)
                    {
                        token.ThrowIfCancellationRequested(); var interfaceStart = cursor;
                        if (!Integer(out var target) || !Integer(out var scalar)) { Partial("PhysicsLayoutRecordIncomplete", layouts, recordStart); return; }
                        if (!ValidLink(target)) { Partial("PhysicsLayoutInterfaceInvalid", layouts, recordStart); return; }
                        interfaces[index] = new(target, new(owner, table, target.NativeInt32), scalar, At(layouts, interfaceStart, cursor - interfaceStart));
                    }
                    membersTotal += size; Diagnostic("PhysicsLayoutInterfaceProfileUnobserved");
                }
                Records.Add(new(new(owner, table, layouts.Ordinal, Records.Count, slot.NativeInt32), At(layouts, recordStart, cursor - recordStart), slot,
                    Reference(parent), mask, gated.ToArray(), subtype, memberCount, members, interfaceCount, interfaces));
            }
            TableFullyVisited = true;
        }
        private bool ValidLink(PhysicsTypeInteger field) => field.NativeInt32 >= 0 && field.NativeInt32 < slotCount;
        public void Resolve()
        {
            var index = new Dictionary<int, List<PhysicsLayoutRecordIdentity>>();
            foreach (var row in Records)
            {
                token.ThrowIfCancellationRequested();
                if (!index.TryGetValue(row.Identity.Slot, out var group)) { if (!budget.Reserve(192)) { Diagnostic("PhysicsLayoutResolutionLimit"); return; } index[row.Identity.Slot] = group = []; }
                if (!budget.Reserve(16)) { Diagnostic("PhysicsLayoutResolutionLimit"); return; } group.Add(row.Identity);
                if (group.Count > 1) Diagnostic("PhysicsLayoutDuplicateSlot");
            }
            PhysicsLayoutReference Bind(PhysicsLayoutReference reference)
            {
                token.ThrowIfCancellationRequested(); if (reference.Index.NativeInt32 == 0) return reference;
                if (!index.TryGetValue(reference.Index.NativeInt32, out var candidates))
                {
                    if (!TableFullyVisited) { Diagnostic("PhysicsLayoutTargetUnresolved"); return reference; }
                    Diagnostic("PhysicsLayoutTargetMissing"); return reference with { Status = PhysicsLayoutReferenceStatus.Missing };
                }
                if (!budget.Reserve(80L + 8L * candidates.Count)) { Diagnostic("PhysicsLayoutResolutionLimit"); return reference; }
                var result = candidates.ToArray(); token.ThrowIfCancellationRequested();
                if (!TableFullyVisited && result.Length == 1) Diagnostic("PhysicsLayoutTargetUnresolved");
                return reference with { Status = result.Length > 1 ? PhysicsLayoutReferenceStatus.Ambiguous :
                    TableFullyVisited ? PhysicsLayoutReferenceStatus.Unique : PhysicsLayoutReferenceStatus.Unresolved, Candidates = result };
            }
            for (var ordinal = 0; ordinal < Records.Count; ordinal++)
            {
                token.ThrowIfCancellationRequested(); var row = Records[ordinal];
                if (!budget.Reserve(256L + 96L * row.Members.Count)) { Diagnostic("PhysicsLayoutResolutionLimit"); return; }
                var members = new PhysicsLayoutMember[row.Members.Count];
                for (var member = 0; member < members.Length; member++) { token.ThrowIfCancellationRequested(); members[member] = row.Members[member] with { Type = Bind(row.Members[member].Type) }; }
                Records[ordinal] = row with { Parent = Bind(row.Parent), Subtype = row.Subtype is null ? null : Bind(row.Subtype), Members = members };
            }
        }
    }
}

