using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Deviant.Export.Models;

internal sealed class FbxBinary7400Node
{
    private const int HeaderBytes = 13;
    private const int NullRecordBytes = 13;

    public FbxBinary7400Node(string name, IReadOnlyList<FbxBinary7400Property>? properties = null,
        IReadOnlyList<FbxBinary7400Node>? children = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = Encoding.UTF8.GetBytes(name);
        if (Name.Length is < 1 or > byte.MaxValue)
            throw new InvalidDataException("An FBX binary node name must contain 1 to 255 UTF-8 bytes.");
        Properties = properties ?? [];
        Children = children ?? [];

        long propertyBytes = 0;
        foreach (var property in Properties)
            propertyBytes = checked(propertyBytes + property.SerializedBytes);
        if (propertyBytes > uint.MaxValue)
            throw new InvalidDataException("An FBX node property list exceeds the 7400 size limit.");
        PropertyBytes = (uint)propertyBytes;

        long size = checked(HeaderBytes + Name.Length + propertyBytes + NullRecordBytes);
        foreach (var child in Children)
            size = checked(size + child.SerializedBytes);
        SerializedBytes = size;
    }

    public byte[] Name { get; }
    public IReadOnlyList<FbxBinary7400Property> Properties { get; }
    public IReadOnlyList<FbxBinary7400Node> Children { get; }
    public uint PropertyBytes { get; }
    public long SerializedBytes { get; }
}

internal sealed class FbxBinary7400Property
{
    private const int MaximumStringBytes = 1_048_576;
    private readonly Action<BinaryWriter, CancellationToken> _writePayload;

    private FbxBinary7400Property(byte typeCode, long payloadBytes,
        Action<BinaryWriter, CancellationToken> writePayload)
    {
        if (payloadBytes < 0 || payloadBytes > uint.MaxValue + 4L)
            throw new InvalidDataException("An FBX binary property exceeds the 7400 size limit.");
        TypeCode = typeCode;
        PayloadBytes = payloadBytes;
        _writePayload = writePayload;
    }

    public byte TypeCode { get; }
    public long PayloadBytes { get; }
    public long SerializedBytes => checked(1 + PayloadBytes);

    public static FbxBinary7400Property Int32(int value) =>
        new((byte)'I', sizeof(int), (writer, _) => writer.Write(value));

    public static FbxBinary7400Property Int64(long value) =>
        new((byte)'L', sizeof(long), (writer, _) => writer.Write(value));

    public static FbxBinary7400Property Boolean(bool value) =>
        new((byte)'C', sizeof(byte), (writer, _) => writer.Write((byte)(value ? 1 : 0)));

    public static FbxBinary7400Property Single(float value) =>
        new((byte)'F', sizeof(float), (writer, _) => writer.Write(value));

    public static FbxBinary7400Property Double(double value) =>
        new((byte)'D', sizeof(double), (writer, _) => writer.Write(value));

