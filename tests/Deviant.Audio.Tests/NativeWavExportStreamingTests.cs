using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Audio;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

[CollectionDefinition("Generated native decoder", DisableParallelization = true)]
public sealed class NativeDecoderFixtureCollection { }

[Collection("Generated native decoder")]
public sealed class NativeWavExportStreamingTests : IDisposable
{
    private const string ReceiptVariable = "DEVIANT_TEST_BINARY_INVOCATION";
    private const string ExitVariable = "DEVIANT_TEST_BINARY_EXIT";
    private const string LingerVariable = "DEVIANT_TEST_BINARY_LINGER_MS";
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-native-wave-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> savedEnvironment = new();
    private string ReceiptPath => Path.Combine(root, "invocation.json");
    private string Destination => Path.Combine(root, "result.wav");

    public NativeWavExportStreamingTests()
    {
        Directory.CreateDirectory(root);
        foreach (var key in new[] { ReceiptVariable, ExitVariable, LingerVariable })
        {
            savedEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, key == ReceiptVariable ? ReceiptPath : null);
        }
    }

    [Theory]
    [InlineData(WavFormat.Pcm16, (ushort)1, (ushort)16)]
    [InlineData(WavFormat.Pcm24, (ushort)1, (ushort)24)]
    [InlineData(WavFormat.Float32, (ushort)3, (ushort)32)]
    public async Task DecodeStreamsBinaryBytesToValidatedAtomicOutput(WavFormat format, ushort code, ushort bits)
    {
        var expected = Wave(code, bits);
        using var source = await Source.CreateAsync(expected);
        var original = Encoding.ASCII.GetBytes("Existing generated destination");
        await File.WriteAllBytesAsync(Destination, original);
        var sawValidation = false;
        var service = Service();
        var result = await service.DecodeToWavAsync(source.Catalog, source.Media, Destination, format,
            new ProgressAction(update =>
            {
                if (update.Phase != "Validating WAV") return;
                sawValidation = true;
                Assert.Equal(original, File.ReadAllBytes(Destination));
                Assert.Single(Directory.EnumerateFiles(root, "result.wav.*.tmp"));
            }));

        Assert.True(sawValidation);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(32, result.SampleFrames);
        Assert.Equal(1, result.Channels);
        Assert.Equal(22050, result.SampleRate);
        Assert.Equal(format, result.Format);
        Assert.Equal(AudioService.ReadWavMetadata(Destination).SampleFrames, result.SampleFrames);
        AssertPipedAndStopped();
        AssertClean(source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOutputOrPrecisionCannotReplaceTheDestination(bool malformed)
    {
        using var source = await Source.CreateAsync(malformed ? new byte[64] : Wave(1, 24));
        var original = Encoding.ASCII.GetBytes("Retain generated destination on validation error");
        await File.WriteAllBytesAsync(Destination, original);

        await Assert.ThrowsAsync<AudioToolException>(() => Service().DecodeToWavAsync(source.Catalog, source.Media, Destination));

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination));
        AssertPipedAndStopped();
        AssertClean(source);
    }

    [Fact]
    public async Task BinaryOutputLimitCannotPublishAnOversizedWave()
    {
        using var source = await Source.CreateAsync(Wave(1, 16));
        var original = Encoding.ASCII.GetBytes("Retain generated destination on output limit");
        await File.WriteAllBytesAsync(Destination, original);

        var error = await Assert.ThrowsAsync<AudioToolException>(() => Service(new(MaximumWavFileBytes: 64))
            .DecodeToWavAsync(source.Catalog, source.Media, Destination));

        Assert.Contains("binary output limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(Destination));
        AssertPipedAndStopped();
        AssertClean(source);
    }

    [Fact]
    public async Task NativeFailureAfterOutputCannotReplaceTheDestination()
    {
        using var source = await Source.CreateAsync(Wave(1, 16));
        Environment.SetEnvironmentVariable(ExitVariable, "17");
        var original = Encoding.ASCII.GetBytes("Retain generated destination on native failure");
        await File.WriteAllBytesAsync(Destination, original);

        await Assert.ThrowsAsync<AudioToolException>(() => Service().DecodeToWavAsync(source.Catalog, source.Media, Destination));

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination));
        AssertPipedAndStopped();
        AssertClean(source);
    }

    [Fact]
    public async Task CancellationStopsTheProducerAndRemovesOnlyItsStaging()
    {
        using var source = await Source.CreateAsync(Wave(1, 16));
        Environment.SetEnvironmentVariable(LingerVariable, "10000");
        var original = Encoding.ASCII.GetBytes("Retain generated destination on cancellation");
        await File.WriteAllBytesAsync(Destination, original);
        using var cancellation = new CancellationTokenSource();
        var decoding = Service().DecodeToWavAsync(source.Catalog, source.Media, Destination, cancellationToken: cancellation.Token);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(ReceiptPath)) await Task.Delay(10, wait.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination));
        AssertPipedAndStopped();
        AssertClean(source);
    }

    [Fact]
    public async Task CancellationDuringValidationCannotCommitTheOutput()
    {
        using var source = await Source.CreateAsync(Wave(1, 16));
        var original = Encoding.ASCII.GetBytes("Retain generated destination before commit");
        await File.WriteAllBytesAsync(Destination, original);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().DecodeToWavAsync(source.Catalog, source.Media, Destination,
            progress: new ProgressAction(update => { if (update.Phase == "Validating WAV") cancellation.Cancel(); }),
            cancellationToken: cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(Destination));
        AssertPipedAndStopped();
        AssertClean(source);
    }

    private static AudioService Service(AudioProcessingLimits? limits = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "BinaryOutputWorker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable), "Build the generic binary fixture apphost before running these tests.");
        return new(new AudioToolPaths(executable, null, null, null, null, null, null), processingLimits: limits);
    }

    private void AssertPipedAndStopped()
    {
        var receipt = JsonSerializer.Deserialize<Invocation>(File.ReadAllText(ReceiptPath))!;
        Assert.Contains("-p", receipt.Arguments);
        Assert.DoesNotContain("-o", receipt.Arguments);
        try
        {
            using var process = Process.GetProcessById(receipt.ProcessId);
            Assert.True(process.HasExited || process.StartTime.ToUniversalTime().Ticks != receipt.StartTimeUtcTicks,
                "The generated native producer is still running.");
        }
        catch (ArgumentException) { }
    }

    private void AssertClean(Source source)
    {
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        if (Directory.Exists(source.WorkspaceRoot))
            Assert.Empty(Directory.EnumerateFiles(source.WorkspaceRoot, "*", SearchOption.AllDirectories));
    }

    private static byte[] Wave(ushort code, ushort bits)
    {
        var width = bits / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write((uint)(36 + 32 * width)); writer.Write("WAVEfmt "u8);
        writer.Write(16u); writer.Write(code); writer.Write((ushort)1); writer.Write(22050u);
        writer.Write((uint)(22050 * width)); writer.Write((ushort)width); writer.Write(bits);
        writer.Write("data"u8); writer.Write((uint)(32 * width));
        for (var index = 0; index < 32; index++)
            if (code == 3) writer.Write(index / 64f);
            else for (var part = 0; part < width; part++) writer.Write((byte)(index * 19 + part * 73));
        writer.Flush(); return stream.ToArray();
    }

    public void Dispose()
    {
        foreach (var (key, value) in savedEnvironment) Environment.SetEnvironmentVariable(key, value);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Invocation(string[] Arguments, int ProcessId, long StartTimeUtcTicks);
    private sealed class ProgressAction(Action<AudioProgress> action) : IProgress<AudioProgress>
    { public void Report(AudioProgress value) => action(value); }

    private sealed class Source(FormatsFixtureBuilder fixture, AudioCatalog catalog, MediaEntry media) : IDisposable
    {
        public AudioCatalog Catalog { get; } = catalog;
        public MediaEntry Media { get; } = media;
        public string WorkspaceRoot { get; } = Path.Combine(Path.GetTempPath(), "DeviantAudio",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.Fingerprint))).ToLowerInvariant()[..16]);

        public static async Task<Source> CreateAsync(byte[] bytes)
        {
            var fixture = FormatsFixtureBuilder.CreateEmpty();
            try
            {
                fixture.AddResource(1033, 1, bytes); fixture.WriteIndex();
                var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
                var record = Assert.Single(snapshot.Records);
                var volume = Assert.Single(snapshot.Volumes);
                var catalog = new AudioCatalog
                {
                    SourcePath = snapshot.IndexPath, Fingerprint = Guid.NewGuid().ToString("N"), IndexContentId = snapshot.IndexContentId,
                    PhysicalSourceStamp = snapshot.PhysicalSourceStamp, IndexStamp = snapshot.IndexStamp, SourceVolumes = snapshot.Volumes.ToList()
                };
                var media = new MediaEntry
                {
                    Identity = new(snapshot.IndexContentId, record.Ordinal, "wem"), Key = "generated", Id = 1,
                    Slice = new(volume.FilePath, record.Offset, record.StoredSize), State = MediaState.CompleteEmbedded,
                    Completeness = MediaCompleteness.Complete, Availability = SourceAvailability.Available,
                    ContainerValidity = ContainerValidity.Valid, IsRiff = true, Codec = "PCM", Channels = 1, SampleRate = 22050
                };
                return new(fixture, catalog, media);
            }
            catch { fixture.Dispose(); throw; }
        }

        public void Dispose()
        {
            fixture.Dispose();
            if (Directory.Exists(WorkspaceRoot)) Directory.Delete(WorkspaceRoot, recursive: true);
        }
    }
}
