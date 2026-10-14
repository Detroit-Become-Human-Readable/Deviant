using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class ScriptBundleInspectionReaderTests
{
    private static readonly ResourceIdentity Resource = new("generated-index", 17, "owner/bundle");
    private static readonly ResourceIdentity Parent = new("generated-index", 17, "owner");
    private static readonly SourceRevision Revision = new("generated-index", "generated-revision");
    private static readonly SourceExtent[] PhysicalSources = [new("volume.bin", 256, 4096)];
    private static readonly byte[] LuaHeader = [27, 76, 117, 97, 81, 0, 1, 4, 4, 4, 4, 0];

    [Fact]
    public void ParsesOnlySelectedChunkAndRetainsItsKeyMetadataIdentityAndSpan()
    {
        var first = new BundleChunk(0x11, LuaHeader, []);
        var secondBytecode = LuaChunk("selected", [ReturnWord, ReturnWord]);
        var second = new BundleChunk(0x22, secondBytecode,
            [new BundleMetadata(0x33, [0xde, 0xad, 0xbe, 0xef], 0x5a, 0xa5)]);
        var bundleBytes = Build(first, second);

        var result = new ScriptBundleInspectionReader().Read(bundleBytes, Resource, Revision,
            new(Parent, 700, bundleBytes.Length), PhysicalSources, 1);

        Assert.NotNull(result.Bundle.Value);
        Assert.Empty(result.Bundle.Diagnostics);
        Assert.Equal(2, result.Bundle.Value!.Chunks.Count);
        var selected = result.SelectedChunk!;
        Assert.Same(result.Bundle.Value.Chunks[1], selected);
        Assert.Equal(0x22UL, selected.OpaqueKey);
        Assert.Equal(Resource.MemberPath + "/script/chunk/1", selected.Identity.MemberPath);
        Assert.Equal(secondBytecode, selected.BytecodeBytes.ToArray());
        var metadata = Assert.Single(selected.Metadata);
        Assert.Equal(0x33UL, metadata.OpaqueKey);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, metadata.OpaquePayload.ToArray());
        Assert.Equal(0x5a, metadata.Flag0);
        Assert.Equal(0xa5, metadata.Flag1);

        var luaResult = result.LuaChunk!;
        Assert.NotNull(luaResult.Value);
        Assert.Equal(selected.Identity, luaResult.Value!.Identity);
        Assert.Equal(selected.BytecodeSpan, luaResult.Value.InputExtent);
        Assert.Equal(selected.BytecodeSpan, luaResult.Value.ConsumedExtent);
        Assert.Equal(secondBytecode, luaResult.Value.RawBytes);
        Assert.Equal("selected", luaResult.Value.RootPrototype.SourceName.DisplayText);
        Assert.Equal(2, luaResult.Value.RootPrototype.Code.Count);
        Assert.Equal(PhysicalSources, result.Bundle.Value.OriginalPhysicalExtents);
        Assert.Equal(PhysicalSources, luaResult.Value.OriginalPhysicalExtents);
        Assert.Equal(Revision, luaResult.Value.Revision);
        Assert.Equal(nameof(ScriptBundleReader), Assert.Single(result.Bundle.Steps).Decoder);
        Assert.Equal("Lua 5.1 binary chunk", Assert.Single(luaResult.Steps).Decoder);
    }

    [Fact]
    public void InnerLuaFailureKeepsValidBundleAndSelectedChunk()
    {
        var bytes = Build(new BundleChunk(0x44, LuaHeader, []));

        var result = new ScriptBundleInspectionReader().Read(bytes, Resource, Revision,
            new(Parent, 128, bytes.Length), PhysicalSources, 0);

        Assert.NotNull(result.Bundle.Value);
        Assert.NotNull(result.SelectedChunk);
        Assert.NotNull(result.LuaChunk);
        Assert.Null(result.LuaChunk!.Value);
        Assert.Contains(result.LuaChunk.Diagnostics, diagnostic => diagnostic.Contains("TruncatedData", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedOuterBundleReturnsItsFailureWithoutSelectingOrParsingLua()
    {
        var malformed = Build(new BundleChunk(0x55, LuaChunk("valid", [ReturnWord]), []));
        malformed = [.. malformed, 0xee];

        var result = new ScriptBundleInspectionReader().Read(malformed, Resource, Revision,
            new(Parent, 0, malformed.Length), PhysicalSources, 99);

        Assert.Null(result.Bundle.Value);
        Assert.Contains("ScriptBundleBodyLengthMismatch", result.Bundle.Diagnostics);
        Assert.Null(result.SelectedChunk);
        Assert.Null(result.LuaChunk);
    }

    [Fact]
    public void RejectsNegativeAndOutOfRangeChunkSelectionsAfterFraming()
    {
        var bytes = Build(new BundleChunk(0x66, LuaHeader, []));
        var reader = new ScriptBundleInspectionReader();

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(bytes, Resource, Revision,
            new(Parent, 0, bytes.Length), PhysicalSources, -1));
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(bytes, Resource, Revision,
            new(Parent, 0, bytes.Length), PhysicalSources, 1));
        Assert.Contains("between 0 and 0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservesCancellationBeforeFraming()
    {
        var bytes = Build(new BundleChunk(0x77, LuaChunk("cancel", [ReturnWord]), []));
        var reader = new ScriptBundleInspectionReader();
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.Read(bytes, Resource, Revision,
            new(Parent, 0, bytes.Length), PhysicalSources, 0, token: alreadyCancelled.Token));
    }

    private static byte[] Build(params BundleChunk[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("QZIP\0SBCHK___"u8);
        writer.Write(1U);
        writer.Write(0U);
        writer.Write((uint)chunks.Length);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Key);
            writer.Write((uint)chunk.Bytecode.Length);
            writer.Write(chunk.Bytecode);
            writer.Write((uint)chunk.Metadata.Length);
            foreach (var metadata in chunk.Metadata)
            {
                writer.Write(metadata.Key);
                writer.Write(19000U);
                writer.Write(1U);
                writer.Write("NOCHK___"u8);
                writer.Write(1U);
                writer.Write(metadata.Flag0);
                writer.Write(metadata.Flag1);
                writer.Write((uint)metadata.Payload.Length);
                writer.Write(metadata.Payload);
            }
        }
        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), (uint)(bytes.Length - 21));
        return bytes;
    }

    private static byte[] LuaChunk(string sourceName, IReadOnlyList<uint> code)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(LuaHeader);
        var sourceBytes = Encoding.UTF8.GetBytes(sourceName);
        writer.Write((uint)(sourceBytes.Length + 1));
        writer.Write(sourceBytes);
        writer.Write((byte)0);
        writer.Write(0U); // first line
        writer.Write(0U); // last line
        writer.Write((byte)0); // upvalues
        writer.Write((byte)0); // parameters
        writer.Write((byte)0); // vararg flags
        writer.Write((byte)2); // stack size
        writer.Write((uint)code.Count);
        foreach (var instruction in code) writer.Write(instruction);
        writer.Write(0U); // constants
        writer.Write(0U); // child prototypes
        writer.Write(0U); // line info
        writer.Write(0U); // locals
        writer.Write(0U); // upvalue names
        writer.Flush();
        return stream.ToArray();
    }

    private static uint ReturnWord => 30u | (1u << 23);

    private sealed record BundleChunk(ulong Key, byte[] Bytecode, BundleMetadata[] Metadata);
    private sealed record BundleMetadata(ulong Key, byte[] Payload, byte Flag0 = 0, byte Flag1 = 0);
}
