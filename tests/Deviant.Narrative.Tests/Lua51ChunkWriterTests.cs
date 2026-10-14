using System.Text;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class Lua51ChunkWriterTests
{
    private static readonly ResourceIdentity Identity = new("synthetic-index", 7);
    private static readonly SourceRevision Revision = new("synthetic-index", "synthetic-source");

    [Fact]
    public void SerializesTheFullGeneratedPrototypeTreeWithoutChangingBytes()
    {
        var bytes = BuildChunk(root => WritePrototype(root,
            source: [0xff, 0xc0], upvalues: 1,
            code: [Encode(36, bx: 0), Encode(0, a: 2, b: 3), Encode(34, c: 0), 0xdecafbad, Encode(30, b: 1)],
            constants: [(0, 0u), (1, 3u), (3, 0x7fc00001u)], strings: [(4, 0u, [0x80, 0xff])],
            children: [child => WritePrototype(child, source: [0xfe], upvalues: 1, code: [Encode(30, b: 1)])],
            lineInfo: [1, 4, 8], locals: [([0xfd], 0, 5)], upvalueNames: [[0xfc]]));
        var document = Read(bytes);
        var writer = new Lua51ChunkWriter();

        Assert.Equal(bytes, writer.Write(document));
        Assert.Equal(bytes, writer.Write(document with { RawBytes = [0xaa, 0xbb, 0xcc] }));
        Assert.Equal((byte?)3, Assert.Single(document.RootPrototype.Constants, c => c.Tag == 1).BooleanRawByte);
        Assert.Equal(new byte[] { 0x80, 0xff }, document.RootPrototype.Constants[3].StringValue!.ContentBytes);
        Assert.Equal(Lua51InstructionRole.ClosureBinding, document.RootPrototype.Code[1].Role);
        Assert.Equal(Lua51InstructionRole.SetListExtraData, document.RootPrototype.Code[3].Role);
        Assert.Equal(new uint[] { 1, 4, 8 }, document.RootPrototype.LineInfo);
        Assert.Equal(new byte[] { 0xfd }, Assert.Single(document.RootPrototype.Locals).Name.ContentBytes);
        Assert.Equal(new byte[] { 0xfc }, Assert.Single(document.RootPrototype.UpvalueNames).ContentBytes);
    }

    [Fact]
    public void ReencodesOneEditedNumberAndStringWhilePreservingOtherFields()
    {
        var bytes = BuildChunk(root => WritePrototype(root,
            code: [Encode(30, b: 1)], constants: [(1, 2u), (3, 0x3f800000u)],
            strings: [(4, 0u, Encoding.UTF8.GetBytes("before"))]));
        var original = Read(bytes);
        var constants = original.RootPrototype.Constants.ToArray();
        var numberIndex = Array.FindIndex(constants, item => item.Kind == Lua51ConstantKind.Number);
        constants[numberIndex] = constants[numberIndex] with { NumberBits = 0x3f000000u, NumberDisplay = "0.5" };
        var stringIndex = Array.FindIndex(constants, item => item.Kind == Lua51ConstantKind.String);
        var sourceString = constants[stringIndex].StringValue!;
        var replacement = Encoding.UTF8.GetBytes("edited text");
        constants[stringIndex] = constants[stringIndex] with
        {
            StringValue = sourceString with
            {
                SerializedBytes = [.. replacement, 0],
                ContentBytes = replacement,
                DisplayText = "edited text"
            }
        };
        var edited = original with { RootPrototype = original.RootPrototype with { Constants = constants } };

        var encoded = new Lua51ChunkWriter().Write(edited);
        var reparsed = Read(encoded);
        Assert.Equal((uint?)0x3f000000u, reparsed.RootPrototype.Constants[numberIndex].NumberBits);
        Assert.Equal(replacement, reparsed.RootPrototype.Constants[stringIndex].StringValue!.ContentBytes);
        Assert.Equal((byte?)2, reparsed.RootPrototype.Constants[0].BooleanRawByte);
    }

    [Fact]
    public void BooleanRawByteSurvivesWhenTruthIsUnchangedAndEditCanonicalizesIt()
    {
        var document = Read(BuildChunk(root => WritePrototype(root, constants: [(1, 0x7fu)])));
        Assert.True(document.RootPrototype.Constants[0].BooleanValue == true);
        Assert.Equal((byte?)0x7f, document.RootPrototype.Constants[0].BooleanRawByte);
        Assert.Equal((byte?)0x7f, Read(new Lua51ChunkWriter().Write(document)).RootPrototype.Constants[0].BooleanRawByte);

        var legacyConstants = document.RootPrototype.Constants.ToArray();
        legacyConstants[0] = legacyConstants[0] with { BooleanRawByte = null };
        var legacy = document with { RootPrototype = document.RootPrototype with { Constants = legacyConstants } };
        Assert.Equal((byte?)1, Read(new Lua51ChunkWriter().Write(legacy)).RootPrototype.Constants[0].BooleanRawByte);

        var constants = document.RootPrototype.Constants.ToArray();
        constants[0] = constants[0] with { BooleanValue = false };
        var edited = document with { RootPrototype = document.RootPrototype with { Constants = constants } };
        var reparsed = Read(new Lua51ChunkWriter().Write(edited));
        Assert.False(reparsed.RootPrototype.Constants[0].BooleanValue == true);
        Assert.Equal((byte?)0, reparsed.RootPrototype.Constants[0].BooleanRawByte);
    }

    [Fact]
    public void RejectsMalformedFieldsAndHonorsCountDepthOutputAndCancellationLimits()
    {
        var bytes = BuildChunk(root => WritePrototype(root,
            constants: [(0, 0u), (3, 1u)], children: [child => WritePrototype(child)]));
        var document = Read(bytes);
        var writer = new Lua51ChunkWriter();

        Assert.Throws<InvalidDataException>(() => writer.Write(document, new(MaximumChunkBytes: bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => writer.Write(document, new(MaximumDepth: 0)));
        Assert.Throws<InvalidDataException>(() => writer.Write(document, new(MaximumPrototypes: 1)));
        Assert.Throws<InvalidDataException>(() => writer.Write(document, new(MaximumCollectionEntries: 1)));
        var wrongHeader = (byte[])document.RawHeader.Clone(); wrongHeader[6] = 0;
        Assert.Throws<InvalidDataException>(() => writer.Write(document with { RawHeader = wrongHeader }));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => writer.Write(document, cancellationToken: canceled.Token));

        using var duringWrite = new CancellationTokenSource();
        var twoWords = Read(BuildChunk(root => WritePrototype(root, code: [Encode(30, b: 1), Encode(30, b: 1)])));
        var cancelOnAccess = new CancelOnIndexRead<Lua51InstructionWord>(twoWords.RootPrototype.Code, duringWrite);
        var canceledTree = twoWords with { RootPrototype = twoWords.RootPrototype with { Code = cancelOnAccess } };
        Assert.Throws<OperationCanceledException>(() => writer.Write(canceledTree, cancellationToken: duringWrite.Token));

        var constants = document.RootPrototype.Constants.ToArray();
        constants[1] = constants[1] with
        {
            Tag = 4,
            Kind = Lua51ConstantKind.String,
            NumberBits = null,
            StringValue = new Lua51RawString(false, [0x61], [0x61], "a", constants[1].Extent)
        };
        var malformed = document with { RootPrototype = document.RootPrototype with { Constants = constants } };
        Assert.Throws<InvalidDataException>(() => writer.Write(malformed));
    }

    private static Lua51ChunkDocument Read(byte[] bytes)
    {
        var extent = new DecodedExtent(Identity, 0, bytes.Length);
        var result = new Lua51ChunkReader().Read(bytes, Identity, Revision, extent, []);
        return result.Value ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
    }

    private static byte[] BuildChunk(Action<BinaryWriter> writeRoot)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 4, 0 });
        writeRoot(writer); return stream.ToArray();
    }

    private static void WritePrototype(BinaryWriter writer, byte[]? source = null, byte upvalues = 0,
        uint[]? code = null, (byte Tag, uint Value)[]? constants = null,
        (byte Tag, uint Value, byte[] Content)[]? strings = null,
        Action<BinaryWriter>[]? children = null, uint[]? lineInfo = null,
        (byte[] Name, uint Start, uint End)[]? locals = null, byte[][]? upvalueNames = null)
    {
        WriteString(writer, source); writer.Write(0u); writer.Write(0u);
        writer.Write(upvalues); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)2);
        code ??= []; writer.Write((uint)code.Length); foreach (var word in code) writer.Write(word);
        constants ??= []; strings ??= [];
        writer.Write((uint)(constants.Length + strings.Length));
        foreach (var constant in constants)
        {
            writer.Write(constant.Tag);
            if (constant.Tag == 1) writer.Write((byte)constant.Value);
            else if (constant.Tag == 3) writer.Write(constant.Value);
        }
        foreach (var value in strings) { writer.Write(value.Tag); WriteString(writer, value.Content); }
        children ??= []; writer.Write((uint)children.Length); foreach (var child in children) child(writer);
        lineInfo ??= []; writer.Write((uint)lineInfo.Length); foreach (var line in lineInfo) writer.Write(line);
        locals ??= []; writer.Write((uint)locals.Length);
        foreach (var local in locals) { WriteString(writer, local.Name); writer.Write(local.Start); writer.Write(local.End); }
        upvalueNames ??= []; writer.Write((uint)upvalueNames.Length); foreach (var name in upvalueNames) WriteString(writer, name);
    }

    private static void WriteString(BinaryWriter writer, byte[]? content)
    {
        if (content is null) { writer.Write(0u); return; }
        writer.Write((uint)content.Length + 1); writer.Write(content); writer.Write((byte)0);
    }

    private static uint Encode(byte opcode, byte a = 0, ushort b = 0, ushort c = 0, uint bx = 0) =>
        opcode == 36 ? opcode | ((uint)a << 6) | (bx << 14) :
        opcode | ((uint)a << 6) | ((uint)c << 14) | ((uint)b << 23);

    private sealed class CancelOnIndexRead<T>(IReadOnlyList<T> items, CancellationTokenSource stop) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index]
        {
            get { if (index == 0) stop.Cancel(); return items[index]; }
        }
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
