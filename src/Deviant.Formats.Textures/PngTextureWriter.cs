using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Deviant.Formats.Textures;

/// <summary>Writes an 8-bit RGBA texture surface as a single-image PNG.</summary>
public static class PngTextureWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const long MaximumPixels = 64L * 1024 * 1024;

    public static void Write(Stream destination, TextureSurface surface, bool? isSrgb = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(surface);
        if (!destination.CanWrite) throw new ArgumentException("The PNG destination is not writable.", nameof(destination));
        if (surface.IsHdr) throw new TextureFormatException("HdrPngRequiresToneMap", "HDR pixels require an explicit tone-mapping choice before PNG export.");
        if (surface.Width <= 0 || surface.Height <= 0 || (long)surface.Width * surface.Height > MaximumPixels)
            throw new TextureFormatException("InvalidDimensions", "The PNG dimensions are outside the supported range.");
        var expectedLength = checked((long)surface.Width * surface.Height * 4);
        if (surface.Rgba8.Length != expectedLength)
            throw new TextureFormatException("InvalidPixelExtent", "The RGBA pixel buffer length does not match its dimensions.");

        destination.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)surface.Width));
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)surface.Height));
        header[8] = 8;  // Bit depth.
        header[9] = 6;  // RGBA.
        header[10] = 0; // Compression method.
        header[11] = 0; // Filter method.
        header[12] = 0; // No interlace.
        WriteChunk(destination, "IHDR"u8, header);
        if (isSrgb ?? surface.IsSrgb)
            WriteChunk(destination, "sRGB"u8, [0]);
        else
        {
            Span<byte> gamma = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(gamma, 100_000);
            WriteChunk(destination, "gAMA"u8, gamma);
        }

        using (var idat = new IdatChunkStream(destination))
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            {
                var rowLength = checked(surface.Width * 4);
                var pixels = surface.Rgba8.Span;
                var row = new byte[checked(rowLength + 1)];
                for (var y = 0; y < surface.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    row[0] = 0; // PNG filter None.
                    pixels.Slice(y * rowLength, rowLength).CopyTo(row.AsSpan(1));
                    zlib.Write(row);
                }
            }
            idat.Complete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteChunk(destination, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, checked((uint)data.Length));
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(type, data));
        stream.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in type) crc = UpdateCrc(crc, value);
        foreach (var value in data) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB8_8320u);
        return crc;
    }

    private sealed class IdatChunkStream(Stream destination) : Stream
    {
        private const int ChunkBufferLength = 64 * 1024;
        private readonly byte[] buffer = new byte[ChunkBufferLength];
        private int buffered;
        private bool complete;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !complete;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => FlushChunk();
        public override int Read(byte[] output, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            if (complete) throw new ObjectDisposedException(nameof(IdatChunkStream));
            while (!source.IsEmpty)
            {
                var count = Math.Min(buffer.Length - buffered, source.Length);
                source[..count].CopyTo(buffer.AsSpan(buffered));
                source = source[count..];
                buffered += count;
                if (buffered == buffer.Length) FlushChunk();
            }
        }

        public void Complete()
        {
            if (complete) return;
            FlushChunk();
            complete = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Complete();
            base.Dispose(disposing);
        }

        private void FlushChunk()
        {
            if (buffered == 0) return;
            WriteChunk(destination, "IDAT"u8, buffer.AsSpan(0, buffered));
            buffered = 0;
        }
    }
}
