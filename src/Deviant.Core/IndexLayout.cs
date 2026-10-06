using System.Buffers.Binary;
using System.Globalization;

namespace Deviant.Core;

public static class IndexLayout
{
    public const int HeaderLength = 105;
    public const int RecordLength = 28;
    public const uint SupportedVersion = 18;
    public const string ParserVersion = "deviant-pc-v18-1";
    public static ReadOnlySpan<byte> Magic => "QUANTICDREAMTABINDEX"u8;
    public static string VolumeName(string stem, uint package) =>
        stem + (package == 0 ? ".dat" : ".d" + package.ToString("D2", CultureInfo.InvariantCulture));
    public static ResourceRecord ReadRecord(ReadOnlySpan<byte> bytes, long ordinal, string stem)
    {
        if (bytes.Length != RecordLength) throw new ArchiveException("TruncatedRecord", "An index record is truncated.");
        var package = BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]);
        return new(ordinal, BinaryPrimitives.ReadUInt32BigEndian(bytes),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]),
            package, BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]),
            VolumeName(stem, package));
    }
    public static bool SameStoredRecord(ResourceRecord a, ResourceRecord b) =>
        a.Ordinal == b.Ordinal && a.Type == b.Type && a.Flags == b.Flags && a.Id == b.Id && a.Package == b.Package &&
        a.Offset == b.Offset && a.StoredSize == b.StoredSize && a.AlternativeSize == b.AlternativeSize;
    public static bool InBounds(long offset, long length, long sourceLength) =>
        offset >= 0 && length >= 0 && sourceLength >= 0 && offset <= sourceLength && length <= sourceLength - offset;
}
