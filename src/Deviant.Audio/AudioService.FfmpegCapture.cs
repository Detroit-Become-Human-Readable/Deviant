using System.Buffers.Binary;
using System.Numerics;

namespace Deviant.Audio;

public sealed partial class AudioService
{
    // Only call after a successful, fully drained FFmpeg stdout capture using the fixed PCM16 WAV flags.
    // Unknown-size data consumes the remaining capture; its bytes are never searched for chunk markers.
    private static void NormalizeSuccessfulFfmpegWaveCapture(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("WAV capture normalization requires a readable, writable, seekable stream.", nameof(stream));
        long position = stream.Position;
        try
        {
            const long maximumHeaderBytes = 1024 * 1024;
            const int maximumChunks = 64;
            long length = stream.Length;
            if (length < 44 || length > (long)uint.MaxValue + 8) throw InvalidCapture("container length");
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            ReadExactly(stream, header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw InvalidCapture("container signature");
            uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (riffSize != uint.MaxValue && riffSize != length - 8) throw InvalidCapture("RIFF size");

            bool hasFormat = false, hasData = false, unknownDataSize = false;
            int blockAlign = 0, chunks = 0;
            long cursor = 12, headerBytes = 12, dataSizeOffset = 0, dataBytes = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> listKind = stackalloc byte[4];
            while (cursor < length)
            {
                if (++chunks > maximumChunks || length - cursor < 8) throw InvalidCapture("chunk framing");
                headerBytes += 8;
                if (headerBytes > maximumHeaderBytes) throw InvalidCapture("header limit");
                stream.Position = cursor; ReadExactly(stream, chunk);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                long start = cursor + 8;
                if (chunk[..4].SequenceEqual("data"u8))
                {
                    if (!hasFormat || hasData) throw InvalidCapture("data chunk order");
                    hasData = true; unknownDataSize = size == uint.MaxValue;
                    dataSizeOffset = cursor + 4;
                    dataBytes = unknownDataSize ? length - start : size;
                    if (dataBytes < 1 || dataBytes > length - start || dataBytes % blockAlign != 0)
                        throw InvalidCapture("PCM sample alignment");
                    cursor = start + dataBytes;
                    if (unknownDataSize) break;
                    continue;
                }

                long paddedSize = size + (long)(size & 1);
                if (paddedSize > length - start) throw InvalidCapture("truncated metadata chunk");
                headerBytes += paddedSize;
                if (headerBytes > maximumHeaderBytes) throw InvalidCapture("header limit");
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (hasFormat || hasData) throw InvalidCapture("format chunk order");
                    blockAlign = ReadPcm16Format(stream, size);
                    hasFormat = true;
                }
                else if (chunk[..4].SequenceEqual("LIST"u8))
                {
                    if (size < 4) throw InvalidCapture("LIST framing");
                    ReadExactly(stream, listKind);
                    if (!listKind.SequenceEqual("INFO"u8)) throw InvalidCapture("LIST type");
                }
                else if (!chunk[..4].SequenceEqual("JUNK"u8)) throw InvalidCapture("chunk type");
                cursor = start + paddedSize;
            }
            if (!hasFormat || !hasData || cursor != length) throw InvalidCapture("missing or truncated PCM data");

            // All framing and format checks precede mutation. Finite canonical sizes remain untouched.
            Span<byte> value = stackalloc byte[4];
            if (riffSize == uint.MaxValue)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)(length - 8)));
                stream.Position = 4; stream.Write(value);
            }
            if (unknownDataSize)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)dataBytes));
                stream.Position = dataSizeOffset; stream.Write(value);
            }
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("The captured WAV header is truncated.", error); }
        finally { stream.Position = position; }

        static InvalidDataException InvalidCapture(string detail) => new("The captured WAV has invalid " + detail + ".");

        static int ReadPcm16Format(Stream source, uint length)
        {
            if (length is not (16 or 18 or 40)) throw InvalidCapture("PCM format length");
            Span<byte> format = stackalloc byte[40]; ReadExactly(source, format[..(int)length]);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
            uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
            uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
            ushort alignment = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
            ushort precision = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
            if (channels == 0 || sampleRate is 0 or > int.MaxValue || precision != 16 || alignment != channels * 2 ||
                sampleRate * (long)alignment > uint.MaxValue || byteRate != sampleRate * (long)alignment)
                throw InvalidCapture("PCM16 format");
            if (tag == 1)
            {
                if (length == 40 || length == 18 && BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) != 0)
                    throw InvalidCapture("PCM format extension");
            }
            else if (tag == 0xfffe)
            {
                if (length != 40 || BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) != 22 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(format[18..]) != 16 ||
                    new Guid(format.Slice(24, 16)) != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                    throw InvalidCapture("extensible PCM16 format");
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(format[20..]);
                if (mask != 0 && BitOperations.PopCount(mask) != channels) throw InvalidCapture("channel mask");
            }
            else throw InvalidCapture("PCM format tag");
            return alignment;
        }
    }
}
