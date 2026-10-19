using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Deviant.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class FfmpegWaveCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CaptureNormalizationPreservesPcmMetadataPositionAndCanonicalBytes(bool sentinel, bool extensible)
    {
        byte[] expected = Wave(false, extensible);
        using var stream = new MemoryStream(Wave(sentinel, extensible));
        stream.Position = 7;

        Normalize(stream);

        Assert.Equal(7, stream.Position); Assert.True(stream.CanRead && stream.CanWrite && stream.CanSeek);
        Assert.Equal(expected, stream.ToArray());
        var metadata = AudioService.ReadWavMetadata(stream);
        Assert.Equal(2, metadata.Channels); Assert.Equal(48000, metadata.SampleRate);
        Assert.Equal(16, metadata.BitsPerSample); Assert.Equal(16, metadata.SampleFrames);
        Assert.Equal((ushort)1, metadata.FormatCode);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("wave")]
    [InlineData("short-riff")]
    [InlineData("truncated-fmt")]
    [InlineData("truncated-data")]
    [InlineData("unaligned")]
    [InlineData("float")]
    [InlineData("precision")]
    [InlineData("block-align")]
    [InlineData("byte-rate")]
    [InlineData("duplicate-fmt")]
    [InlineData("duplicate-data")]
    [InlineData("missing-data")]
    [InlineData("empty-data")]
    [InlineData("unknown-chunk")]
    [InlineData("extensible-guid")]
    public void MalformedCapturesAreRejectedWithoutChangingTheirBytes(string defect)
    {
        var bytes = Wave(defect is not ("truncated-data" or "duplicate-data" or "missing-data"), defect == "extensible-guid");
        int data = DataOffset(bytes);
        switch (defect)
        {
            case "signature": "RF64"u8.CopyTo(bytes); break;
            case "wave": "NONE"u8.CopyTo(bytes.AsSpan(8)); break;
            case "short-riff": Put(bytes, 4, 35); break;
            case "truncated-fmt": Put(bytes, 16, 4096); break;
            case "truncated-data": Put(bytes, data - 4, (uint)(bytes.Length - data + 4)); break;
            case "unaligned": Array.Resize(ref bytes, bytes.Length - 1); break;
            case "float": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 3); break;
            case "precision": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 24); break;
            case "block-align": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2); break;
            case "byte-rate": Put(bytes, 28, 1); break;
            case "duplicate-fmt": bytes = InsertChunk(bytes, data - 8, "fmt ", bytes.AsSpan(20, 16).ToArray()); break;
            case "duplicate-data": bytes = InsertChunk(bytes, bytes.Length, "data", new byte[4]); Put(bytes, 4, (uint)(bytes.Length - 8)); break;
            case "missing-data": Array.Resize(ref bytes, data - 8); Put(bytes, 4, (uint)(bytes.Length - 8)); break;
            case "empty-data": Array.Resize(ref bytes, data); break;
            case "unknown-chunk": "XXXX"u8.CopyTo(bytes.AsSpan(12)); break;
            case "extensible-guid": bytes[44] ^= 1; break;
        }
        var expected = bytes.ToArray(); using var stream = new MemoryStream(bytes); stream.Position = 3;

        Assert.Throws<InvalidDataException>(() => Normalize(stream));

        Assert.Equal(3, stream.Position); Assert.Equal(expected, stream.ToArray());
    }

    [Fact]
    public void ChunkLikePcmBytesRemainAudioData()
    {
        var bytes = Wave(true); int data = DataOffset(bytes);
        "datafmt LISTJUNK"u8.CopyTo(bytes.AsSpan(data));
        using var stream = new MemoryStream(bytes);
        Normalize(stream);
        Assert.Equal(bytes.AsSpan(data).ToArray(), stream.ToArray().AsSpan(data).ToArray());
        Assert.Equal(16, AudioService.ReadWavMetadata(stream).SampleFrames);
    }

    [Fact]
    public void HeaderByteChunkAndContainerBoundsAreEnforced()
    {
        var oversizedHeader = InsertChunk(Wave(true), 36, "JUNK", new byte[1024 * 1024]);
        using (var stream = new MemoryStream(oversizedHeader)) Assert.Throws<InvalidDataException>(() => Normalize(stream));
        var chunks = Wave(true);
        for (int index = 0; index < 63; index++) chunks = InsertChunk(chunks, 36, "JUNK", []);
        using (var stream = new MemoryStream(chunks)) Assert.Throws<InvalidDataException>(() => Normalize(stream));
        using var oversized = new OversizedStream();
        Assert.Throws<InvalidDataException>(() => Normalize(oversized));
    }

    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [AudioToolsTheory(requireDecoder: false, requireFfmpeg: true)]
    public async Task BundledPipeCaptureMatchesIndependentSeekableDecode(int channels)
    {
        string root = Path.Combine(Path.GetTempPath(), "Deviant-ffmpeg-wave-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.wav"), reference = Path.Combine(root, "reference.wav");
            await File.WriteAllBytesAsync(source, Wave(false, channels: channels));
            var tools = AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS"));
            string[] arguments = ["-v", "error", "-xerror", "-nostdin", "-i", source, "-map", "0:a:0", "-map_metadata", "-1",
                "-c:a", "pcm_s16le", "-rf64", "never", "-write_bext", "0", "-write_peak", "off", "-f", "wav"];
            var runner = new ProcessRunner();
            var disk = await runner.RunAsync(new(tools.Ffmpeg!, [.. arguments, reference], Timeout: TimeSpan.FromSeconds(10),
                MonitoredOutputFiles: [reference], MaximumOutputFileBytes: 1024 * 1024));
            Assert.Equal(0, disk.ExitCode);
            using var capture = new MemoryStream();
            var pipe = await runner.RunBinaryAsync(new(tools.Ffmpeg!, [.. arguments, "pipe:1"], Timeout: TimeSpan.FromSeconds(10)), capture, 1024 * 1024);
            Assert.True(pipe.ExitCode == 0, $"FFmpeg exited {pipe.ExitCode}: {pipe.StandardError}");
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(capture.ToArray().AsSpan(4)));
            long position = capture.Position;

            Normalize(capture);

            Assert.Equal(position, capture.Position);
            Assert.Equal(AudioService.ReadWavMetadata(reference), AudioService.ReadWavMetadata(capture));
            Assert.Equal(Pcm(await File.ReadAllBytesAsync(reference)), Pcm(capture.ToArray()));
            Assert.Equal(channels, AudioService.ReadWavMetadata(capture).Channels);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Normalize(Stream stream)
    {
        try { typeof(AudioService).GetMethod("NormalizeSuccessfulFfmpegWaveCapture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [stream]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static byte[] Wave(bool sentinel, bool extensible = false, int channels = 2)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8); writer.Write(0u); writer.Write("WAVEfmt "u8); writer.Write(extensible ? 40u : 16u);
        writer.Write(extensible ? (ushort)0xfffe : (ushort)1); writer.Write((ushort)channels); writer.Write(48000u);
        writer.Write((uint)(48000 * channels * 2)); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        if (extensible)
        {
            writer.Write((ushort)22); writer.Write((ushort)16); writer.Write(3u);
            writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        }
        writer.Write("data"u8); writer.Write(sentinel ? uint.MaxValue : (uint)(16 * channels * 2));
        for (int sample = 0; sample < 16 * channels; sample++) writer.Write((short)(sample * 17 - 300));
        var bytes = stream.ToArray(); Put(bytes, 4, sentinel ? uint.MaxValue : (uint)(bytes.Length - 8)); return bytes;
    }

    private static int DataOffset(byte[] bytes)
    {
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            if (bytes.AsSpan(at, 4).SequenceEqual("data"u8)) return at + 8;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4)); at = checked(at + 8 + (int)size + (int)(size & 1));
        }
        throw new InvalidDataException("Generated data chunk missing.");
    }
    private static byte[] Pcm(byte[] bytes) { int at = DataOffset(bytes); return bytes.AsSpan(at, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at - 4))).ToArray(); }
    private static byte[] InsertChunk(byte[] bytes, int at, string id, byte[] payload)
    {
        using var stream = new MemoryStream(); stream.Write(bytes.AsSpan(0, at)); stream.Write(Encoding.ASCII.GetBytes(id));
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true)) { writer.Write((uint)payload.Length); writer.Write(payload); if ((payload.Length & 1) != 0) writer.Write((byte)0); }
        stream.Write(bytes.AsSpan(at)); return stream.ToArray();
    }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private sealed class OversizedStream : MemoryStream { public override long Length => (long)uint.MaxValue + 9; }
}
