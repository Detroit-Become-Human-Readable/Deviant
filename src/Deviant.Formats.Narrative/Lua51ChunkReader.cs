using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Narrative;

/// <summary>Reads one exact Lua 5.1 binary chunk in the supported Detroit profile. It never executes bytecode.</summary>
public sealed class Lua51ChunkReader
{
    public const string Version = "lua51-game-profile-reader-v1";
    public const int HeaderLength = 12;
    private static ReadOnlySpan<byte> ExpectedHeader => [0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 4, 0];
    private static readonly string[] OpcodeNames =
    [
        "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL", "GETTABLE", "SETGLOBAL",
        "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF", "ADD", "SUB", "MUL", "DIV", "MOD", "POW",
        "UNM", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE", "TEST", "TESTSET", "CALL",
        "TAILCALL", "RETURN", "FORLOOP", "FORPREP", "TFORLOOP", "SETLIST", "CLOSE", "CLOSURE", "VARARG"
    ];

    public DecodeResult<Lua51ChunkDocument> Read(ReadOnlySpan<byte> input, ResourceIdentity identity,
        SourceRevision revision, DecodedExtent inputExtent, IReadOnlyList<SourceExtent> physicalSources,
        Lua51ReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(inputExtent);
        ArgumentNullException.ThrowIfNull(physicalSources);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new Lua51ReadOptions();
        IReadOnlyList<SourceExtent> sources = Array.Empty<SourceExtent>();
        var steps = new List<DecodeStep> { new("Lua 5.1 binary chunk", Version, Describe(options)) };

        DecodeResult<Lua51ChunkDocument> Fail(string code, string message) =>
            new(null, identity, revision, sources, steps, [code + ": " + message], [],
                new Dictionary<string, string> { ["chunkProfile"] = "Lua 5.1, format 0, little-endian, int/size_t/instruction/number=4, number=nonintegral" });

        if (identity.Ordinal < 0 || string.IsNullOrWhiteSpace(identity.IndexContentId) || string.IsNullOrWhiteSpace(revision.IndexContentId) ||
            string.IsNullOrWhiteSpace(revision.PhysicalSourceStamp) || identity.IndexContentId != revision.IndexContentId ||
            inputExtent.Container is null || !IsContainerOf(inputExtent.Container, identity) ||
            inputExtent.Offset < 0 || inputExtent.Length != input.Length ||
            inputExtent.Offset > long.MaxValue - inputExtent.Length)
            return Fail("SourceIdentityMismatch", "The input extent, identity, revision, or byte length does not describe this exact chunk.");
        if (!ValidOptions(options)) return Fail("InvalidLimits", "One or more reader limits are outside the supported range.");
        var physicalSourceCount = physicalSources.Count;
        if (physicalSourceCount > 4096)
            return Fail("PhysicalExtentLimitExceeded", "The source has more physical extents than the supported limit.");
        long sourceMetadataBytes = 0;
        for (var i = 0; i < physicalSourceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extent = physicalSources[i];
            if (extent is null || !ValidPhysicalExtent(extent))
                return Fail("InvalidPhysicalExtent", "A physical source extent has an unsafe path, invalid bounds, or malformed SHA-256 text.");
            try
            {
                sourceMetadataBytes = checked(sourceMetadataBytes + 256L + extent.RelativeSource.Length * 2L +
                    (extent.PayloadSha256?.Length ?? 0) * 2L);
            }
            catch (OverflowException)
            {
                return Fail("MemoryLimitExceeded", "Provenance metadata exceeds the configured memory budget.");
            }
        }
        long baseRetained;
        try
        {
            baseRetained = checked((long)input.Length + HeaderLength +
                (identity.MemberPath?.Length ?? 0) * 2L + revision.IndexContentId.Length * 2L +
                revision.PhysicalSourceStamp.Length * 2L);
        }
        catch (OverflowException)
        {
            return Fail("MemoryLimitExceeded", "Provenance metadata exceeds the configured memory budget.");
        }
        if (baseRetained > options.MaximumRetainedBytes - sourceMetadataBytes)
            return Fail("MemoryLimitExceeded", "Input and provenance metadata exceed the configured memory budget.");
        var sourceSnapshot = new SourceExtent[physicalSourceCount];
        long copiedSourceMetadataBytes = 0;
        for (var i = 0; i < sourceSnapshot.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extent = physicalSources[i];
            if (extent is null || !ValidPhysicalExtent(extent))
                return Fail("InvalidPhysicalExtent", "A physical source extent changed to an invalid value while it was being captured.");
            try
            {
                copiedSourceMetadataBytes = checked(copiedSourceMetadataBytes + 256L + extent.RelativeSource.Length * 2L +
                    (extent.PayloadSha256?.Length ?? 0) * 2L);
                if (baseRetained > options.MaximumRetainedBytes - copiedSourceMetadataBytes)
                    return Fail("MemoryLimitExceeded", "The captured provenance metadata exceeds the configured memory budget.");
            }
            catch (OverflowException)
            {
                return Fail("MemoryLimitExceeded", "Captured provenance metadata exceeds the configured memory budget.");
            }
            sourceSnapshot[i] = extent;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var initialRetained = checked(baseRetained + copiedSourceMetadataBytes);
        sources = sourceSnapshot;
        if (input.Length > options.MaximumChunkBytes)
            return Fail("ChunkLimitExceeded", "The chunk exceeds the configured input-byte limit.");
        if (input.Length < HeaderLength) return Fail("TruncatedHeader", "The Lua binary header is incomplete.");
        if (!input[..HeaderLength].SequenceEqual(ExpectedHeader))
        {
            var signature = input[..Math.Min(4, input.Length)];
            return Fail(signature.SequenceEqual(ExpectedHeader[..4]) ? "UnsupportedHeaderProfile" : "InvalidSignature",
                "Only the observed Lua 5.1 format 0, little-endian, four-byte game profile is supported.");
        }

        try
        {
            var cursor = new ChunkCursor(input, inputExtent, options, cancellationToken, initialRetained);
            cursor.Retain(1024);
            var header = cursor.Take(HeaderLength).ToArray();
            var prototypeIdentity = identity with { MemberPath = (identity.MemberPath ?? "lua51") + "/proto/0" };
            var prototype = cursor.ReadPrototype(prototypeIdentity, 0);
            if (cursor.Position != input.Length)
                throw new LuaReadException("TrailingBytes", "The declared Lua chunk was not consumed exactly.");
            cancellationToken.ThrowIfCancellationRequested();

            var raw = input.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            cursor.Retain(256);
            var consumed = new DecodedExtent(inputExtent.Container, inputExtent.Offset, input.Length);
            var sha256 = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            var document = new Lua51ChunkDocument(identity, revision, inputExtent, consumed, raw, header,
                prototype, sources);
            steps[0] = new("Lua 5.1 binary chunk", Version, Describe(options), sha256);
            var result = new DecodeResult<Lua51ChunkDocument>(document, identity, revision, sources, steps, [], [],
                new Dictionary<string, string> { ["inputBytes"] = input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["consumedBytes"] = cursor.Position.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (LuaReadException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
        catch (OverflowException)
        {
            return Fail("IntegerOverflow", "A serialized range overflowed the supported integer bounds.");
        }
    }

    private static bool ValidOptions(Lua51ReadOptions o) => o.MaximumChunkBytes is > 0 and <= 64 * 1024 * 1024 &&
        o.MaximumDepth is >= 0 and <= 128 && o.MaximumPrototypes is > 0 and <= 1_000_000 &&
        o.MaximumCollectionEntries is > 0 and <= 4_000_000 && o.MaximumStringBytes is >= 0 and <= 16 * 1024 * 1024 &&
        o.MaximumRetainedBytes >= 0 && o.MaximumRetainedBytes <= 128L * 1024 * 1024;

    private static string Describe(Lua51ReadOptions o) =>
        $"maxChunk={o.MaximumChunkBytes};maxDepth={o.MaximumDepth};maxPrototypes={o.MaximumPrototypes};maxEntries={o.MaximumCollectionEntries};maxString={o.MaximumStringBytes};maxRetained={o.MaximumRetainedBytes}";

    private static bool IsContainerOf(ResourceIdentity container, ResourceIdentity chunk)
    {
        if (container.IndexContentId != chunk.IndexContentId || container.Ordinal != chunk.Ordinal) return false;
        if (container.MemberPath == chunk.MemberPath || container.MemberPath is null) return true;
        return chunk.MemberPath is not null &&
            chunk.MemberPath.StartsWith(container.MemberPath + "/", StringComparison.Ordinal);
    }

    private static bool ValidPhysicalExtent(SourceExtent extent)
    {
        if (string.IsNullOrWhiteSpace(extent.RelativeSource) || Path.IsPathRooted(extent.RelativeSource) ||
            extent.RelativeSource.Length > 4096 || extent.RelativeSource.Any(char.IsControl) || extent.Offset < 0 ||
            extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length)
            return false;
        var segments = extent.RelativeSource.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.Contains(':'))) return false;
        return extent.PayloadSha256 is null || extent.PayloadSha256.Length == 64 &&
            extent.PayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    private ref struct ChunkCursor
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly DecodedExtent parentExtent;
        private readonly Lua51ReadOptions options;
        private readonly CancellationToken cancellationToken;
        private int position;
        private int prototypes;
        private long retainedBytes;
        private int totalEntries;

        public ChunkCursor(ReadOnlySpan<byte> data, DecodedExtent parentExtent,
            Lua51ReadOptions options, CancellationToken cancellationToken, long initialRetainedBytes)
        {
            this.data = data;
            this.parentExtent = parentExtent;
            this.options = options;
            this.cancellationToken = cancellationToken;
            position = 0;
            prototypes = 0;
            retainedBytes = initialRetainedBytes;
            totalEntries = 0;
        }

        public int Position => position;

        public ReadOnlySpan<byte> Take(int length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length < 0 || position > data.Length - length)
                throw new LuaReadException("TruncatedData", "A serialized field extends beyond the supplied chunk.");
            var result = data.Slice(position, length);
            position += length;
            return result;
        }

        private uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        private DecodedExtent At(int start, int length) => new(parentExtent.Container,
            checked(parentExtent.Offset + start), length);

        private int Count(string field, int minimumEntryBytes)
        {
            var value = U32();
            if (value > int.MaxValue) throw new LuaReadException("CountLimitExceeded", $"The {field} count is outside the supported range.");
            var count = (int)value;
            if (count > options.MaximumCollectionEntries || totalEntries > options.MaximumCollectionEntries - count)
                throw new LuaReadException("CountLimitExceeded", $"The {field} count exceeds the configured collection limit.");
            if ((long)count * minimumEntryBytes > data.Length - position)
                throw new LuaReadException("TruncatedData", $"The {field} count cannot fit in the remaining chunk bytes.");
            totalEntries += count;
            return count;
        }

        public void Retain(long amount)
        {
            if (amount < 0 || retainedBytes > options.MaximumRetainedBytes - amount)
                throw new LuaReadException("MemoryLimitExceeded", "Decoded arrays and retained byte fields exceed the configured memory limit.");
            retainedBytes += amount;
        }

        public Lua51Prototype ReadPrototype(ResourceIdentity prototypeIdentity, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > options.MaximumDepth) throw new LuaReadException("DepthLimitExceeded", "The prototype tree exceeds the configured depth.");
            if (++prototypes > options.MaximumPrototypes) throw new LuaReadException("PrototypeLimitExceeded", "The prototype count exceeds the configured limit.");
            Retain(checked(256L + (prototypeIdentity.MemberPath?.Length ?? 0) * 2L));
            var start = position;
            var source = ReadString("source name");
            var firstLine = U32();
            var lastLine = U32();
            var upvalues = Take(4);
            var upvalueCount = upvalues[0];
            var parameters = upvalues[1];
            var vararg = upvalues[2];
            var stackSize = upvalues[3];

            var codeCount = Count("instruction", minimumEntryBytes: 4);
            Retain(checked((long)codeCount * 160));
            var code = new Lua51InstructionWord[codeCount];
            for (var i = 0; i < code.Length; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var wordOffset = position;
                var word = U32();
                code[i] = DecodeWord(i, word, At(wordOffset, 4), Lua51InstructionRole.Instruction);
            }

            var constantCount = Count("constant", minimumEntryBytes: 1);
            Retain(checked((long)constantCount * 112));
            var constants = new Lua51Constant[constantCount];
            for (var i = 0; i < constants.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var constantStart = position;
                var tag = Take(1)[0];
                constants[i] = tag switch
                {
                    0 => new(tag, Lua51ConstantKind.Nil, At(constantStart, position - constantStart)),
                    1 => ReadBoolean(tag, constantStart),
                    3 => ReadNumber(tag, constantStart),
                    4 => ReadStringConstant(tag, constantStart),
                    _ => throw new LuaReadException("UnsupportedConstantTag", $"Constant tag {tag} is not supported by this Lua 5.1 profile.")
                };
            }

            var childCount = Count("child prototype", minimumEntryBytes: 40);
            Retain(checked((long)childCount * IntPtr.Size));
            var children = new Lua51Prototype[childCount];
            for (var i = 0; i < children.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                children[i] = ReadPrototype(ChildIdentity(prototypeIdentity, i), depth + 1);
            }

            var lineCount = Count("line info", minimumEntryBytes: 4);
            Retain(checked((long)lineCount * 8));
            var lineInfo = new uint[lineCount];
            for (var i = 0; i < lineInfo.Length; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                lineInfo[i] = U32();
            }

            var localCount = Count("local variable", minimumEntryBytes: 12);
            Retain(checked((long)localCount * 112));
            var locals = new Lua51LocalVariable[localCount];
            for (var i = 0; i < locals.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localStart = position;
                var name = ReadString("local name");
                var begin = U32();
                var end = U32();
                locals[i] = new(name, begin, end, At(localStart, position - localStart));
            }

            var upvalueNameCount = Count("upvalue name", minimumEntryBytes: 4);
            Retain(checked((long)upvalueNameCount * 64));
            var upvalueNames = new Lua51RawString[upvalueNameCount];
            for (var i = 0; i < upvalueNames.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                upvalueNames[i] = ReadString("upvalue name");
            }

            Retain(checked((long)code.Length * 96));
            ClassifySpecialWords(code, children, cancellationToken);
            var extent = At(start, position - start);
            return new(prototypeIdentity, extent, source, firstLine, lastLine,
                upvalueCount, parameters, vararg, stackSize, code, constants, children,
                lineInfo, locals, upvalueNames);
        }

        private Lua51Constant ReadBoolean(byte tag, int start)
        {
            var value = Take(1)[0];
            return new(tag, Lua51ConstantKind.Boolean, At(start, position - start), BooleanValue: value != 0)
            { BooleanRawByte = value };
        }

        private Lua51Constant ReadNumber(byte tag, int start)
        {
            var bits = U32();
            var value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return new(tag, Lua51ConstantKind.Number, At(start, position - start), NumberBits: bits,
                NumberDisplay: value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private Lua51Constant ReadStringConstant(byte tag, int start)
        {
            var value = ReadString("string constant");
            return new(tag, Lua51ConstantKind.String, At(start, position - start), StringValue: value);
        }

        private Lua51RawString ReadString(string field)
        {
            var start = position;
            var length = U32();
            if (length == 0)
            {
                Retain(128);
                return new(true, [], [], string.Empty, At(start, position - start));
            }
            if (length > int.MaxValue || length > options.MaximumStringBytes)
                throw new LuaReadException("StringLimitExceeded", $"The {field} exceeds the configured string limit.");
            Retain(checked((long)length * 4 + 128));
            var serialized = Take((int)length).ToArray();
            if (serialized.Length == 0 || serialized[^1] != 0)
                throw new LuaReadException("StringTerminatorMissing", $"The {field} does not end with its serialized NUL byte.");
            var content = serialized.AsSpan(0, serialized.Length - 1).ToArray();
            return new(false, serialized, content, Encoding.UTF8.GetString(content), At(start, position - start));
        }

        private static ResourceIdentity ChildIdentity(ResourceIdentity chunkIdentity, int? childIndex)
        {
            var suffix = "/" + childIndex!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = (chunkIdentity.MemberPath ?? "lua51/proto/0") + suffix;
            return chunkIdentity with { MemberPath = path };
        }

        private static Lua51InstructionWord DecodeWord(int pc, uint word, DecodedExtent extent,
            Lua51InstructionRole role, int? associatedPc = null)
        {
            var op = (byte)(word & 0x3f);
            var a = (byte)((word >> 6) & 0xff);
            var c = (ushort)((word >> 14) & 0x1ff);
            var b = (ushort)((word >> 23) & 0x1ff);
            var bx = (word >> 14) & 0x3ffff;
            var sbx = unchecked((int)bx) - 131071;
            var name = op < OpcodeNames.Length ? OpcodeNames[op] : "UNKNOWN";
            return new(pc, word, extent, op, name, a, b, c, bx, sbx, role, associatedPc);
        }

        private static void ClassifySpecialWords(Lua51InstructionWord[] code, IReadOnlyList<Lua51Prototype> children,
            CancellationToken cancellationToken)
        {
            for (var pc = 0; pc < code.Length; pc++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = code[pc];
                if (instruction.RawOpcode == 34 && instruction.C == 0 && pc + 1 < code.Length)
                {
                    code[pc + 1] = DecodeWord(pc + 1, code[pc + 1].RawWord, code[pc + 1].Extent,
                        Lua51InstructionRole.SetListExtraData, pc);
                    pc++;
                    continue;
                }
                if (instruction.RawOpcode == 34 && instruction.C == 0)
                    throw new LuaReadException("TruncatedSetListExtraWord", "SETLIST with C=0 has no following raw data word.");
                if (instruction.RawOpcode != 36) continue;
                if (instruction.Bx >= children.Count)
                    throw new LuaReadException("InvalidClosurePrototype", "CLOSURE references a child prototype outside its parent.");
                var bindingCount = children[(int)instruction.Bx].UpvalueCount;
                if (code.Length - pc - 1 < bindingCount)
                    throw new LuaReadException("TruncatedClosureBindings", "CLOSURE is missing one or more following upvalue binding words.");
                for (var i = 1; i <= bindingCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    code[pc + i] = DecodeWord(pc + i, code[pc + i].RawWord, code[pc + i].Extent,
                        Lua51InstructionRole.ClosureBinding, pc);
                }
                pc += bindingCount;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class LuaReadException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