    public static FbxBinary7400Property String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new InvalidDataException("An FBX string property exceeds the configured writer limit.");
        return new((byte)'S', checked(sizeof(uint) + bytes.Length), (writer, _) =>
        {
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        });
    }

    public static FbxBinary7400Property Array(byte typeCode, uint count, uint elementSize,
        Action<BinaryWriter, CancellationToken> writeValues)
    {
        ArgumentNullException.ThrowIfNull(writeValues);
        if (typeCode != (byte)'c' && typeCode != (byte)'b' && typeCode != (byte)'i' &&
            typeCode != (byte)'l' && typeCode != (byte)'f' && typeCode != (byte)'d')
            throw new ArgumentOutOfRangeException(nameof(typeCode));
        var dataBytes = checked((ulong)count * elementSize);
        if (dataBytes > uint.MaxValue)
            throw new InvalidDataException("An FBX array exceeds the 7400 byte-count limit.");
        return new(typeCode, checked(3L * sizeof(uint) + (long)dataBytes), (writer, token) =>
        {
            writer.Write(count);
            writer.Write(0u); // encoding 0: uncompressed array data
            writer.Write((uint)dataBytes);
            var start = writer.BaseStream.Position;
            writeValues(writer, token);
            if (writer.BaseStream.Position - start != (long)dataBytes)
                throw new InvalidDataException("An FBX array writer emitted a different byte count than declared.");
        });
    }

    public static FbxBinary7400Property RawFile(string path, long expectedBytes, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedBytes < 0 || expectedBytes > uint.MaxValue)
            throw new InvalidDataException("Embedded media exceeds the FBX 7400 blob size limit.");
        return new((byte)'R', checked(sizeof(uint) + expectedBytes), (writer, token) =>
        {
            writer.Write((uint)expectedBytes);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            if (input.Length != expectedBytes)
                throw new InvalidDataException("Staged embedded media changed length before serialization.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long remaining = expectedBytes;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new EndOfStreamException("Staged embedded media ended before its declared size.");
                hash.AppendData(buffer, 0, read);
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
            if (input.ReadByte() != -1)
                throw new InvalidDataException("Staged embedded media grew during serialization.");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(actual, expectedSha256))
                throw new InvalidDataException("Staged embedded media hash changed before serialization.");
        });
    }

    public static FbxBinary7400Property RawBytes(ReadOnlyMemory<byte> bytes)
    {
        return new((byte)'R', checked(sizeof(uint) + bytes.Length), (writer, _) =>
        {
            writer.Write((uint)bytes.Length);
            writer.Write(bytes.Span);
        });
    }

    internal void Write(BinaryWriter writer, CancellationToken token)
    {
        writer.Write(TypeCode);
        var start = writer.BaseStream.Position;
        _writePayload(writer, token);
        if (writer.BaseStream.Position - start != PayloadBytes)
            throw new InvalidDataException("An FBX property writer emitted a different byte count than declared.");
    }
}

internal static class FbxBinary7400Writer
{
    private const int NodeHeaderBytes = 13;
    private const int NullRecordBytes = 13;
    private const int FooterBytes = 128;
    private const uint Version = 7400;
    private const long MaximumNodes = 2_000_000;
    private static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");
    private static readonly byte[] FooterFileIdKey = [0x18,0xb3,0x1a,0xea,0x86,0x24,0xfc,0xc3,0x8e,0xc9,0x80,0x23,0x97,0x25,0xc2,0xff];
    private static readonly byte[] FooterMagicKey = [0xfa,0xbc,0x9b,0x09,0xd0,0xc9,0xe4,0x67,0xb1,0x76,0xca,0x82,0x1d,0xff,0x19,0x78];
    private static readonly byte[] FooterTrailer = [0xf8,0x5a,0x8c,0x6a,0xde,0xf5,0xd9,0x7e,0xec,0xe9,0x0c,0xe3,0x75,0x8f,0x29,0x0b];
    private static readonly byte[] MagicOrder = [0x11,0x12,0x05,0x06,0x0b,0x0c,0x08,0x09,0x14,0x15,0x00,0x01,0x02,0x03,0x0e,0x0f];

    public static byte[] CreateFileId(string creationTime) => CreateChainedMagic(creationTime, FooterFileIdKey, footer: false);

    public static string FormatCreationTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss:fff", CultureInfo.InvariantCulture);

