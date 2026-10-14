using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Sdk;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class Lua51ChunkReaderTests
{
    private const string IndexId = "test-index";
    private static readonly SourceRevision Revision = new(IndexId, "test-source");
    private static ResourceIdentity Identity(long ordinal = 1) => new(IndexId, ordinal);

    [Fact]
    public void ReadsHeaderPrototypeInstructionsConstantsAndDebugWithExactExtent()
    {
        var bytes = BuildChunk(writer => WritePrototype(writer, code:
        [
            Encode(30, a: 0, b: 1), // RETURN
            Encode(34, c: 0), 0xdecafbad // SETLIST data word
        ], constants: [(0, 0u), (1, 1u), (3, 0x7fc00001u), (4, 0u)]));
        var result = Read(bytes);

        Assert.NotNull(result.Value);
        var root = result.Value.RootPrototype;
        Assert.Equal(bytes, result.Value.RawBytes);
        Assert.Equal(bytes.AsSpan(0, 12).ToArray(), result.Value.RawHeader);
        Assert.Equal(bytes.Length, result.Value.ConsumedExtent.Length);
        Assert.Equal("lua51/proto/0", root.Identity.MemberPath);
        Assert.Equal(0x7fc00001u, root.Constants[2].NumberBits);
        Assert.Equal("NaN", root.Constants[2].NumberDisplay);
        Assert.True(root.Constants[0].Kind == Lua51ConstantKind.Nil);
        Assert.True(root.Constants[1].BooleanValue);
        var stringValue = root.Constants[3].StringValue!;
        Assert.Equal(new byte[] { 0 }, stringValue.SerializedBytes);
        Assert.Empty(stringValue.ContentBytes);
        Assert.Equal("SETLIST", root.Code[1].OpcodeName);
        Assert.Equal("CLOSE", Decode(Encode(35)).OpcodeName);
        Assert.Equal(Lua51InstructionRole.SetListExtraData, root.Code[2].Role);
        Assert.Equal(1, root.Code[2].AssociatedProgramCounter);
    }

    [Fact]
    public void PreservesClosureBindingWordsWithoutBuildingControlFlow()
    {
        var bytes = BuildChunk(writer =>
        {
            WritePrototype(writer, code: [Encode(36, bx: 0), Encode(0, a: 2, b: 3), Encode(30, b: 1)],
                children: [child => WritePrototype(child, upvalues: 1)]);
        });
        var result = Read(bytes);

        Assert.NotNull(result.Value);
        var root = result.Value.RootPrototype;
        Assert.Single(root.Children);
        Assert.Equal("lua51/proto/0/0", root.Children[0].Identity.MemberPath);
        Assert.Equal(Lua51InstructionRole.ClosureBinding, root.Code[1].Role);
        Assert.Equal(0, root.Code[1].AssociatedProgramCounter);
        Assert.Equal(Encode(0, a: 2, b: 3), root.Code[1].RawWord);
    }

    [Fact]
    public void RejectsUnsupportedHeaderAndTrailingBytes()
    {
        var valid = BuildChunk(writer => WritePrototype(writer));
        var wrongHeader = (byte[])valid.Clone(); wrongHeader[6] = 0;
        Assert.Contains("UnsupportedHeaderProfile", Read(wrongHeader).Diagnostics[0]);
        var otherVersion = (byte[])valid.Clone(); otherVersion[4] = 0x52;
        Assert.Contains("UnsupportedHeaderProfile", Read(otherVersion).Diagnostics[0]);
        Assert.Contains("TrailingBytes", Read([.. valid, 0x7f]).Diagnostics[0]);
    }

    [Fact]
    public void RejectsTruncationInvalidStringAndSpecialWordUnderflow()
    {
        var valid = BuildChunk(writer => WritePrototype(writer));
        Assert.Contains("TruncatedData", Read(valid[..^1]).Diagnostics[0]);

        var badString = BuildChunk(writer => WritePrototype(writer, source: [0xff]));
        badString[17] = 1;
        Assert.Contains("StringTerminatorMissing", Read(badString).Diagnostics[0]);

        var missingSetListWord = BuildChunk(writer => WritePrototype(writer, code: [Encode(34, c: 0)]));
        Assert.Contains("TruncatedSetListExtraWord", Read(missingSetListWord).Diagnostics[0]);
    }

    [Fact]
    public void EnforcesDepthCountAndRetainedMemoryLimits()
    {
        var nested = BuildChunk(writer => WritePrototype(writer,
            children: [child => WritePrototype(child, children: [grandchild => WritePrototype(grandchild)])]));
        Assert.Contains("DepthLimitExceeded", Read(nested, new(MaximumDepth: 1)).Diagnostics[0]);
        Assert.Contains("ChunkLimitExceeded", Read(nested, new(MaximumChunkBytes: nested.Length - 1)).Diagnostics[0]);
        var withCode = BuildChunk(writer => WritePrototype(writer, code: [Encode(30, b: 1)]));
        Assert.Contains("MemoryLimitExceeded", Read(withCode, new(MaximumRetainedBytes: withCode.Length)).Diagnostics[0]);
    }

    [Fact]
    public void PropagatesCancellationAndValidatesSourceExtentOwnership()
    {
        var bytes = BuildChunk(writer => WritePrototype(writer));
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(bytes, token: source.Token));

        var foreign = new Lua51ChunkReader().Read(bytes, Identity(), Revision,
            new DecodedExtent(Identity(2), 0, bytes.Length), [], cancellationToken: default);
        Assert.Contains("SourceIdentityMismatch", foreign.Diagnostics[0]);

        var chunkIdentity = Identity() with { MemberPath = "script/chunk-0" };
        var parentExtent = new DecodedExtent(Identity(), 37, bytes.Length);
        var parent = new Lua51ChunkReader().Read(bytes, chunkIdentity, Revision, parentExtent, []);
        Assert.NotNull(parent.Value);
        Assert.Equal(Identity(), parent.Value.ConsumedExtent.Container);
        Assert.Equal("script/chunk-0/proto/0", parent.Value.RootPrototype.Identity.MemberPath);

        var nestedParent = Identity() with { MemberPath = "script" };
        var nestedChunk = Identity() with { MemberPath = "script/chunk-0" };
        var nested = new Lua51ChunkReader().Read(bytes, nestedChunk, Revision,
            new DecodedExtent(nestedParent, 37, bytes.Length), []);
        Assert.NotNull(nested.Value);
        var siblingParent = Identity() with { MemberPath = "other" };
        var sibling = new Lua51ChunkReader().Read(bytes, nestedChunk, Revision,
            new DecodedExtent(siblingParent, 37, bytes.Length), []);
        Assert.Contains("SourceIdentityMismatch", sibling.Diagnostics[0]);
        var negativeOrdinal = Identity(-1);
        var invalidOrdinal = new Lua51ChunkReader().Read(bytes, negativeOrdinal,
            new SourceRevision(IndexId, "test-source"), new DecodedExtent(negativeOrdinal, 0, bytes.Length), []);
        Assert.Contains("SourceIdentityMismatch", invalidOrdinal.Diagnostics[0]);

        var invalidPath = ReadWithSources(bytes, [new SourceExtent("../archive.dat", 0, bytes.Length)]);
        Assert.Contains("InvalidPhysicalExtent", invalidPath.Diagnostics[0]);
        var invalidHash = ReadWithSources(bytes, [new SourceExtent("archive.dat", 0, bytes.Length, "not-a-hash")]);
        Assert.Contains("InvalidPhysicalExtent", invalidHash.Diagnostics[0]);
        var invalidBounds = ReadWithSources(bytes, [new SourceExtent("archive.dat", -1, bytes.Length)]);
        Assert.Contains("InvalidPhysicalExtent", invalidBounds.Diagnostics[0]);
    }

    [Fact]
    public void PreflightsHostileCountsAndTightBudgetsBeforeArrayAllocation()
    {
        var hostile = BuildChunk(writer => WritePrototype(writer));
        BinaryPrimitives.WriteUInt32LittleEndian(hostile.AsSpan(28, 4), 500_000);
        Assert.Contains("TruncatedData", Read(hostile).Diagnostics[0]);

        var valid = BuildChunk(writer => WritePrototype(writer, code: [Encode(30, b: 1)]));
        Assert.Contains("MemoryLimitExceeded", Read(valid, new(MaximumRetainedBytes: valid.Length + 1500)).Diagnostics[0]);

        var nullLabels = BuildChunk(writer => WritePrototype(writer, upvalueNameCount: 1));
        Assert.Contains("MemoryLimitExceeded", Read(nullLabels,
            new(MaximumRetainedBytes: nullLabels.Length + 1600)).Diagnostics[0]);
    }

    [LuaCorpusFact]
    public void ReadsEveryRetainedRealChunkFromProvidedManifests()
    {
        var rawPairs = Environment.GetEnvironmentVariable("DEVIANT_LUA_CORPUS_PAIRS");
        var configuredPairs = rawPairs ?? throw new InvalidOperationException("The corpus prerequisite attribute did not detect its required paths.");

        var totalChunks = 0;
        var totalResources = 0;
        var expectedChunks = 0;
        var expectedResources = 0;
        foreach (var pair in configuredPairs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = pair.Split('\t');
            Assert.Equal(2, fields.Length);
            var manifestPath = Path.GetFullPath(fields[0]);
            var sampleRoot = Path.GetFullPath(fields[1]);
            var manifestBytes = File.ReadAllBytes(manifestPath);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var entries = manifest.RootElement.EnumerateArray().ToArray();
            expectedResources += entries.Length;
            foreach (var entry in entries)
            {
                var indexId = entry.GetProperty("indexContentId").GetString()!;
                var ordinal = entry.GetProperty("ordinal").GetInt64();
                var chunks = entry.GetProperty("inspection").GetProperty("chunks").EnumerateArray().ToArray();
                expectedChunks += chunks.Length;
                var resourcePath = Directory.EnumerateFiles(sampleRoot, "*.bin", SearchOption.AllDirectories)
                    .Single(path => Path.GetFileName(path).EndsWith($"_r{ordinal:D8}.bin", StringComparison.OrdinalIgnoreCase));
                var resource = File.ReadAllBytes(resourcePath);
                Assert.Equal(entry.GetProperty("length").GetInt32(), resource.Length);
                Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant());
                Assert.Equal(entry.GetProperty("length").GetInt32(), entry.GetProperty("inspection").GetProperty("consumedBytes").GetInt32());
                var identity = new ResourceIdentity(indexId, ordinal);
                var revision = new SourceRevision(indexId, "private-harness-source");
                foreach (var chunk in chunks)
                {
                    var offset = chunk.GetProperty("chunkOffset").GetInt32();
                    var length = chunk.GetProperty("chunkLength").GetInt32();
                    Assert.InRange(offset, 0, resource.Length);
                    Assert.InRange(length, 1, resource.Length - offset);
                    var payload = resource.AsSpan(offset, length);
                    var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
                    Assert.Equal(chunk.GetProperty("chunkSha256").GetString(), sha);
                    var physical = new SourceExtent(entry.GetProperty("source").GetString()!,
                        entry.GetProperty("sourceOffset").GetInt64() + offset, length, sha);
                    var decoded = new DecodedExtent(identity, offset, length);
                    var chunkIdentity = identity with { MemberPath = $"script/chunk/{chunk.GetProperty("index").GetInt32()}" };
                    var result = new Lua51ChunkReader().Read(payload, chunkIdentity, revision, decoded, [physical]);
                    Assert.True(result.Value is not null, $"Resource ordinal {ordinal}, chunk {chunk.GetProperty("index").GetInt32()}: {string.Join("; ", result.Diagnostics)}");
                    Assert.Equal(length, result.Value!.ConsumedExtent.Length);
                    Assert.Equal(length, int.Parse(result.UnknownFields["consumedBytes"]));
                    totalChunks++;
                }
                totalResources++;
            }
        }

        Assert.True(expectedResources > 0);
        Assert.True(expectedChunks > 0);
        Assert.Equal(expectedResources, totalResources);
        Assert.Equal(expectedChunks, totalChunks);
    }

    private static DecodeResult<Lua51ChunkDocument> Read(byte[] bytes, Lua51ReadOptions? options = null,
        CancellationToken token = default)
    {
        var identity = Identity();
        return new Lua51ChunkReader().Read(bytes, identity, Revision,
            new DecodedExtent(identity, 0, bytes.Length), [], options, token);
    }

    private static DecodeResult<Lua51ChunkDocument> ReadWithSources(byte[] bytes,
        IReadOnlyList<SourceExtent> physicalSources)
    {
        var identity = Identity();
        return new Lua51ChunkReader().Read(bytes, identity, Revision,
            new DecodedExtent(identity, 0, bytes.Length), physicalSources);
    }

    private static byte[] BuildChunk(Action<BinaryWriter> writeRoot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 4, 0 });
        writeRoot(writer);
        return stream.ToArray();
    }

    private static void WritePrototype(BinaryWriter writer, byte upvalues = 0, byte[]? source = null,
        uint[]? code = null, (byte Tag, uint Value)[]? constants = null,
        Action<BinaryWriter>[]? children = null, int upvalueNameCount = 0)
    {
        WriteString(writer, source);
        writer.Write(0u); writer.Write(0u);
        writer.Write(upvalues); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)2);
        code ??= [];
        writer.Write((uint)code.Length); foreach (var word in code) writer.Write(word);
        constants ??= [];
        writer.Write((uint)constants.Length);
        foreach (var constant in constants)
        {
            writer.Write(constant.Tag);
            switch (constant.Tag)
            {
                case 0: break;
                case 1: writer.Write((byte)constant.Value); break;
                case 3: writer.Write(constant.Value); break;
                case 4: WriteString(writer, []); break;
                default: throw new ArgumentOutOfRangeException(nameof(constants));
            }
        }
        children ??= [];
        writer.Write((uint)children.Length); foreach (var child in children) child(writer);
        writer.Write(0u); // line information
        writer.Write(0u); // local variables
        writer.Write((uint)upvalueNameCount);
        for (var i = 0; i < upvalueNameCount; i++) WriteString(writer, null);
    }

    private static void WriteString(BinaryWriter writer, byte[]? content)
    {
        if (content is null) { writer.Write(0u); return; }
        writer.Write((uint)content.Length + 1);
        writer.Write(content); writer.Write((byte)0);
    }

    private static uint Encode(byte opcode, byte a = 0, ushort b = 0, ushort c = 0, uint bx = 0) =>
        opcode == 36 ? opcode | ((uint)a << 6) | (bx << 14) :
        opcode | ((uint)a << 6) | ((uint)c << 14) | ((uint)b << 23);

    private static Lua51InstructionWord Decode(uint word) => Read(BuildChunk(writer =>
        WritePrototype(writer, code: [word]))).Value!.RootPrototype.Code[0];
}

public sealed class LuaCorpusFactAttribute : FactAttribute
{
    public LuaCorpusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVIANT_LUA_CORPUS_PAIRS")))
            Skip = "Set DEVIANT_LUA_CORPUS_PAIRS to manifest and sample-root pairs to run the retained corpus check.";
    }
}
