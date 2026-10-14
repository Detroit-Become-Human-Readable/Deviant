using System.Text;

namespace Deviant.Formats.Narrative;

public sealed record Lua51WriteOptions(int MaximumChunkBytes = 16 * 1024 * 1024,
    int MaximumDepth = 64, int MaximumPrototypes = 100_000,
    int MaximumCollectionEntries = 1_000_000, int MaximumStringBytes = 4 * 1024 * 1024);

/// <summary>Serializes the supported Lua 5.1 game profile.</summary>
public sealed class Lua51ChunkWriter
{
    public const string Version = "lua51-game-profile-writer-v1";
    private static ReadOnlySpan<byte> ExpectedHeader => [0x1b, 0x4c, 0x75, 0x61, 0x51, 0, 1, 4, 4, 4, 4, 0];

    /// <summary>Rebuilds the chunk from the represented prototype tree and raw field bytes.</summary>
    public byte[] Write(Lua51ChunkDocument document, Lua51WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new Lua51WriteOptions();
        ValidateOptions(options);
        if (document.RawHeader is null || document.RawHeader.Length != Lua51ChunkReader.HeaderLength ||
            !document.RawHeader.AsSpan().SequenceEqual(ExpectedHeader))
            throw new InvalidDataException("Only the captured Lua 5.1 game header profile can be serialized.");

        using var stream = new BoundedStream(options.MaximumChunkBytes);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.RawHeader);
            var context = new WriteContext(writer, stream, options, cancellationToken);
            context.WritePrototype(document.RootPrototype, 0);
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return stream.ToArray();
    }

    private static void ValidateOptions(Lua51WriteOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.MaximumChunkBytes is < Lua51ChunkReader.HeaderLength or > 64 * 1024 * 1024 ||
            o.MaximumDepth is < 0 or > 128 || o.MaximumPrototypes is < 1 or > 1_000_000 ||
            o.MaximumCollectionEntries is < 1 or > 4_000_000 || o.MaximumStringBytes is < 0 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(o), "One or more writer limits are outside the supported range.");
    }

    private sealed class WriteContext(BinaryWriter writer, BoundedStream stream, Lua51WriteOptions options,
        CancellationToken token)
    {
        private int prototypes;
        private int entries;

        public void WritePrototype(Lua51Prototype prototype, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (prototype is null) throw new InvalidDataException("A prototype is null.");
            if (depth > options.MaximumDepth) throw new InvalidDataException("The prototype tree exceeds the configured depth limit.");
            if (++prototypes > options.MaximumPrototypes) throw new InvalidDataException("The prototype tree exceeds the configured prototype limit.");

            WriteString(prototype.SourceName, "prototype source name");
            writer.Write(prototype.FirstLineDefined); writer.Write(prototype.LastLineDefined);
            writer.Write(prototype.UpvalueCount); writer.Write(prototype.ParameterCount);
            writer.Write(prototype.VarargFlags); writer.Write(prototype.MaximumStackSize);

            Count(prototype.Code, "instruction");
            writer.Write(Count32(prototype.Code.Count, "instruction"));
            for (var i = 0; i < prototype.Code.Count; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                writer.Write(prototype.Code[i].RawWord);
            }

            Count(prototype.Constants, "constant");
            writer.Write(Count32(prototype.Constants.Count, "constant"));
            foreach (var constant in prototype.Constants)
            {
                token.ThrowIfCancellationRequested();
                WriteConstant(constant);
            }

            Count(prototype.Children, "child prototype");
            writer.Write(Count32(prototype.Children.Count, "child prototype"));
            foreach (var child in prototype.Children) WritePrototype(child, depth + 1);

            Count(prototype.LineInfo, "line info");
            writer.Write(Count32(prototype.LineInfo.Count, "line info"));
            foreach (var line in prototype.LineInfo) { token.ThrowIfCancellationRequested(); writer.Write(line); }

            Count(prototype.Locals, "local variable");
            writer.Write(Count32(prototype.Locals.Count, "local variable"));
            foreach (var local in prototype.Locals)
            {
                token.ThrowIfCancellationRequested();
                WriteString(local.Name, "local name");
                writer.Write(local.StartProgramCounter); writer.Write(local.EndProgramCounter);
            }

            Count(prototype.UpvalueNames, "upvalue name");
            writer.Write(Count32(prototype.UpvalueNames.Count, "upvalue name"));
            foreach (var name in prototype.UpvalueNames) { token.ThrowIfCancellationRequested(); WriteString(name, "upvalue name"); }
            if (stream.Length > options.MaximumChunkBytes) throw new InvalidDataException("The serialized chunk exceeds the configured output limit.");
        }

        private void WriteConstant(Lua51Constant constant)
        {
            if (constant is null) throw new InvalidDataException("A constant is null.");
            writer.Write(constant.Tag);
            switch (constant.Tag, constant.Kind)
            {
                case (0, Lua51ConstantKind.Nil): break;
                case (1, Lua51ConstantKind.Boolean):
                    if (constant.BooleanValue is not bool value) throw new InvalidDataException("A boolean constant has no value.");
                    var raw = constant.BooleanRawByte;
                    writer.Write(raw is byte encoded && (encoded != 0) == value ? encoded : value ? (byte)1 : (byte)0);
                    break;
                case (3, Lua51ConstantKind.Number):
                    if (constant.NumberBits is not uint bits) throw new InvalidDataException("A number constant has no raw float bits.");
                    writer.Write(bits);
                    break;
                case (4, Lua51ConstantKind.String):
                    WriteString(constant.StringValue ?? throw new InvalidDataException("A string constant has no raw string value."), "string constant");
                    break;
                default: throw new InvalidDataException("The constant tag/kind pair is not supported by this Lua 5.1 profile.");
            }
        }

        private void WriteString(Lua51RawString value, string field)
        {
            if (value is null) throw new InvalidDataException($"The {field} is null.");
            if (value.SerializedBytes is null || value.ContentBytes is null)
                throw new InvalidDataException($"The {field} byte arrays are missing.");
            if (value.IsNull)
            {
                if (value.SerializedBytes.Length != 0 || value.ContentBytes.Length != 0)
                    throw new InvalidDataException($"The null {field} carries serialized bytes.");
                writer.Write(0u);
                return;
            }
            var bytes = value.SerializedBytes;
            if (bytes.Length == 0 || bytes.Length > options.MaximumStringBytes || bytes[^1] != 0 ||
                !bytes.AsSpan(0, bytes.Length - 1).SequenceEqual(value.ContentBytes))
                throw new InvalidDataException($"The {field} bytes are malformed or exceed the configured string limit.");
            writer.Write(checked((uint)bytes.Length));
            writer.Write(bytes);
        }

        private void Count<T>(IReadOnlyList<T> values, string field)
        {
            if (values is null) throw new InvalidDataException($"The {field} array is null.");
            if (values.Count > options.MaximumCollectionEntries || entries > options.MaximumCollectionEntries - values.Count)
                throw new InvalidDataException($"The {field} arrays exceed the configured collection limit.");
            entries += values.Count;
        }

        private static uint Count32(int count, string field) => count < 0 ?
            throw new InvalidDataException($"The {field} count is negative.") : checked((uint)count);
    }

    private sealed class BoundedStream(int limit) : MemoryStream
    {
        private void Check(int count)
        {
            if (count < 0 || Length > limit - (long)count)
                throw new InvalidDataException("The serialized chunk exceeds the configured output limit.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}
