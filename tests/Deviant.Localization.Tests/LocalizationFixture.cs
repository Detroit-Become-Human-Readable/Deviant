using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Core;

namespace Deviant.Localization.Tests;

internal sealed class LocalizationFixture : IDisposable
{
    private readonly List<byte[]> resources = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantLocalizationTests", Guid.NewGuid().ToString("N"));
    public string IndexPath => Path.Combine(Root, "strings.idx");
    public string VolumePath => Path.Combine(Root, "strings.dat");
    public string CachePath { get; }

    public LocalizationFixture()
    {
        Directory.CreateDirectory(Root);
        CachePath = Path.Combine(Path.GetDirectoryName(Root)!, "deviant-localization-cache-" + Guid.NewGuid().ToString("N"));
    }

    public void AddResource(byte[] bytes) => resources.Add(bytes);

    public async Task<ArchiveSnapshot> BuildAsync()
    {
        var records = new List<byte[]>();
        using var data = new MemoryStream();
        for (var index = 0; index < resources.Count; index++)
        {
            var content = resources[index];
            var row = new byte[IndexLayout.RecordLength];
            WriteBigEndian(row, 0, 1016); WriteBigEndian(row, 4, 1); WriteBigEndian(row, 8, checked((uint)(index + 1)));
            WriteBigEndian(row, 12, checked((uint)data.Position)); WriteBigEndian(row, 16, checked((uint)content.Length));
            WriteBigEndian(row, 20, 0); WriteBigEndian(row, 24, 0);
            records.Add(row); data.Write(content);
        }
        await File.WriteAllBytesAsync(VolumePath, data.ToArray());
        var indexBytes = new byte[IndexLayout.HeaderLength + records.Count * IndexLayout.RecordLength];
        IndexLayout.Magic.CopyTo(indexBytes);
        WriteBigEndian(indexBytes, 20, IndexLayout.SupportedVersion);
        WriteBigEndian(indexBytes, 24, 0);
        WriteBigEndian(indexBytes, 101, checked((uint)records.Count));
        for (var row = 0; row < records.Count; row++) records[row].CopyTo(indexBytes, IndexLayout.HeaderLength + row * IndexLayout.RecordLength);
        await File.WriteAllBytesAsync(IndexPath, indexBytes);
        return await new ArchiveReader().OpenAsync(IndexPath);
    }

    public static byte[] Wrap(byte[] localizedPayload, int referenceCount = 0)
    {
        using var stream = new MemoryStream();
        stream.Write("COM_CONT"u8);
        WriteLittleEndian(stream, 6);
        WriteLittleEndian(stream, checked((uint)(4 + referenceCount * 9)));
        WriteLittleEndian(stream, checked((uint)referenceCount));
        for (var index = 0; index < referenceCount; index++)
        {
            WriteLittleEndian(stream, 1022); WriteLittleEndian(stream, checked((uint)(index + 1))); stream.WriteByte(1);
        }
        stream.Write(localizedPayload);
        return stream.ToArray();
    }

    public static byte[] MakeLocalization(uint version, params (string Language, byte[][] Records)[] languages)
    {
        using var body = new MemoryStream();
        foreach (var (language, records) in languages)
        {
            WriteLittleEndian(body, 0x00000301); body.WriteByte(0);
            body.Write(Encoding.ASCII.GetBytes(language));
            foreach (var record in records) body.Write(record);
        }
        using var payload = new MemoryStream();
        payload.Write("LOCALIZ_"u8);
        WriteLittleEndian(payload, version);
        // The declared span starts immediately after the size field and includes the
        // reserved byte (v5) plus language count before the body.
        WriteLittleEndian(payload, checked((uint)(body.Length + (version == 5 ? 5 : 4))));
        if (version == 5) payload.WriteByte(0);
        WriteLittleEndian(payload, checked((uint)languages.Length));
        body.Position = 0; body.CopyTo(payload);
        return payload.ToArray();
    }

    public static byte[] DirectText(string key, string text, uint flags = 1) =>
        DirectTextBytes(key, Encoding.Unicode.GetBytes(text), flags);

    public static byte[] DirectTextBytes(string key, byte[] utf16Bytes, uint flags = 1)
    {
        using var stream = new MemoryStream();
        WriteLittleEndian(stream, flags);
        var keyBytes = Encoding.ASCII.GetBytes(key); WriteLittleEndian(stream, checked((uint)keyBytes.Length)); stream.Write(keyBytes);
        WriteLittleEndian(stream, checked((uint)utf16Bytes.Length)); stream.Write(utf16Bytes);
        return stream.ToArray();
    }

    public static byte[] PointerRecord(params (string Key, byte Kind, byte[] Payload)[] pointers)
    {
        using var stream = new MemoryStream();
        WriteLittleEndian(stream, 1); WriteLittleEndian(stream, checked((uint)pointers.Length));
        foreach (var (key, kind, payload) in pointers)
        {
            var keyBytes = Encoding.ASCII.GetBytes(key);
            WriteLittleEndian(stream, checked((uint)keyBytes.Length)); stream.Write(keyBytes);
            stream.WriteByte(kind); stream.Write(payload);
        }
        return stream.ToArray();
    }

    public static byte[] UnknownRecord(uint flags, uint keyLength, byte[] tail)
    {
        using var stream = new MemoryStream();
        WriteLittleEndian(stream, flags); WriteLittleEndian(stream, keyLength); stream.Write(tail);
        return stream.ToArray();
    }

    public static int FindSequence(byte[] bytes, ReadOnlySpan<byte> sequence) => bytes.AsSpan().IndexOf(sequence);

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
    private static void WriteLittleEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buffer, value); stream.Write(buffer);
    }
}