    public static void Write(Stream output, IReadOnlyList<FbxBinary7400Node> roots, string creationTime,
        long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(creationTime);
        if (!output.CanWrite || !output.CanSeek) throw new ArgumentException("The FBX 7400 output must be writable and seekable.", nameof(output));
        if (output.Position != 0) throw new ArgumentException("The FBX 7400 output must start at offset zero.", nameof(output));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ValidateCreationTime(creationTime);

        long nodeCount = 0;
        long rootBytes = 0;
        foreach (var root in roots)
        {
            rootBytes = checked(rootBytes + Measure(root, 0, ref nodeCount));
            if (rootBytes > uint.MaxValue) throw new InvalidDataException("FBX root nodes exceed the 7400 offset limit.");
        }
        if (nodeCount > MaximumNodes) throw new InvalidDataException("FBX binary document exceeds the node-count limit.");
        var afterRootTerminator = checked(HeaderMagic.Length + sizeof(uint) + rootBytes + NullRecordBytes);
        var afterFooterMagic = checked(afterRootTerminator + 16);
        var alignment = 16 - afterFooterMagic % 16;
        var totalBytes = checked(afterFooterMagic + alignment + FooterBytes + FooterTrailer.Length);
        if (totalBytes > maximumBytes || totalBytes > uint.MaxValue)
            throw new InvalidDataException("FBX binary output exceeds the configured 7400 file-size limit.");

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(HeaderMagic);
        writer.Write(Version);
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteNode(writer, root, cancellationToken);
        }
        WriteFooter(writer, creationTime);
        writer.Flush();
        if (output.Position != totalBytes)
            throw new InvalidDataException("FBX binary output size differed from its checked preflight length.");
    }

    private static long Measure(FbxBinary7400Node node, int depth, ref long nodeCount)
    {
        if (depth > 64) throw new InvalidDataException("FBX binary document nesting exceeds the writer limit.");
        nodeCount = checked(nodeCount + 1);
        long size = checked(NodeHeaderBytes + node.Name.Length + node.PropertyBytes + NullRecordBytes);
        foreach (var child in node.Children) size = checked(size + Measure(child, depth + 1, ref nodeCount));
        return size;
    }

    private static void WriteNode(BinaryWriter writer, FbxBinary7400Node node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = writer.BaseStream.Position;
        var end = checked(start + node.SerializedBytes);
        if (end > uint.MaxValue) throw new InvalidDataException("FBX node end offset exceeds the 7400 limit.");
        writer.Write((uint)end);
        writer.Write((uint)node.Properties.Count);
        writer.Write(node.PropertyBytes);
        writer.Write((byte)node.Name.Length);
        writer.Write(node.Name);
        foreach (var property in node.Properties)
        {
            token.ThrowIfCancellationRequested();
            property.Write(writer, token);
        }
        foreach (var child in node.Children) WriteNode(writer, child, token);
        WriteZeros(writer, NullRecordBytes);
        if (writer.BaseStream.Position != end)
            throw new InvalidDataException("FBX node ended at an offset different from its checked header.");
    }

    private static void WriteFooter(BinaryWriter writer, string creationTime)
    {
        WriteZeros(writer, NullRecordBytes);
        writer.Write(CreateChainedMagic(creationTime, FooterMagicKey, footer: true));
        var alignment = 16 - (int)(writer.BaseStream.Position % 16);
        WriteZeros(writer, alignment);
        writer.Write(0u);
        writer.Write(Version);
        for (var index = 2; index < FooterBytes / sizeof(uint); index++) writer.Write(0u);
        writer.Write(FooterTrailer);
    }

    private static byte[] CreateChainedMagic(string creationTime, byte[] key, bool footer)
    {
        var time = Encoding.ASCII.GetBytes(creationTime);
        var magic = new byte[16];
        for (var index = 0; index < magic.Length; index++) magic[index] = time[MagicOrder[index]];
        if (footer)
        {
            for (var index = 2; index < magic.Length; index++) magic[index] ^= magic[index - 2];
            for (var index = 0; index < magic.Length; index++) magic[index] ^= time[MagicOrder[index]];
        }
        for (var index = 1; index < magic.Length; index++) magic[index] ^= magic[index - 1];
        for (var index = 0; index < magic.Length; index++) magic[index] ^= key[index];
        return magic;
    }

    private static void ValidateCreationTime(string creationTime)
    {
        if (creationTime.Length != 23 || creationTime.Any(character => character > 0x7f) ||
            !DateTime.TryParseExact(creationTime, "yyyy-MM-dd HH:mm:ss:fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new InvalidDataException("FBX binary creation time must use invariant yyyy-MM-dd HH:mm:ss:fff form.");
    }

    private static void WriteZeros(BinaryWriter writer, int count)
    {
        Span<byte> zeros = stackalloc byte[32];
        zeros.Clear();
        while (count > 0)
        {
            var current = Math.Min(count, zeros.Length);
            writer.Write(zeros[..current]);
            count -= current;
        }
    }
}
