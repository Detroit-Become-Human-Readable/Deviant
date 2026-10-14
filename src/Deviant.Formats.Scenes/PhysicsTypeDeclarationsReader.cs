using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads type declarations and references within one embedded TYPE table.</summary>
public sealed class PhysicsTypeDeclarationsReader
{
    public const string Version = "physics-type-declarations-v1";

    /// <summary>Input metadata must remain available and unchanged during this synchronous read.</summary>
    public DecodeResult<PhysicsTypeDeclarationsDocument> Read(PhysicsTaggedMetadataDocument metadata,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        PhysicsTypeDeclarationLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested(); limits ??= new(); ValidateLimits(limits);
        DecodeResult<PhysicsTypeDeclarationsDocument> Early(string code) => new(null, metadata.Identity, metadata.Revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (!ValidIdentity(metadata)) return Early("PhysicsTypeSourceMismatch");
        var physicalCount = physicalExtents.Count; token.ThrowIfCancellationRequested();
        var stepCount = precedingSteps?.Count ?? 0; token.ThrowIfCancellationRequested();
        var chunkCount = metadata.Chunks.Count; token.ThrowIfCancellationRequested();
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents || stepCount < 0 || stepCount > limits.MaximumPrecedingSteps ||
            chunkCount < 0 || chunkCount > limits.MaximumChunks) return Early("PhysicsTypeContextLimit");
        var budget = new Budget(limits.MaximumMetadataBytes);
        if (!budget.Reserve(4096L + 8L * (physicalCount + stepCount) + 384L * chunkCount +
            TextBytes(metadata.Identity.IndexContentId) + TextBytes(metadata.Identity.MemberPath) +
            TextBytes(metadata.Revision.PhysicalSourceStamp) + TextBytes(metadata.EmbeddedSha256))) return Early("PhysicsTypeContextLimit");
        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physicalCount; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = physicalExtents[index]; token.ThrowIfCancellationRequested();
            if (extent is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                !ValidSourceName(extent.RelativeSource) || !OptionalHash(extent.PayloadSha256)) return Early("PhysicsTypePhysicalExtentInvalid");
            if (!budget.Reserve(128 + TextBytes(extent.RelativeSource) + TextBytes(extent.PayloadSha256))) return Early("PhysicsTypeContextLimit");
            physical[index] = extent;
        }
        var steps = new DecodeStep[stepCount + 1];
        for (var index = 0; index < stepCount; index++)
        {
            token.ThrowIfCancellationRequested(); var step = precedingSteps![index]; token.ThrowIfCancellationRequested();
            if (step is null || !ValidText(step.Decoder) || !ValidText(step.Version) || !ValidText(step.Options, true) || !OptionalHash(step.PayloadSha256))
                return Early("PhysicsTypeContextInvalid");
            if (!budget.Reserve(128 + TextBytes(step.Decoder) + TextBytes(step.Version) + TextBytes(step.Options) + TextBytes(step.PayloadSha256)))
                return Early("PhysicsTypeContextLimit");
            steps[index] = step;
        }
        steps[^1] = Step();
        var chunks = new Snapshot[chunkCount];
        var previousChildEnds = new long[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            token.ThrowIfCancellationRequested(); var chunk = metadata.Chunks[index]; token.ThrowIfCancellationRequested();
            if (chunk is null || chunk.RawHeader is not { Length: 8 }) return Early("PhysicsTypeChunkInvalid");
            var header = chunk.RawHeader.ToArray(); token.ThrowIfCancellationRequested();
            var word = BinaryPrimitives.ReadUInt32BigEndian(header); var tag = Encoding.ASCII.GetString(header, 4, 4);
            if (chunk.Ordinal != index || chunk.RawBigEndianWord != word || chunk.RawFlags != word >> 30 || chunk.DeclaredLength != (word & 0x3fffffff) ||
                chunk.DeclaredLength < 8 || chunk.Tag != tag || chunk.Depth < 0 || chunk.Depth > limits.MaximumDepth ||
                !ValidExtent(chunk.HeaderExtent, metadata.EmbeddedExtent) || !ValidExtent(chunk.BodyExtent, metadata.EmbeddedExtent) ||
                chunk.HeaderExtent.Length != 8 || chunk.BodyExtent.Offset != chunk.HeaderExtent.Offset + 8 || chunk.BodyExtent.Length != chunk.DeclaredLength - 8)
                return Early("PhysicsTypeChunkInvalid");
            if (index == 0)
            {
                if (tag != "TAG0" || chunk.RawFlags != 0 || chunk.ParentOrdinal is not null || chunk.Depth != 0 ||
                    chunk.HeaderExtent.Offset != metadata.EmbeddedExtent.Offset || chunk.DeclaredLength != metadata.EmbeddedExtent.Length)
                    return Early("PhysicsTypeProfileUnsupported");
            }
            else
            {
                if (chunk.ParentOrdinal is not { } parent || parent < 0 || parent >= index || chunks[parent].Flags != 0 ||
                    chunk.Depth != chunks[parent].Depth + 1 || !Inside(chunk.HeaderExtent.Offset, chunk.DeclaredLength, chunks[parent].BodyExtent) ||
                    chunk.HeaderExtent.Offset < previousChildEnds[parent]) return Early("PhysicsTypeChunkInvalid");
                previousChildEnds[parent] = chunk.HeaderExtent.Offset + chunk.DeclaredLength;
            }
            byte[]? body = null; string? bodyHash = null;
            if (tag is "TSTR" or "TNA1")
            {
                if (chunk.RawFlags != 1 || chunk.MetadataBody is not { } borrowed || borrowed.Length != chunk.BodyExtent.Length || !HashShape(chunk.MetadataBodySha256))
                    return Early("PhysicsTypeLeafInvalid");
                if (borrowed.Length > limits.MaximumLeafBytes || !budget.Reserve(512L + borrowed.Length)) return Early("PhysicsTypeLeafLimit");
                body = borrowed.ToArray(); token.ThrowIfCancellationRequested(); bodyHash = Hash(body, token);
                if (!string.Equals(bodyHash, chunk.MetadataBodySha256, StringComparison.OrdinalIgnoreCase)) return Early("PhysicsTypeLeafHashMismatch");
            }
            chunks[index] = new(index, chunk.ParentOrdinal, chunk.Depth, tag, (byte)(word >> 30), word, chunk.HeaderExtent, chunk.BodyExtent, header, body, bodyHash);
        }
        token.ThrowIfCancellationRequested();
        Snapshot? type = null, tstr = null, tna = null;
        foreach (var chunk in chunks)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.Tag == "TYPE") { if (type is not null) return Early("PhysicsTypeProfileUnsupported"); type = chunk; }
            if (chunk.Tag == "TSTR") { if (tstr is not null) return Early("PhysicsTypeProfileUnsupported"); tstr = chunk; }
            if (chunk.Tag == "TNA1") { if (tna is not null) return Early("PhysicsTypeProfileUnsupported"); tna = chunk; }
        }
        if (type is null || tstr is null || tna is null || type.Flags != 0 || type.Parent != 0 || type.Depth != 1 ||
            tstr.Parent != type.Ordinal || tna.Parent != type.Ordinal || tstr.Ordinal >= tna.Ordinal)
            return Early("PhysicsTypeProfileUnsupported");
        var scan = new Scan(metadata.Identity, type.Ordinal, tstr, tna, limits, budget, token);
        scan.Parse(); token.ThrowIfCancellationRequested();
        var document = new PhysicsTypeDeclarationsDocument(metadata.Identity, metadata.Revision, metadata.EmbeddedExtent, metadata.EmbeddedSha256,
            type.Ordinal, Leaf(tstr), Leaf(tna), 1, scan.CountField, scan.Names.ToArray(), scan.Declarations.ToArray(),
            scan.Diagnostics.Count == 0 ? PhysicsTypeDeclarationCoverage.CompleteStructure : PhysicsTypeDeclarationCoverage.Partial,
            scan.Unknown.ToArray(), budget.Used);
        token.ThrowIfCancellationRequested();
        return new(document, metadata.Identity, metadata.Revision, physical, steps, scan.Diagnostics.ToArray(), [], new Dictionary<string, string>());
    }

    private static PhysicsTypeDeclarationLeaf Leaf(Snapshot item) => new(item.Ordinal, item.HeaderExtent, item.BodyExtent, item.Word, item.Hash!, item.Header, item.Body!);
    private static DecodeStep Step() => new(nameof(PhysicsTypeDeclarationsReader), Version, "TNA1; local-type-slots; raw64-bit-parameters");
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
    private static void ValidateLimits(PhysicsTypeDeclarationLimits l)
    {
        if (l.MaximumChunks < 0 || l.MaximumChunks > 4096 || l.MaximumDepth < 0 || l.MaximumDepth > 64 || l.MaximumLeafBytes < 0 || l.MaximumLeafBytes > 1024 * 1024 ||
            l.MaximumStrings < 0 || l.MaximumStrings > 65536 || l.MaximumStringBytes < 0 || l.MaximumStringBytes > 4096 || l.MaximumSlots < 1 || l.MaximumSlots > 65536 ||
            l.MaximumParametersPerDeclaration < 0 || l.MaximumParametersPerDeclaration > 65536 || l.MaximumParameters < 0 || l.MaximumParameters > 65536 ||
            l.MaximumMetadataBytes < 0 || l.MaximumMetadataBytes > 64L * 1024 * 1024 || l.MaximumPhysicalExtents < 0 || l.MaximumPhysicalExtents > 262144 ||
            l.MaximumPrecedingSteps < 0 || l.MaximumPrecedingSteps > 4096) throw new ArgumentOutOfRangeException(nameof(l));
    }
    private sealed class Budget(long maximum) { public long Used { get; private set; } public bool Reserve(long bytes) { if (bytes < 0 || bytes > maximum - Used) return false; Used += bytes; return true; } }
    private sealed record Snapshot(int Ordinal, int? Parent, int Depth, string Tag, byte Flags, uint Word,
        DecodedExtent HeaderExtent, DecodedExtent BodyExtent, byte[] Header, byte[]? Body, string? Hash);
    private sealed class Scan(ResourceIdentity owner, int table, Snapshot strings, Snapshot declarations,
        PhysicsTypeDeclarationLimits limits, Budget budget, CancellationToken token)
    {
        public List<PhysicsTypeName> Names { get; } = []; public List<PhysicsTypeDeclaration> Declarations { get; } = [];
        public List<string> Diagnostics { get; } = []; public List<DecodedExtent> Unknown { get; } = [];
        public PhysicsTypeInteger? CountField { get; private set; }
        private int cursor, parametersTotal;
        private DecodedExtent At(Snapshot source, int offset, int length) => new(source.BodyExtent.Container, source.BodyExtent.Offset + offset, length);
        private void Partial(string code, Snapshot source, int start) { if (!Diagnostics.Contains(code)) Diagnostics.Add(code); if (start < source.Body!.Length) Unknown.Add(At(source, start, source.Body.Length - start)); }
        private bool Integer(out PhysicsTypeInteger value)
        {
            token.ThrowIfCancellationRequested(); var start = cursor;
            if (!PhysicsTypeIntegerReader.TryRead(declarations.Body, ref cursor, out var bits, out var width, out var code))
            { if (!Diagnostics.Contains(code!)) Diagnostics.Add(code!); value = null!; return false; }
            value = new(bits, unchecked((int)bits), At(declarations, start, width), declarations.Body!.AsMemory(start, width)); return true;
        }
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
                    { Partial("PhysicsTypeStringLimit", strings, start); Partial("PhysicsTypeDeclarationsUnvisited", declarations, 0); return; }
                    var printable = true; for (var at = start; at < index; at++) if (bytes[at] < 32 || bytes[at] > 126) printable = false;
                    Names.Add(new(Names.Count, printable ? Encoding.ASCII.GetString(bytes, start, length) : null, At(strings, start, length), bytes.AsMemory(start, length)));
                }
                start = index + 1;
            }
            if (start < bytes.Length) Partial("PhysicsTypeStringTail", strings, start);
            if (!budget.Reserve(192)) { Partial("PhysicsTypeMetadataLimit", declarations, 0); return; }
            if (!Integer(out var count)) { Partial("PhysicsTypeCountIncomplete", declarations, 0); return; }
            CountField = count;
            if (count.NativeInt32 < 1 || count.NativeInt32 > limits.MaximumSlots) { Partial("PhysicsTypeSlotLimit", declarations, 0); return; }
            for (var slot = 1; slot < count.NativeInt32; slot++)
            {
                token.ThrowIfCancellationRequested(); var recordStart = cursor;
                if (!budget.Reserve(768)) { Partial("PhysicsTypeMetadataLimit", declarations, recordStart); return; }
                if (!Integer(out var nameIndex) || !Integer(out var parameterCount)) { Partial("PhysicsTypeDeclarationIncomplete", declarations, recordStart); return; }
                PhysicsTypeName? name = null;
                if (nameIndex.NativeInt32 >= 0)
                {
                    if (nameIndex.NativeInt32 >= Names.Count) { Partial("PhysicsTypeNameInvalid", declarations, recordStart); return; }
                    name = Names[nameIndex.NativeInt32];
                }
                var size = parameterCount.NativeInt32;
                if (size < 0 || size > limits.MaximumParametersPerDeclaration || size > limits.MaximumParameters - parametersTotal)
                { Partial("PhysicsTypeParameterLimit", declarations, recordStart); return; }
                if (!budget.Reserve(640L * size)) { Partial("PhysicsTypeMetadataLimit", declarations, recordStart); return; }
                var parameters = new PhysicsTypeParameter[size];
                for (var index = 0; index < size; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Integer(out var parameterName) || !Integer(out var parameterValue)) { Partial("PhysicsTypeDeclarationIncomplete", declarations, recordStart); return; }
                    if (parameterName.NativeInt32 < 0 || parameterName.NativeInt32 >= Names.Count) { Partial("PhysicsTypeParameterNameInvalid", declarations, recordStart); return; }
                    var named = Names[parameterName.NativeInt32]; var kind = named.RawBytes.Span[0] == (byte)'t' ? PhysicsTypeParameterKind.LocalTypeSlot : PhysicsTypeParameterKind.NumericValue;
                    PhysicsTypeSlotIdentity? target = null;
                    if (kind == PhysicsTypeParameterKind.LocalTypeSlot)
                    {
                        if (parameterValue.NativeInt32 < 0 || parameterValue.NativeInt32 >= count.NativeInt32) { Partial("PhysicsTypeTargetInvalid", declarations, recordStart); return; }
                        target = new(owner, table, parameterValue.NativeInt32);
                    }
                    parameters[index] = new(parameterName, named, parameterValue, kind, target, target?.Slot == 0);
                }
                parametersTotal += size;
                Declarations.Add(new(new(owner, table, slot), At(declarations, recordStart, cursor - recordStart), nameIndex, name, parameterCount, parameters));
            }
            if (cursor < declarations.Body!.Length) Partial("PhysicsTypeDeclarationTail", declarations, cursor);
        }
    }
}

