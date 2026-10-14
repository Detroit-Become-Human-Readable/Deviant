using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Narrative;

/// <summary>Reads one bounded QD property-value payload. It preserves hashed names and never assigns property meanings.</summary>
public sealed class QdPropertyValueReader
{
    public const string Version = "qd-property-value-reader-v1";
    private const int MaximumPhysicalExtents = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public DecodeResult<QdPropertyValueDocument> Read(ReadOnlyMemory<byte> input, ResourceIdentity identity,
        SourceRevision revision, DecodedExtent inputExtent, IReadOnlyList<SourceExtent> physicalSources,
        QdPropertyValueReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(inputExtent);
        ArgumentNullException.ThrowIfNull(physicalSources);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new QdPropertyValueReadOptions();

        IReadOnlyList<SourceExtent> sourceSnapshot = Array.Empty<SourceExtent>();
        var steps = new List<DecodeStep> { new("QD property value", Version, Describe(options)) };
        DecodeResult<QdPropertyValueDocument> Fail(string code, string message,
            IReadOnlyDictionary<string, string>? unknown = null) =>
            new(null, identity, revision, sourceSnapshot, steps, [code + ": " + message], [],
                unknown ?? new Dictionary<string, string>());

        if (!ValidIdentity(identity, revision, inputExtent) || inputExtent.Length != input.Length)
            return Fail("SourceIdentityMismatch", "The input extent, resource identity and revision do not describe the same payload.");
        if (!ValidOptions(options))
            return Fail("InvalidLimits", "One or more reader limits are outside the supported range.");
        if (input.Length > options.MaximumPayloadBytes)
            return Fail("PayloadLimitExceeded", "The payload exceeds the configured input limit.");

        var sourceCount = physicalSources.Count;
        if (sourceCount < 0 || sourceCount > MaximumPhysicalExtents)
            return Fail("PhysicalExtentLimitExceeded", "The source has more physical extents than the supported limit.");

        long sourceMetadataBytes = 0;
        for (var i = 0; i < sourceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = physicalSources[i];
            if (source is null || !ValidPhysicalExtent(source))
                return Fail("InvalidPhysicalExtent", "A source extent has an unsafe relative path, invalid bounds or malformed SHA-256 text.");
            try
            {
                sourceMetadataBytes = checked(sourceMetadataBytes + 256L + source.RelativeSource.Length * 2L +
                    (source.PayloadSha256?.Length ?? 0) * 2L);
            }
            catch (OverflowException)
            {
                return Fail("MemoryLimitExceeded", "Source provenance exceeds the configured retained-memory budget.");
            }
        }

        long baseRetainedBytes;
        try
        {
            baseRetainedBytes = checked((long)input.Length + 1536L +
                (identity.IndexContentId.Length + revision.IndexContentId.Length + revision.PhysicalSourceStamp.Length +
                 (identity.MemberPath?.Length ?? 0)) * 2L);
        }
        catch (OverflowException)
        {
            return Fail("MemoryLimitExceeded", "Input provenance exceeds the configured retained-memory budget.");
        }
        if (baseRetainedBytes > options.MaximumRetainedBytes - sourceMetadataBytes)
            return Fail("MemoryLimitExceeded", "Input and source provenance exceed the configured retained-memory budget.");

        var capturedSources = new SourceExtent[sourceCount];
        long capturedSourceMetadataBytes = 0;
        for (var i = 0; i < capturedSources.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = physicalSources[i];
            if (source is null || !ValidPhysicalExtent(source))
                return Fail("InvalidPhysicalExtent", "A source extent changed to an invalid value while being captured.");
            try
            {
                capturedSourceMetadataBytes = checked(capturedSourceMetadataBytes + 256L + source.RelativeSource.Length * 2L +
                    (source.PayloadSha256?.Length ?? 0) * 2L);
                if (baseRetainedBytes > options.MaximumRetainedBytes - capturedSourceMetadataBytes)
                    return Fail("MemoryLimitExceeded", "Captured source provenance exceeds the configured retained-memory budget.");
            }
            catch (OverflowException)
            {
                return Fail("MemoryLimitExceeded", "Captured source provenance exceeds the configured retained-memory budget.");
            }
            capturedSources[i] = source;
        }
        cancellationToken.ThrowIfCancellationRequested();
        sourceSnapshot = capturedSources;

        var snapshot = new byte[input.Length];
        for (var offset = 0; offset < snapshot.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(64 * 1024, snapshot.Length - offset);
            input.Span.Slice(offset, count).CopyTo(snapshot.AsSpan(offset, count));
            offset += count;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var payloadHash = HashWithCancellation(snapshot, cancellationToken);
        steps[0] = new("QD property value", Version, Describe(options), payloadHash);
        cancellationToken.ThrowIfCancellationRequested();

        var unknownBase = new Dictionary<string, string>
        {
            ["payloadSha256"] = payloadHash,
            ["payloadExtent"] = $"{inputExtent.Offset.ToString(CultureInfo.InvariantCulture)}+{inputExtent.Length.ToString(CultureInfo.InvariantCulture)}"
        };

        try
        {
            var parser = new PayloadParser(snapshot, identity, inputExtent, options, cancellationToken,
                baseRetainedBytes + capturedSourceMetadataBytes);
            var root = parser.Parse();
            parser.ValidateCoverage();
            cancellationToken.ThrowIfCancellationRequested();
            var document = parser.ToDocument(revision, sourceSnapshot, root);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new DecodeResult<QdPropertyValueDocument>(document, identity, revision,
                sourceSnapshot, steps, [], [], new Dictionary<string, string>
                {
                    ["nodeCount"] = document.Nodes.Count.ToString(CultureInfo.InvariantCulture),
                    ["edgeCount"] = document.ChildEdges.Count.ToString(CultureInfo.InvariantCulture),
                    ["supportedProfiles"] = "boolean; signed integers 1/2/4/8; number 1/2/4/8; UTF-8 string; writer-sized packed arrays; header-only type 7; object/array"
                });
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (PayloadReadException ex)
        {
            var unknownOffset = Math.Clamp(ex.Offset, 0, snapshot.Length);
            var unknownLength = ex.Code.StartsWith("Unsupported", StringComparison.Ordinal)
                ? snapshot.Length - unknownOffset
                : ex.Length;
            unknownBase["unknownExtent"] =
                $"{checked(inputExtent.Offset + unknownOffset).ToString(CultureInfo.InvariantCulture)}+{unknownLength.ToString(CultureInfo.InvariantCulture)}";
            if (ex.Tag is { } tag) unknownBase["rawTag"] = tag.ToString("X2", CultureInfo.InvariantCulture);
            if (ex.Code.StartsWith("Unsupported", StringComparison.Ordinal))
                unknownBase["unsupportedProfile"] = ex.Code;
            return Fail(ex.Code, ex.Message, unknownBase);
        }
        catch (OverflowException)
        {
            unknownBase["unknownExtent"] = $"{inputExtent.Offset.ToString(CultureInfo.InvariantCulture)}+{input.Length.ToString(CultureInfo.InvariantCulture)}";
            return Fail("IntegerOverflow", "A serialized count or offset exceeded the supported integer range.", unknownBase);
        }
    }

    private static bool ValidOptions(QdPropertyValueReadOptions value) =>
        value.MaximumPayloadBytes is > 0 and <= 64 * 1024 * 1024 &&
        value.MaximumStringBytes is >= 0 and <= 16 * 1024 * 1024 &&
        value.MaximumNodes is > 0 and <= 1_000_000 &&
        value.MaximumEdges is > 0 and <= 4_000_000 &&
        value.MaximumDepth is >= 0 and <= 128 &&
        value.MaximumRetainedBytes is > 0 and <= 128L * 1024 * 1024;

    private static string Describe(QdPropertyValueReadOptions value) =>
        $"maxPayload={value.MaximumPayloadBytes};maxString={value.MaximumStringBytes};maxNodes={value.MaximumNodes};maxEdges={value.MaximumEdges};maxDepth={value.MaximumDepth};maxRetained={value.MaximumRetainedBytes}";

    private static bool ValidIdentity(ResourceIdentity identity, SourceRevision revision, DecodedExtent extent)
    {
        if (identity.Ordinal < 0 || string.IsNullOrWhiteSpace(identity.IndexContentId) ||
            string.IsNullOrWhiteSpace(revision.IndexContentId) || string.IsNullOrWhiteSpace(revision.PhysicalSourceStamp) ||
            identity.IndexContentId != revision.IndexContentId || extent.Container is null ||
            extent.Container.IndexContentId != identity.IndexContentId || extent.Container.Ordinal != identity.Ordinal ||
            extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length)
            return false;
        if (extent.Container.MemberPath == identity.MemberPath || extent.Container.MemberPath is null) return true;
        return identity.MemberPath is not null &&
            identity.MemberPath.StartsWith(extent.Container.MemberPath + "/", StringComparison.Ordinal);
    }

    private static bool ValidPhysicalExtent(SourceExtent value)
    {
        if (string.IsNullOrWhiteSpace(value.RelativeSource) || Path.IsPathRooted(value.RelativeSource) ||
            value.RelativeSource.Length > 4096 || value.RelativeSource.Any(char.IsControl) ||
            value.Offset < 0 || value.Length < 0 || value.Offset > long.MaxValue - value.Length)
            return false;
        if (value.RelativeSource.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".." || part.Contains(':')))
            return false;
        return value.PayloadSha256 is null || value.PayloadSha256.Length == 64 &&
            value.PayloadSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private static string HashWithCancellation(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(128 * 1024, bytes.Length - offset);
            hash.AppendData(bytes.Slice(offset, count));
            offset += count;
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class PayloadParser
    {
        private readonly byte[] payload;
        private readonly ResourceIdentity identity;
        private readonly DecodedExtent inputExtent;
        private readonly QdPropertyValueReadOptions options;
        private readonly CancellationToken cancellationToken;
        private readonly Dictionary<int, MutableNode> nodesByOffset = [];
        private readonly HashSet<int> activeOffsets = [];
        private readonly List<MutableNode> nodes = [];
        private readonly List<MutableEdge> edges = [];
        private readonly List<PayloadInterval> ownIntervals = [];
        private long retainedBytes;

        public PayloadParser(byte[] payload, ResourceIdentity identity, DecodedExtent inputExtent,
            QdPropertyValueReadOptions options, CancellationToken cancellationToken, long initiallyRetained)
        {
            this.payload = payload;
            this.identity = identity;
            this.inputExtent = inputExtent;
            this.options = options;
            this.cancellationToken = cancellationToken;
            retainedBytes = initiallyRetained;
        }

        public MutableNode Parse()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length == 0)
                throw new PayloadReadException("TruncatedHeader", "The value payload is empty.", 0, 0);
            return ParseNode(0, parent: null, childOrdinal: null, depth: 0);
        }

        public void ValidateCoverage()
        {
            cancellationToken.ThrowIfCancellationRequested();
            SortIntervalsByStart();
            var cursor = 0;
            foreach (var interval in ownIntervals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (interval.Start < cursor)
                    throw new PayloadReadException("OverlappingNodeExtents", "Two distinct value nodes claim overlapping serialized bytes.", interval.Start, interval.End - interval.Start);
                if (interval.Start > cursor)
                    throw new PayloadReadException("UnsupportedPayloadGap", "The node table leaves a byte range without an established value profile.", cursor, interval.Start - cursor);
                cursor = interval.End;
            }
            if (cursor < payload.Length)
                throw new PayloadReadException("UnsupportedPayloadGap", "The payload contains a trailing range without an established value profile.", cursor, payload.Length - cursor);
            if (cursor > payload.Length)
                throw new PayloadReadException("NodeExtentOutOfBounds", "A value node extends past the supplied payload.", payload.Length, 0);
        }

        public QdPropertyValueDocument ToDocument(SourceRevision revision,
            IReadOnlyList<SourceExtent> physicalSources, MutableNode root)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SortEdgesByParentAndOrdinal();
            Retain(checked((long)nodes.Count * 256 + (long)edges.Count * 128));
            var publicNodes = new QdPropertyValueNode[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = nodes[i];
                var subtreeLength = node.SubtreeEnd - node.SubtreeStart;
                var subtreeIsContiguous = !node.HasAliasedDescendant && node.SubtreeOwnedBytes == subtreeLength;
                publicNodes[i] = new QdPropertyValueNode(node.NodeId, node.ValueIdentity,
                    node.Parent?.NodeId, node.ChildOrdinal, node.Start, node.Tag, node.Type,
                    node.ModeFlags, node.NameHash, node.Kind, Extent(node.Start, node.HeaderLength),
                    node.ValueLength is { } valueLength ? Extent(node.Start + node.HeaderLength, valueLength) : null,
                    Extent(node.Start, node.OwnLength),
                    subtreeIsContiguous ? Extent(node.SubtreeStart, subtreeLength) : null,
                    Extent(node.SubtreeStart, subtreeLength), subtreeIsContiguous,
                    node.IsAliased, node.ChildCount, node.ChildOffsetWidth, node.PackedElementCount,
                    node.PackedElementWidth, node.BooleanRawByte, node.BooleanValue, node.NumberEncoding,
                    node.NumericBits, node.SignedIntegerValue, node.ScaledIntegerValue, node.NumericDisplay,
                    node.NumericIsFinite, node.StringDisplay, node.StringIsValidUtf8,
                    node.ValueLength is { } rawLength ? payload.AsMemory(node.Start + node.HeaderLength, rawLength) : default);
            }
            var publicEdges = new QdPropertyValueChildEdge[edges.Count];
            for (var i = 0; i < edges.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edge = edges[i];
                publicEdges[i] = new(edge.Parent.NodeId, edge.Target.NodeId, edge.Ordinal,
                    edge.RelativeOffset, edge.Target.Start, edge.Target.IsAliased);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(identity, revision, inputExtent,
                Extent(0, payload.Length), payload, root.NodeId, publicNodes, publicEdges, physicalSources);
        }

        private MutableNode ParseNode(int start, MutableNode? parent, int? childOrdinal, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > options.MaximumDepth)
                throw new PayloadReadException("DepthLimitExceeded", "The child graph exceeds the configured nesting limit.", start, 0);
            if (start < 0 || start >= payload.Length)
                throw new PayloadReadException("ChildOffsetOutOfBounds", "A child offset is outside the supplied payload.",
                    Math.Clamp(start, 0, payload.Length), 0);
            if (activeOffsets.Contains(start))
                throw new PayloadReadException("ValueGraphCycle", "A child edge returns to a value on the active traversal path.", start, 1);
            if (nodesByOffset.TryGetValue(start, out var existing))
            {
                if (depth + existing.MaxSubtreeDepth - 1 > options.MaximumDepth)
                    throw new PayloadReadException("DepthLimitExceeded", "An aliased child path exceeds the configured nesting limit.", start, existing.OwnLength, existing.Tag);
                existing.IsAliased = true;
                return existing;
            }
            if (nodes.Count >= options.MaximumNodes)
                throw new PayloadReadException("NodeLimitExceeded", "The unique value-node count exceeds the configured limit.", start, 0);

            Require(start, 5, "TruncatedHeader");
            var tag = payload[start];
            if ((tag & 0x80) == 0)
                throw new PayloadReadException("UnsupportedTagProfile", "Writer-produced values use the high tag bit.", start, 1, tag);
            var type = (byte)(tag & 0x0f);
            var modeFlags = (byte)(tag & 0x70);
            var nameHash = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(start + 1, 4));
            var headerLength = 5;
            var valueLength = 0;
            var hasValueExtent = true;
            var ownLength = 5;
            ushort? childCount = null;
            int? childOffsetWidth = null;
            uint? packedCount = null;
            int? packedWidth = null;
            QdPropertyValueKind kind;
            byte? booleanRaw = null;
            bool? booleanValue = null;
            QdPropertyNumberEncoding? numberEncoding = null;
            ulong? numericBits = null;
            long? signedInteger = null;
            int? scaledInteger = null;
            string? numericDisplay = null;
            bool? numericFinite = null;
            string? stringDisplay = null;
            bool? stringValidUtf8 = null;

            switch (type)
            {
                case 0:
                    RequireNoScalarFlags(start, tag, type);
                    kind = QdPropertyValueKind.Boolean;
                    headerLength = 5; valueLength = 1; ownLength = 6;
                    Require(start, ownLength, "TruncatedBoolean");
                    booleanRaw = payload[start + 5];
                    booleanValue = booleanRaw != 0;
                    break;
                case 1:
                    RequireNoFourByteOffsetFlag(start, tag);
                    kind = QdPropertyValueKind.SignedInteger;
                    numberEncoding = QdPropertyNumberEncoding.SignedInteger;
                    var integerWidth = IntegerWidth(modeFlags, start, tag);
                    headerLength = 5; valueLength = integerWidth; ownLength = checked(headerLength + valueLength);
                    Require(start, ownLength, "TruncatedInteger");
                    numericBits = ReadUnsigned(start + 5, integerWidth);
                    signedInteger = SignExtend(numericBits.Value, integerWidth);
                    numericDisplay = signedInteger.Value.ToString(CultureInfo.InvariantCulture);
                    numericFinite = true;
                    break;
                case 2:
                    RequireNoFourByteOffsetFlag(start, tag);
                    kind = QdPropertyValueKind.Number;
                    numberEncoding = (modeFlags & 0x30) switch
                    {
                        0x00 => QdPropertyNumberEncoding.SignedInteger,
                        0x10 => QdPropertyNumberEncoding.ScaledInt16Hundredths,
                        0x20 => QdPropertyNumberEncoding.Float32,
                        0x30 => QdPropertyNumberEncoding.Float64,
                        _ => throw new InvalidOperationException()
                    };
                    var numberWidth = numberEncoding.Value switch
                    {
                        QdPropertyNumberEncoding.SignedInteger => 1,
                        QdPropertyNumberEncoding.ScaledInt16Hundredths => 2,
                        QdPropertyNumberEncoding.Float32 => 4,
                        QdPropertyNumberEncoding.Float64 => 8,
                        _ => throw new InvalidOperationException()
                    };
                    headerLength = 5; valueLength = numberWidth; ownLength = checked(headerLength + valueLength);
                    Require(start, ownLength, "TruncatedNumber");
                    numericBits = ReadUnsigned(start + 5, numberWidth);
                    DecodeNumber(numberEncoding.Value, numericBits.Value, out signedInteger,
                        out scaledInteger, out numericDisplay, out var isFinite);
                    numericFinite = isFinite;
                    break;
                case 3:
                    RequireNoScalarFlags(start, tag, type);
                    kind = QdPropertyValueKind.String;
                    Require(start, 9, "TruncatedStringHeader");
                    var stringLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(start + 5, 4));
                    if (stringLength > options.MaximumStringBytes || stringLength > options.MaximumPayloadBytes || stringLength > int.MaxValue)
                        throw new PayloadReadException("StringLimitExceeded", "The string exceeds the configured payload limit.", start + 5, 4, tag);
                    valueLength = checked((int)stringLength);
                    headerLength = 9; ownLength = checked(headerLength + valueLength);
                    Require(start, ownLength, "TruncatedString");
                    Retain(checked((long)valueLength * 2 + 128));
                    try
                    {
                        stringDisplay = StrictUtf8.GetString(payload, start + headerLength, valueLength);
                        stringValidUtf8 = true;
                    }
                    catch (DecoderFallbackException)
                    {
                        stringDisplay = Encoding.UTF8.GetString(payload, start + headerLength, valueLength);
                        stringValidUtf8 = false;
                    }
                    break;
                case 4:
                case 5:
                case 6:
                    RequireNoFourByteOffsetFlag(start, tag);
                    kind = QdPropertyValueKind.PackedArray;
                    var elementWidth = PackedWidth((byte)(tag & 0x3f), start, tag);
                    Require(start, 9, "TruncatedPackedArrayHeader");
                    var elementCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(start + 5, 4));
                    var packedByteLength = checked((long)elementCount * elementWidth);
                    if (packedByteLength > int.MaxValue || packedByteLength > options.MaximumPayloadBytes)
                        throw new PayloadReadException("PackedArrayLimitExceeded", "The packed byte count exceeds the configured payload limit.", start + 5, 4, tag);
                    headerLength = 9; valueLength = checked((int)packedByteLength); ownLength = checked(headerLength + valueLength);
                    Require(start, ownLength, "TruncatedPackedArray");
                    packedCount = elementCount; packedWidth = elementWidth;
                    break;
                case 7:
                    RequireNoScalarFlags(start, tag, type);
                    kind = QdPropertyValueKind.UnknownHeaderOnly;
                    headerLength = 5; ownLength = 5; valueLength = 0; hasValueExtent = false;
                    break;
                case 8:
                case 9:
                    Require((tag & 0x30) == 0, start, "UnsupportedContainerMode", "Object and array containers use only the 0x40 offset-width flag.", tag);
                    kind = type == 8 ? QdPropertyValueKind.Object : QdPropertyValueKind.Array;
                    Require(start, 7, "TruncatedContainerHeader");
                    var serializedChildCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(start + 5, 2));
                    childCount = serializedChildCount;
                    childOffsetWidth = (tag & 0x40) != 0 ? 4 : 3;
                    var tableLength = checked((long)serializedChildCount * childOffsetWidth.Value);
                    if (serializedChildCount > options.MaximumEdges - edges.Count)
                        throw new PayloadReadException("EdgeLimitExceeded", "The ordered child-edge count exceeds the configured limit.", start + 5, 2, tag);
                    if (tableLength > payload.Length - (start + 7))
                        throw new PayloadReadException("TruncatedOffsetTable", "The child-offset table extends past the payload.", start + 7, payload.Length - (start + 7), tag);
                    headerLength = 7; valueLength = checked((int)tableLength); ownLength = checked(headerLength + valueLength);
                    break;
                default:
                    throw new PayloadReadException("UnsupportedTypeProfile", $"Type nibble {type} has no established serialized extent.",
                        start, payload.Length - start, tag);
            }

            Require(start, ownLength, "NodeExtentOutOfBounds");
            var identityPathLength = (identity.MemberPath?.Length ?? "qd-value".Length) + 18;
            var nodeKeyLength = identity.IndexContentId.Length + 2 + identity.Ordinal.ToString(CultureInfo.InvariantCulture).Length + identityPathLength;
            Retain(checked(512L + nodeKeyLength * 2L + identityPathLength * 4L));
            var valueIdentity = MakeValueIdentity(identity, start);
            var node = new MutableNode(nodes.Count, valueIdentity.Key, valueIdentity, parent, childOrdinal, start, tag, type,
                modeFlags, nameHash, kind, headerLength, hasValueExtent ? valueLength : null, ownLength, childCount,
                childOffsetWidth, packedCount, packedWidth, booleanRaw, booleanValue,
                numberEncoding, numericBits, signedInteger, scaledInteger, numericDisplay,
                numericFinite, stringDisplay, stringValidUtf8);
            nodes.Add(node);
            nodesByOffset.Add(start, node);
            activeOffsets.Add(start);
            ownIntervals.Add(new(start, checked(start + ownLength)));
            node.SubtreeStart = start;
            node.SubtreeEnd = checked(start + ownLength);
            node.SubtreeOwnedBytes = ownLength;

            if (childCount is { } count)
            {
                var offsetWidth = childOffsetWidth!.Value;
                for (var ordinal = 0; ordinal < count; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (edges.Count >= options.MaximumEdges)
                        throw new PayloadReadException("EdgeLimitExceeded", "The ordered child-edge count exceeds the configured limit.", start + 5, 2, tag);
                    var tableOffset = checked(start + 7 + ordinal * offsetWidth);
                    var relativeOffset = offsetWidth == 3
                        ? ((uint)payload[tableOffset] << 16) | ((uint)payload[tableOffset + 1] << 8) | payload[tableOffset + 2]
                        : BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(tableOffset, 4));
                    var targetLong = (long)start + relativeOffset;
                    if (targetLong < 0 || targetLong >= payload.Length || targetLong > int.MaxValue)
                        throw new PayloadReadException("ChildOffsetOutOfBounds", "A relative child offset resolves outside the payload.",
                            tableOffset, offsetWidth, tag);
                    var targetOffset = (int)targetLong;
                    var alreadyParsed = nodesByOffset.ContainsKey(targetOffset);
                    var target = ParseNode(targetOffset, node, ordinal, depth + 1);
                    if (edges.Count >= options.MaximumEdges)
                        throw new PayloadReadException("EdgeLimitExceeded", "The ordered child-edge count exceeds the configured limit.", tableOffset, offsetWidth, tag);
                    Retain(128);
                    edges.Add(new(node, target, ordinal, relativeOffset, target.IsAliased));
                    node.SubtreeStart = Math.Min(node.SubtreeStart, target.SubtreeStart);
                    node.SubtreeEnd = Math.Max(node.SubtreeEnd, target.SubtreeEnd);
                    node.HasAliasedDescendant |= alreadyParsed || target.HasAliasedDescendant;
                    if (!alreadyParsed)
                        node.SubtreeOwnedBytes = checked(node.SubtreeOwnedBytes + target.SubtreeOwnedBytes);
                    node.MaxSubtreeDepth = Math.Max(node.MaxSubtreeDepth, checked(target.MaxSubtreeDepth + 1));
                    if (node.SubtreeEnd - node.SubtreeStart > payload.Length)
                        throw new PayloadReadException("SubtreeExtentOutOfBounds", "The computed subtree range exceeds the payload.", start, ownLength, tag);
                }
            }
            activeOffsets.Remove(start);
            cancellationToken.ThrowIfCancellationRequested();
            return node;
        }

        private void RequireNoScalarFlags(int start, byte tag, byte type)
        {
            if ((tag & 0x70) != 0)
                throw new PayloadReadException("UnsupportedModeProfile", $"Type {type} has unrecognized mode or flag bits.", start, 1, tag);
        }

        private void RequireNoFourByteOffsetFlag(int start, byte tag)
        {
            if ((tag & 0x40) != 0)
                throw new PayloadReadException("UnsupportedModeProfile", "The 0x40 flag is established only for container child-offset widths.", start, 1, tag);
        }

        private int IntegerWidth(byte modeFlags, int start, byte tag) => (modeFlags & 0x30) switch
        {
            0x00 => 1,
            0x10 => 2,
            0x20 => 4,
            0x30 => 8,
            _ => throw new PayloadReadException("UnsupportedIntegerMode", "The signed-integer width mode is not recognized.", start, 1, tag)
        };

        private static int PackedWidth(byte typeAndMode, int start, byte tag) => typeAndMode switch
        {
            0x04 or 0x05 => 1,
            0x15 => 2,
            0x25 or 0x26 => 4,
            0x35 or 0x36 => 8,
            _ => throw new PayloadReadException("UnsupportedPackedArrayMode", "The packed-array element width is not established for this type/mode pair.", start, 1, tag)
        };

        private void DecodeNumber(QdPropertyNumberEncoding encoding, ulong bits, out long? signed,
            out int? scaled, out string display, out bool finite)
        {
            signed = null; scaled = null; finite = true;
            switch (encoding)
            {
                case QdPropertyNumberEncoding.SignedInteger:
                    signed = unchecked((sbyte)bits);
                    display = signed.Value.ToString(CultureInfo.InvariantCulture);
                    return;
                case QdPropertyNumberEncoding.ScaledInt16Hundredths:
                    scaled = unchecked((short)bits);
                    display = ((decimal)scaled.Value / 100m).ToString(CultureInfo.InvariantCulture);
                    return;
                case QdPropertyNumberEncoding.Float32:
                    var single = BitConverter.Int32BitsToSingle(unchecked((int)(uint)bits));
                    finite = float.IsFinite(single);
                    display = single.ToString("R", CultureInfo.InvariantCulture);
                    return;
                case QdPropertyNumberEncoding.Float64:
                    var doubleValue = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                    finite = double.IsFinite(doubleValue);
                    display = doubleValue.ToString("R", CultureInfo.InvariantCulture);
                    return;
                default:
                    throw new InvalidOperationException("Unreachable property-number encoding.");
            }
        }

        private ulong ReadUnsigned(int offset, int width) => width switch
        {
            1 => payload[offset],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4)),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset, 8)),
            _ => throw new InvalidOperationException("Unsupported primitive width.")
        };

        private static long SignExtend(ulong bits, int width) => width switch
        {
            1 => unchecked((sbyte)bits),
            2 => unchecked((short)bits),
            4 => unchecked((int)bits),
            8 => unchecked((long)bits),
            _ => throw new InvalidOperationException("Unsupported signed integer width.")
        };

        private static ResourceIdentity MakeValueIdentity(ResourceIdentity source, int offset)
        {
            var prefix = source.MemberPath is null ? "qd-value" : source.MemberPath + "/qd-value";
            var path = prefix + "/" + offset.ToString("X8", CultureInfo.InvariantCulture);
            return new ResourceIdentity(source.IndexContentId, source.Ordinal, path);
        }

        private DecodedExtent Extent(int start, int length) =>
            new(inputExtent.Container, checked(inputExtent.Offset + start), length);

        private void SortIntervalsByStart()
        {
            var count = ownIntervals.Count;
            for (var root = count / 2 - 1; root >= 0; root--) SiftInterval(root, count);
            for (var end = count - 1; end > 0; end--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ownIntervals[0], ownIntervals[end]) = (ownIntervals[end], ownIntervals[0]);
                SiftInterval(0, end);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void SiftInterval(int root, int count)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = root * 2 + 1;
                if (child >= count) return;
                if (child + 1 < count && ownIntervals[child].Start < ownIntervals[child + 1].Start) child++;
                if (ownIntervals[root].Start >= ownIntervals[child].Start) return;
                (ownIntervals[root], ownIntervals[child]) = (ownIntervals[child], ownIntervals[root]);
                root = child;
            }
        }

        private void SortEdgesByParentAndOrdinal()
        {
            var count = edges.Count;
            for (var root = count / 2 - 1; root >= 0; root--) SiftEdge(root, count);
            for (var end = count - 1; end > 0; end--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (edges[0], edges[end]) = (edges[end], edges[0]);
                SiftEdge(0, end);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void SiftEdge(int root, int count)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = root * 2 + 1;
                if (child >= count) return;
                if (child + 1 < count && CompareEdges(edges[child], edges[child + 1]) < 0) child++;
                if (CompareEdges(edges[root], edges[child]) >= 0) return;
                (edges[root], edges[child]) = (edges[child], edges[root]);
                root = child;
            }
        }

        private static int CompareEdges(MutableEdge left, MutableEdge right)
        {
            var parent = left.Parent.Sequence.CompareTo(right.Parent.Sequence);
            return parent != 0 ? parent : left.Ordinal.CompareTo(right.Ordinal);
        }

        private void Require(int start, int length, string code)
        {
            if (start < 0 || length < 0 || start > payload.Length - length)
                throw new PayloadReadException(code, "A serialized value field is truncated or outside the supplied payload.",
                    Math.Clamp(start, 0, payload.Length), Math.Max(0, Math.Min(length, payload.Length - Math.Clamp(start, 0, payload.Length))));
        }

        private void Require(bool condition, int start, string code, string message, byte tag)
        {
            if (!condition) throw new PayloadReadException(code, message, start, 1, tag);
        }

        private void Retain(long amount)
        {
            if (amount < 0 || retainedBytes > options.MaximumRetainedBytes - amount)
                throw new PayloadReadException("MemoryLimitExceeded", "Parsed rows, values and extents exceed the configured retained-memory budget.", 0, payload.Length);
            retainedBytes += amount;
        }

        public sealed class MutableNode(int sequence, string nodeId, ResourceIdentity valueIdentity,
            MutableNode? parent, int? childOrdinal,
            int start, byte tag, byte type, byte modeFlags, uint nameHash, QdPropertyValueKind kind,
            int headerLength, int? valueLength, int ownLength, ushort? childCount, int? childOffsetWidth,
            uint? packedElementCount, int? packedElementWidth, byte? booleanRawByte, bool? booleanValue,
            QdPropertyNumberEncoding? numberEncoding, ulong? numericBits, long? signedIntegerValue,
            int? scaledIntegerValue, string? numericDisplay, bool? numericIsFinite,
            string? stringDisplay, bool? stringIsValidUtf8)
        {
            public int Sequence { get; } = sequence;
            public string NodeId { get; } = nodeId;
            public ResourceIdentity ValueIdentity { get; } = valueIdentity;
            public MutableNode? Parent { get; } = parent;
            public int? ChildOrdinal { get; } = childOrdinal;
            public int Start { get; } = start;
            public byte Tag { get; } = tag;
            public byte Type { get; } = type;
            public byte ModeFlags { get; } = modeFlags;
            public uint NameHash { get; } = nameHash;
            public QdPropertyValueKind Kind { get; } = kind;
            public int HeaderLength { get; } = headerLength;
            public int? ValueLength { get; } = valueLength;
            public int OwnLength { get; } = ownLength;
            public ushort? ChildCount { get; } = childCount;
            public int? ChildOffsetWidth { get; } = childOffsetWidth;
            public uint? PackedElementCount { get; } = packedElementCount;
            public int? PackedElementWidth { get; } = packedElementWidth;
            public byte? BooleanRawByte { get; } = booleanRawByte;
            public bool? BooleanValue { get; } = booleanValue;
            public QdPropertyNumberEncoding? NumberEncoding { get; } = numberEncoding;
            public ulong? NumericBits { get; } = numericBits;
            public long? SignedIntegerValue { get; } = signedIntegerValue;
            public int? ScaledIntegerValue { get; } = scaledIntegerValue;
            public string? NumericDisplay { get; } = numericDisplay;
            public bool? NumericIsFinite { get; } = numericIsFinite;
            public string? StringDisplay { get; } = stringDisplay;
            public bool? StringIsValidUtf8 { get; } = stringIsValidUtf8;
            public bool IsAliased { get; set; }
            public int SubtreeStart { get; set; }
            public int SubtreeEnd { get; set; }
            public long SubtreeOwnedBytes { get; set; }
            public bool HasAliasedDescendant { get; set; }
            public int MaxSubtreeDepth { get; set; } = 1;
        }

        private sealed record MutableEdge(MutableNode Parent, MutableNode Target,
            int Ordinal, uint RelativeOffset, bool IsAlias);

        private readonly record struct PayloadInterval(int Start, int End);
    }

    private sealed class PayloadReadException(string code, string message, int offset, int length,
        byte? tag = null) : Exception(message)
    {
        public string Code { get; } = code;
        public int Offset { get; } = offset;
        public int Length { get; } = length;
        public byte? Tag { get; } = tag;
    }
}
