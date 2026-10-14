using System.Collections;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class Lua51BoundaryTests
{
    private static readonly ResourceIdentity Identity = new("generated-index", 1);
    private static readonly SourceRevision Revision = new("generated-index", "generated-source");

    [Fact]
    public void LiteralSetListOpcodeConsumesOneDataWord()
    {
        // Lua 5.1 enum ordering: SETLIST=34, CLOSE=35, CLOSURE=36.
        // https://www.lua.org/source/5.1/lopcodes.h.html
        var bytes = Chunk([34u, 0xdecafbadu, 30u | (1u << 23)]);
        var value = Assert.IsType<Lua51ChunkDocument>(Read(bytes).Value);
        Assert.Equal("SETLIST", value.RootPrototype.Code[0].OpcodeName);
        Assert.Equal(Lua51InstructionRole.SetListExtraData, value.RootPrototype.Code[1].Role);
        Assert.Equal(0, value.RootPrototype.Code[1].AssociatedProgramCounter);
        Assert.Equal(Lua51InstructionRole.Instruction, value.RootPrototype.Code[2].Role);
        Assert.Equal("RETURN", value.RootPrototype.Code[2].OpcodeName);
    }

    [Fact]
    public void LiteralCloseOpcodeKeepsFollowingReturnAsInstruction()
    {
        var bytes = Chunk([35u, 30u | (1u << 23)]);
        var value = Assert.IsType<Lua51ChunkDocument>(Read(bytes).Value);
        Assert.Equal("CLOSE", value.RootPrototype.Code[0].OpcodeName);
        Assert.Equal(Lua51InstructionRole.Instruction, value.RootPrototype.Code[1].Role);
        Assert.Null(value.RootPrototype.Code[1].AssociatedProgramCounter);
    }

    [Fact]
    public void LiteralSetListWithoutDataWordFailsAtInstructionBoundary()
    {
        var result = Read(Chunk([34u]));
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("TruncatedSetListExtraWord", StringComparison.Ordinal));
    }

    [Fact]
    public void NullDebugNamesCannotAvoidRetainedObjectAccounting()
    {
        const int names = 1000;
        var bytes = Chunk([], names);
        var result = Read(bytes, new(MaximumRetainedBytes: bytes.Length + names * 96L + 1024));
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("MemoryLimitExceeded", StringComparison.Ordinal));
    }

    [Fact]
    public void PhysicalSourceValidationObservesCancellationBetweenElements()
    {
        using var stop = new CancellationTokenSource(); var bytes = Chunk([]);
        var sources = new GuardedSources(3, _ => { stop.Cancel(); return new("generated.dat", 0, bytes.Length); });
        Assert.Throws<OperationCanceledException>(() => new Lua51ChunkReader().Read(bytes, Identity, Revision,
            new(Identity, 0, bytes.Length), sources, cancellationToken: stop.Token));
        Assert.Equal(1, sources.Accesses);
    }

    [Fact]
    public void PhysicalSourceCountIsRejectedBeforeAnyElementIsRead()
    {
        var bytes = Chunk([]);
        var sources = new GuardedSources(int.MaxValue, _ => throw new InvalidOperationException("No source should be accessed."));
        var result = new Lua51ChunkReader().Read(bytes, Identity, Revision, new(Identity, 0, bytes.Length), sources);
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("PhysicalExtentLimitExceeded", StringComparison.Ordinal));
        Assert.Equal(0, sources.Accesses);
    }

    [Fact]
    public void PhysicalSourceSnapshotCopyStopsWithoutReadingEveryElementAgain()
    {
        using var stop = new CancellationTokenSource(); var bytes = Chunk([]); var reads = 0;
        var sources = new GuardedSources(3, _ =>
        {
            if (++reads == 4) stop.Cancel();
            return new("generated.dat", 0, bytes.Length);
        });
        Assert.Throws<OperationCanceledException>(() => new Lua51ChunkReader().Read(bytes, Identity, Revision,
            new(Identity, 0, bytes.Length), sources, cancellationToken: stop.Token));
        Assert.Equal(4, sources.Accesses);
    }

    private static DecodeResult<Lua51ChunkDocument> Read(byte[] bytes, Lua51ReadOptions? options = null) =>
        new Lua51ChunkReader().Read(bytes, Identity, Revision, new(Identity, 0, bytes.Length), [], options);
    private static byte[] Chunk(uint[] code, int nullableUpvalueNames = 0)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(new byte[] { 0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 4, 0 });
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(new byte[] { 0, 0, 2, 2 });
        writer.Write((uint)code.Length); foreach (var word in code) writer.Write(word);
        writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        writer.Write((uint)nullableUpvalueNames); for (var index = 0; index < nullableUpvalueNames; index++) writer.Write(0u);
        return bytes.ToArray();
    }
    private sealed class GuardedSources(int count, Func<int, SourceExtent> read) : IReadOnlyList<SourceExtent>
    {
        public int Count => count;
        public int Accesses { get; private set; }
        public SourceExtent this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<SourceExtent> GetEnumerator() { for (var index = 0; index < count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
