using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;

namespace Deviant.Texture.Tests;

internal sealed record NativeScalarSrgbFixture(NativeTextureSource Source, byte[][][] Planes)
{
    internal static NativeScalarSrgbFixture Create(bool cube = false, bool split = false, bool srgb = true, bool alternating = false)
    {
        const string index = "generated-scalar-sampling"; const int width = 16, mips = 5;
        var revision = new SourceRevision(index, "generated-source");
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        int faces = cube ? 6 : 1;
        var planes = Enumerable.Range(0, faces).Select(face => Enumerable.Range(0, mips).Select(mip =>
            Enumerable.Range(0, (width >> mip) * (width >> mip)).Select(pixel => alternating
                ? (byte)((pixel & 1) == 0 ? 0 : 255) : (byte)(pixel + face * 37 + mip * 53)).ToArray()).ToArray()).ToArray();
        int firstPackedMip = split && !cube ? 2 : 0;
        using var packed = new MemoryStream();
        for (int mip = firstPackedMip; mip < mips; mip++) for (int face = 0; face < faces; face++) packed.Write(planes[face][mip]);
        var padded = new byte[((int)packed.Length + 255) & ~255]; packed.ToArray().CopyTo(padded, 0);
        var payloads = new List<NativeTexturePayload> { Payload(new(index, 2), 2229, 10, Wrap(padded, true)) };
        int explicitMips = !split ? 0 : cube ? mips : 2;
        for (int mip = 0; mip < explicitMips; mip++)
        {
            using var body = new MemoryStream(); for (int face = 0; face < faces; face++) body.Write(planes[face][mip]);
            payloads.Add(Payload(new(index, 3 + mip), 2230, (uint)(11 + mip), Wrap(body.ToArray(), false)));
        }
        var descriptors = new List<TextureFileTextV24LodDescriptor>();
        for (int mip = 0; mip < explicitMips; mip++) descriptors.Add(new((ushort)(width >> mip), (ushort)(width >> mip), 1, (byte)(mips - mip)));
        int tailMip = cube && split ? mips - 1 : firstPackedMip;
        descriptors.Add(new((ushort)(width >> tailMip), (ushort)(width >> tailMip), 1, (byte)(mips - tailMip)));
        var prefix = new byte[84]; prefix[0] = 11; prefix[7] = mips; prefix[12] = cube ? (byte)2 : (byte)1;
        prefix[14] = srgb ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Count);
            foreach (var descriptor in descriptors)
            { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(false); writer.Write((byte)0); writer.Write((byte)0);
            for (int face = 0; face < faces; face++) for (int mip = 0; mip < mips; mip++) writer.Write((uint)planes[face][mip].Length);
            writer.Write(2229u); writer.Write(payloads[0].Id); writer.Write((uint)padded.Length);
            for (int mip = 0; mip < explicitMips; mip++)
            { writer.Write(2230u); writer.Write(payloads[mip + 1].Id); writer.Write((uint)(planes[0][mip].Length * faces)); }
        }
        var bytes = stream.ToArray();
        var rows = payloads.Select((payload, ordinal) => new TextureCatalogReference(ordinal,
            ordinal == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type), ReferenceStatus.Unique,
            [payload.Identity], 0, new("generated.dat", ordinal * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, rows.Length * 9), rows.Length, 0, rows);
        var parsed = new TextureFileTextV24Reader().Parse(bytes, 0, bytes.Length, container, revision,
            new(container, 0, bytes.Length), [new("generated.dat", 100, 3)], membership);
        var metadata = parsed.Value ?? throw new InvalidOperationException(string.Join(";", parsed.Diagnostics));
        return new(new(payloads[0].Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]),
            metadata, bytes, Hash(bytes), payloads), planes);
    }

    private static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes)
    { string hash = Hash(bytes); return new(identity, type, id, new("generated.dat", 0, bytes.Length, hash), bytes, hash); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Wrap(byte[] decoded, bool raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded);
        var block = compressed.ToArray(); var segs = new byte[32 + block.Length];
        "segs"u8.CopyTo(segs); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), (uint)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), (uint)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20), 1); block.CopyTo(segs, 32);
        var wrapped = new byte[32 + segs.Length]; (raw ? "QZIP\0ETF_RAW_"u8 : "QZIP\0ETF_RAWL"u8).CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), (uint)segs.Length); segs.CopyTo(wrapped, 32); return wrapped;
    }
}
