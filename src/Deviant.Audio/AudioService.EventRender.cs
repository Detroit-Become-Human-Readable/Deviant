using System.Buffers.Binary;
using System.Numerics;

namespace Deviant.Audio;

public sealed partial class AudioService
{
    private async Task CaptureEventWaveAsync(IReadOnlyList<string> arguments, Stream output, WavFormat format,
        string workspace, TimeSpan timeout, string failure, CancellationToken token)
    {
        var args = new List<string>(arguments);
        args.AddRange(["-rf64", "never", "-write_bext", "0", "-write_peak", "off", "-f", "wav", "pipe:1"]);
        var result = await _processes.RunBinaryAsync(new ProcessRequest(paths.Ffmpeg!, args, workspace,
            Timeout: timeout), output, _limits.MaximumWavFileBytes, token).ConfigureAwait(false);
        EnsureSuccess(result, failure);
        token.ThrowIfCancellationRequested();
        NormalizeEventWaveCapture(output, format);
        var metadata = ReadWavMetadata(output);
        EnsureWav(metadata);
        EnsureWavFormat(metadata, format);
    }

    // The bundled WAV muxer cannot seek its stdout: RIFF/data sizes remain unknown,
    // no fact chunk is emitted, and its seekable trailer does not add an odd-byte pad.
    // Interpret only this fully drained, successful PCM capture, never arbitrary WAV input.
    private void NormalizeEventWaveCapture(Stream stream, WavFormat expected)
    {
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("Event WAV capture requires a readable, writable, seekable stream.", nameof(stream));
        var savedPosition = stream.Position;
        try
        {
            var length = stream.Length;
            if (length < 44 || length > _limits.MaximumWavFileBytes || length > (long)uint.MaxValue + 8)
                throw InvalidEventCapture("container length");
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            ReadExactly(stream, header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw InvalidEventCapture("container signature");
            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (riffSize != uint.MaxValue && riffSize != length - 8)
                throw InvalidEventCapture("RIFF size");

            bool hasFormat = false, hasData = false, unknownDataSize = false;
            int blockAlign = 0, chunks = 0;
            long cursor = 12, headerBytes = 12, dataBytes = 0, dataSizeOffset = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> listKind = stackalloc byte[4];
            while (cursor < length)
            {
                if (++chunks > 64 || length - cursor < 8) throw InvalidEventCapture("chunk framing");
                headerBytes += 8;
                if (headerBytes > 1024 * 1024) throw InvalidEventCapture("header limit");
                stream.Position = cursor; ReadExactly(stream, chunk);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var start = cursor + 8;
                if (chunk[..4].SequenceEqual("data"u8))
                {
                    if (!hasFormat || hasData) throw InvalidEventCapture("data chunk order");
                    hasData = true; unknownDataSize = size == uint.MaxValue;
                    dataSizeOffset = cursor + 4;
                    dataBytes = unknownDataSize ? length - start : size;
                    if (dataBytes < 1 || dataBytes > length - start || dataBytes % blockAlign != 0)
                        throw InvalidEventCapture("PCM sample alignment");
                    cursor = start + dataBytes;
                    if (!unknownDataSize && (dataBytes & 1) != 0 && cursor < length)
                    {
                        stream.Position = cursor;
                        if (stream.ReadByte() != 0) throw InvalidEventCapture("data padding");
                        cursor++;
                    }
                    if (unknownDataSize) break;
                    continue;
                }

                long paddedSize = size + (long)(size & 1);
                if (paddedSize > length - start) throw InvalidEventCapture("truncated metadata chunk");
                headerBytes += paddedSize;
                if (headerBytes > 1024 * 1024) throw InvalidEventCapture("header limit");
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (hasFormat || hasData) throw InvalidEventCapture("format chunk order");
                    blockAlign = ReadEventPcmFormat(stream, size, expected);
                    hasFormat = true;
                }
                else if (chunk[..4].SequenceEqual("LIST"u8))
                {
                    if (hasData || size < 4) throw InvalidEventCapture("LIST framing");
                    ReadExactly(stream, listKind);
                    if (!listKind.SequenceEqual("INFO"u8)) throw InvalidEventCapture("LIST type");
                }
                else if (!chunk[..4].SequenceEqual("JUNK"u8) || hasData)
                    throw InvalidEventCapture("chunk type");
                cursor = start + paddedSize;
            }
            if (!hasFormat || !hasData || cursor != length)
                throw InvalidEventCapture("missing or truncated PCM data");

            // Validate framing and the complete sample extent before making any header change.
            var appendPadding = unknownDataSize && (dataBytes & 1) != 0;
            var normalizedLength = checked(length + (appendPadding ? 1 : 0));
            if (normalizedLength > _limits.MaximumWavFileBytes || normalizedLength - 8 > uint.MaxValue)
                throw InvalidEventCapture("aligned container length");
            if (appendPadding) { stream.Position = length; stream.WriteByte(0); }
            Span<byte> value = stackalloc byte[4];
            if (riffSize == uint.MaxValue || appendPadding)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)(normalizedLength - 8)));
                stream.Position = 4; stream.Write(value);
            }
            if (unknownDataSize)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, checked((uint)dataBytes));
                stream.Position = dataSizeOffset; stream.Write(value);
            }
        }
        catch (EndOfStreamException error)
        { throw new InvalidDataException("The event WAV capture header is truncated.", error); }
        finally { stream.Position = savedPosition; }
    }

    private static int ReadEventPcmFormat(Stream source, uint length, WavFormat expected)
    {
        if (length is not (16 or 18 or 40)) throw InvalidEventCapture("PCM format length");
        Span<byte> format = stackalloc byte[40]; ReadExactly(source, format[..(int)length]);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        ushort alignment = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        ushort precision = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        int expectedBits = expected switch
        { WavFormat.Pcm16 => 16, WavFormat.Pcm24 => 24, WavFormat.Float32 => 32, _ => throw new ArgumentOutOfRangeException(nameof(expected)) };
        ushort expectedTag = expected == WavFormat.Float32 ? (ushort)3 : (ushort)1;
        if (channels == 0 || sampleRate is 0 or > int.MaxValue || precision != expectedBits ||
            alignment != channels * (expectedBits / 8) || sampleRate * (long)alignment > uint.MaxValue ||
            byteRate != sampleRate * (long)alignment)
            throw InvalidEventCapture("PCM format");
        if (tag == expectedTag)
        {
            if (length == 40 || length == 18 && BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) != 0)
                throw InvalidEventCapture("PCM format extension");
        }
        else if (tag == 0xfffe)
        {
            var expectedGuid = expected == WavFormat.Float32 ? new Guid("00000003-0000-0010-8000-00aa00389b71") :
                new Guid("00000001-0000-0010-8000-00aa00389b71");
            if (length != 40 || BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) != 22 ||
                BinaryPrimitives.ReadUInt16LittleEndian(format[18..]) != expectedBits || new Guid(format.Slice(24, 16)) != expectedGuid)
                throw InvalidEventCapture("extensible PCM format");
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(format[20..]);
            if (mask != 0 && BitOperations.PopCount(mask) != channels) throw InvalidEventCapture("channel mask");
        }
        else throw InvalidEventCapture("PCM format tag");
        return alignment;
    }

    private static InvalidDataException InvalidEventCapture(string detail) => new("The event WAV capture has invalid " + detail + ".");

    private static void RetagEventWaveCapture(Stream stream, AudioWavMetadata metadata, int sampleRate)
    {
        var savedPosition = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12]; ReadExactly(stream, header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                throw new AudioToolException("The event decoder created an unsupported WAV container for pitch processing.");
            var riffLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8;
            if (riffLength > stream.Length || riffLength < 12)
                throw new AudioToolException("The event decoder created a malformed WAV container for pitch processing.");
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> format = stackalloc byte[16];
            Span<byte> updated = stackalloc byte[8];
            long position = 12;
            while (position <= riffLength - chunk.Length)
            {
                stream.Position = position; ReadExactly(stream, chunk);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var payload = checked(position + chunk.Length);
                if (size > riffLength - payload)
                    throw new AudioToolException("The event decoder created a WAV chunk outside its RIFF container.");
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16) throw new AudioToolException("The event decoder created a truncated WAV format chunk.");
                    ReadExactly(stream, format);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(format[4..8]) != metadata.SampleRate ||
                        BinaryPrimitives.ReadUInt16LittleEndian(format[12..14]) != metadata.BlockAlign || metadata.BlockAlign == 0)
                        throw new AudioToolException("The WAV format changed before pitch processing.");
                    var byteRate = (ulong)sampleRate * (uint)metadata.BlockAlign;
                    if (byteRate > uint.MaxValue) throw new AudioToolException("The requested pitch shift exceeds the WAV sample-rate limit.");
                    BinaryPrimitives.WriteUInt32LittleEndian(updated[..4], checked((uint)sampleRate));
                    BinaryPrimitives.WriteUInt32LittleEndian(updated[4..], checked((uint)byteRate));
                    stream.Position = payload + 4; stream.Write(updated); stream.Flush();
                    return;
                }
                position = checked(payload + size + (size & 1));
            }
            throw new AudioToolException("The event decoder created a WAV file without a format chunk.");
        }
        finally { stream.Position = savedPosition; }
    }
}
