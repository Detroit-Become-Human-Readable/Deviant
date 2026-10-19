using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class Ww2OggStdoutTests
{
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("conflicting-output")]
    [AudioToolsTheory(requireDecoder: false, requireFfmpeg: false)]
    public async Task StdoutModeKeepsArgumentAndInputErrorsOutOfBinaryOutput(string defect)
    {
        string root = CreateRoot();
        try
        {
            string input = Path.Combine(root, "source.wem"), unwanted = Path.Combine(root, "unwanted.ogg");
            if (defect != "missing") await File.WriteAllBytesAsync(input, new byte[64]);
            string[] arguments = defect == "conflicting-output" ? ["--stdout", input, "-o", unwanted] : ["--stdout", input];
            var tools = Tools(); using var output = new MemoryStream();
            var result = await new ProcessRunner().RunBinaryAsync(new(tools.Ww2Ogg!, arguments, root,
                Timeout: TimeSpan.FromSeconds(10)), output, 1024 * 1024);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(0, output.Length);
            Assert.NotEmpty(result.StandardError);
            Assert.False(File.Exists(unwanted));
        }
        finally { Directory.Delete(root, true); }
    }

    [AudioToolsFact(requireDecoder: false, requireFfmpeg: true)]
    public async Task GeneratedVorbisFileAndStdoutModesProduceIdenticalOggBytes()
    {
        string root = CreateRoot();
        try
        {
            var tools = Tools(); var runner = new ProcessRunner();
            string input = Path.Combine(root, "source.wem");
            string reference = Path.Combine(root, "reference.ogg");
            await File.WriteAllBytesAsync(input, await GenerateTriadAsync(root, tools));
            var original = await File.ReadAllBytesAsync(input);
            var file = await runner.RunAsync(new(tools.Ww2Ogg!, [input, "-o", reference], root,
                Timeout: TimeSpan.FromSeconds(10), MonitoredOutputFiles: [reference], MaximumOutputFileBytes: 1024 * 1024));
            Assert.Equal(0, file.ExitCode);
            Assert.Contains("Audiokinetic", file.StandardOutput);
            using var stdout = new MemoryStream();

            var pipe = await runner.RunBinaryAsync(new(tools.Ww2Ogg!, ["--stdout", input], root,
                Timeout: TimeSpan.FromSeconds(10)), stdout, 1024 * 1024);

            Assert.Equal(0, pipe.ExitCode);
            Assert.True(stdout.ToArray().AsSpan(0, 4).SequenceEqual("OggS"u8));
            Assert.Equal(await File.ReadAllBytesAsync(reference), stdout.ToArray());
            Assert.Contains("Audiokinetic", pipe.StandardError);
            Assert.Contains("RIFF WAVE", pipe.StandardError);
            Assert.Equal(original, await File.ReadAllBytesAsync(input));
            Assert.Equal(new[] { "reference.ogg", "source.ogg", "source.wav", "source.wem" },
                Directory.EnumerateFiles(root).Select(Path.GetFileName).Order().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task SessionRemuxPreservesFramesAndRemovesItsAdmittedScratch()
    {
        string root = CreateRoot();
        try
        {
            var tools = Tools(); Assert.NotNull(tools.Revorb);
            using var fixture = FormatsFixtureBuilder.CreateEmpty();
            fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Remux_ENG", await GenerateTriadAsync(root, tools)));
            fixture.WriteIndex();
            using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
            var aliases = new AliasStore(Path.Combine(root, "library"));
            await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools);
            var selected = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
            string outputRoot = Path.Combine(root, "exports");

            var report = await session.ExportAsync(session.Review([selected]), new(outputRoot, AudioExportFormat.Ogg));

            var entry = Assert.Single(report.Manifest.Entries);
            Assert.True(entry.Status == AudioExportStatus.Completed, entry.ErrorCode + ": " + entry.Diagnostic);
            Assert.Equal(AudioOperation.WwiseVorbisRemux, entry.Operation);
            Assert.Equal(48000, entry.OutputSampleRate); Assert.Equal(1, entry.OutputChannels);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(outputRoot, entry.RelativeOutput));
            Assert.Equal(4096, FinalGranule(bytes));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Sha256);
            Assert.Empty(Directory.EnumerateFiles(cache.Location, "*.wav", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(cache.Location, "*.ogg", SearchOption.AllDirectories));
            Assert.Equal(0, cache.ActivePreviewLeases);
        }
        finally { Directory.Delete(root, true); }
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task SessionRemuxRefusesOutputGrowthAfterItsInputAndPcmAreAdmitted()
    {
        string root = CreateRoot();
        try
        {
            var tools = Tools(); Assert.NotNull(tools.Revorb);
            using var fixture = FormatsFixtureBuilder.CreateEmpty();
            fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Remux_ENG", await GenerateTriadAsync(root, tools)));
            fixture.WriteIndex();
            using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
            var aliases = new AliasStore(Path.Combine(root, "library"));
            await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools);
            var selected = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
            string outputRoot = Path.Combine(root, "exports"); bool constrained = false; long limit = 0;
            var report = await session.ExportAsync(session.Review([selected]), new(outputRoot, AudioExportFormat.Ogg), new Callback(value =>
            {
                if (value.Phase != "Reconstructing Wwise Vorbis" || constrained) return;
                Assert.NotEmpty(Directory.EnumerateFiles(cache.Location, "*.wem", SearchOption.AllDirectories));
                Assert.NotEmpty(Directory.EnumerateFiles(cache.Location, "*.wav", SearchOption.AllDirectories));
                constrained = true;
                limit = Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) + 64;
                cache.SetLimitAsync(limit).GetAwaiter().GetResult();
            }));

            Assert.True(constrained);
            var entry = Assert.Single(report.Manifest.Entries);
            Assert.Equal(AudioExportStatus.Failed, entry.Status); Assert.Equal("CacheStorageLimit", entry.ErrorCode);
            Assert.Empty(Directory.EnumerateFiles(outputRoot, "*.ogg", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(cache.Location, "*.wav", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(cache.Location, "*.ogg", SearchOption.AllDirectories));
            Assert.True(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= limit);
            Assert.Equal(0, cache.ActivePreviewLeases);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<byte[]> GenerateTriadAsync(string root, AudioToolPaths tools)
    {
        string wave = Path.Combine(root, "source.wav"), ogg = Path.Combine(root, "source.ogg");
        await File.WriteAllBytesAsync(wave, PcmWave());
        var encoded = await new ProcessRunner().RunAsync(new(tools.Ffmpeg!, ["-v", "error", "-nostdin", "-i", wave,
            "-c:a", "libvorbis", "-q:a", "5", "-f", "ogg", ogg], root, Timeout: TimeSpan.FromSeconds(10),
            MonitoredOutputFiles: [ogg], MaximumOutputFileBytes: 1024 * 1024));
        Assert.Equal(0, encoded.ExitCode);
        var measured = await new ProcessRunner().RunAsync(new(tools.Ffprobe!, ["-v", "error", "-select_streams", "a:0", "-show_packets",
            "-show_entries", "packet=pts,duration", "-of", "json", ogg], root, Timeout: TimeSpan.FromSeconds(10)));
        Assert.Equal(0, measured.ExitCode);
        using var timing = JsonDocument.Parse(measured.StandardOutput);
        var granules = new List<uint>();
        foreach (var packet in timing.RootElement.GetProperty("packets").EnumerateArray())
        {
            long end = checked(packet.GetProperty("pts").GetInt64() + packet.GetProperty("duration").GetInt64());
            Assert.InRange(end, 0L, uint.MaxValue);
            if (granules.Count > 0) Assert.True(end >= granules[^1], "Generated Vorbis packet timing is not monotonic.");
            granules.Add((uint)end);
        }
        Assert.NotEmpty(granules); Assert.Equal(0u, granules[0]); Assert.Equal(4096u, granules[^1]);
        return WwiseTriad(await File.ReadAllBytesAsync(ogg), granules);
    }

    private static long FinalGranule(byte[] bytes)
    {
        long granule = -1;
        for (int at = 0; at < bytes.Length;)
        {
            Assert.True(bytes.AsSpan(at, 4).SequenceEqual("OggS"u8));
            int segments = bytes[at + 26];
            if ((bytes[at + 5] & 4) != 0) granule = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 6));
            int body = 0; for (int index = 0; index < segments; index++) body += bytes[at + 27 + index];
            at += 27 + segments + body;
        }
        return granule;
    }

    private sealed class Callback(Action<AudioProgress> action) : IProgress<AudioProgress>
    { public void Report(AudioProgress value) => action(value); }

    private static AudioToolPaths Tools()
    {
        var tools = AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS"));
        Assert.NotNull(tools.Ww2Ogg); return tools;
    }
    private static string CreateRoot()
    { string root = Path.Combine(Path.GetTempPath(), "Deviant-remux-stdout-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    private static byte[] PcmWave()
    {
        const int frames = 4096;
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write("RIFF"u8); writer.Write((uint)(36 + frames * 2)); writer.Write("WAVEfmt "u8); writer.Write(16u);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(48000u); writer.Write(96000u); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write((uint)(frames * 2));
        for (int index = 0; index < frames; index++) writer.Write((short)(Math.Sin(index * .07) * 12000));
        return bytes.ToArray();
    }

    private static byte[] WwiseTriad(byte[] ogg, IReadOnlyList<uint> audioGranules)
    {
        var packets = new List<byte[]>(); using var packet = new MemoryStream();
        for (int at = 0; at < ogg.Length;)
        {
            Assert.True(ogg.AsSpan(at, 4).SequenceEqual("OggS"u8));
            int segments = ogg[at + 26], body = at + 27 + segments;
            for (int segment = 0; segment < segments; segment++)
            {
                int length = ogg[at + 27 + segment]; packet.Write(ogg.AsSpan(body, length)); body += length;
                if (length < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); }
            }
            at = body;
        }
        Assert.True(packets.Count > 3);
        Assert.Equal(new byte[] { 1, 3, 5 }, packets.Take(3).Select(bytes => bytes[0]).ToArray());
        Assert.Equal(packets.Count - 3, audioGranules.Count);
        using var data = new MemoryStream(); using (var writer = new BinaryWriter(data, Encoding.ASCII, true))
            for (int index = 0; index < packets.Count; index++)
            { writer.Write((uint)packets[index].Length); writer.Write(index < 3 ? 0u : audioGranules[index - 3]); writer.Write(packets[index]); }
        int firstAudio = packets.Take(3).Sum(bytes => 8 + bytes.Length);
        using var output = new MemoryStream(); using var riff = new BinaryWriter(output, Encoding.ASCII, true);
        riff.Write("RIFF"u8); riff.Write(0u); riff.Write("WAVEfmt "u8); riff.Write(18u);
        riff.Write((ushort)0xffff); riff.Write((ushort)1); riff.Write(48000u); riff.Write(0u);
        riff.Write((ushort)0); riff.Write((ushort)0); riff.Write((ushort)0);
        riff.Write("vorb"u8); riff.Write(40u); var vorb = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(vorb, 4096); BinaryPrimitives.WriteUInt32LittleEndian(vorb.AsSpan(28), (uint)firstAudio); riff.Write(vorb);
        riff.Write("data"u8); riff.Write((uint)data.Length); riff.Write(data.ToArray());
        var result = output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8)); return result;
    }
}
