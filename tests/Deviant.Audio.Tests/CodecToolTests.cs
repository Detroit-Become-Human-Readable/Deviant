using Deviant.Archives;
using Deviant.Audio;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CodecToolTests
{
    [InlineData(WavFormat.Pcm16, (ushort)1, (ushort)16)]
    [InlineData(WavFormat.Pcm24, (ushort)1, (ushort)24)]
    [InlineData(WavFormat.Float32, (ushort)3, (ushort)32)]
    [AudioToolsTheory(requireDecoder: true, requireFfmpeg: false)]
    public async Task DecodeProducesRequestedWavPrecisionAndRetainsUnverifiedClassification(
        WavFormat precision, ushort formatCode, ushort bitsPerSample)
    {
        using var fixture = FormatsFixtureBuilder.CreateRawPcmAudioSource();
        var (catalog, media) = await CreateAudioSelectionAsync(fixture);
        var tools = GetConfiguredTools();
        var outputPath = Path.Combine(fixture.DirectoryPath, $"decoded-{precision}.wav");

        var result = await new AudioService(tools).DecodeToWavAsync(catalog, media, outputPath, precision);

        var metadata = AudioService.ReadWavMetadata(outputPath);
        Assert.Equal(formatCode, metadata.FormatCode);
        Assert.Equal(bitsPerSample, metadata.BitsPerSample);
        Assert.Equal(1, metadata.Channels);
        Assert.Equal(22050, metadata.SampleRate);
        Assert.True(result.SampleFrames > 0);
        Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task ConvertValidatedUnverifiedPcmSourceToOggAndKeepItsClassification()
    {
        using var fixture = FormatsFixtureBuilder.CreateRawPcmAudioSource();
        var (catalog, media) = await CreateAudioSelectionAsync(fixture);
        var outputPath = Path.Combine(fixture.DirectoryPath, "converted.ogg");

        var result = await new AudioService(GetConfiguredTools()).ConvertToOggAsync(catalog, media, outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(AudioOperation.Reencode, result.Operation);
        Assert.Equal(1, result.Channels);
        Assert.Equal(22050, result.SampleRate);
        Assert.True(result.SampleFrames > 0);
        Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
    }

    [Fact]
    public async Task FullConversionAndImplicitFragmentPreviewAreRejectedWithoutToolInvocation()
    {
        var fragment = new MediaEntry
        {
            Slice = new DataSlice("fixture-volume.dat", 0, 16),
            State = MediaState.PrefetchOnly,
            Completeness = MediaCompleteness.Fragment,
            Availability = SourceAvailability.Available,
            ContainerValidity = ContainerValidity.Truncated,
            IsRiff = true
        };
        var catalog = new AudioCatalog { Fingerprint = "synthetic" };
        var service = new AudioService(new AudioToolPaths(null, null, null, null, null, null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeToWavAsync(catalog, fragment, "ignored.wav"));
        await Assert.ThrowsAsync<AudioToolException>(() => new PreviewService(service).CreateAsync(catalog, fragment));
    }
    [Fact]
    public void TruncatedOrPrefetchBytesCannotUseTheUnverifiedFullConversionPath()
    {
        var media = new MediaEntry { Slice=new("synthetic.dat",0,64), Availability=SourceAvailability.Available,
            IsRiff=true, State=MediaState.PrefetchOnly, ContainerValidity=ContainerValidity.Truncated, Completeness=MediaCompleteness.Unverified };
        Assert.True(MediaPolicy.CanPreview(media)); Assert.False(MediaPolicy.CanConvert(media));
        media.State=MediaState.CompleteEmbedded; Assert.False(MediaPolicy.CanConvert(media));
        media.ContainerValidity=ContainerValidity.Valid; Assert.True(MediaPolicy.CanConvert(media));
    }

    [Fact]
    public async Task ProcessRunnerStopsAProcessThatExceedsItsConfiguredWorkingSet()
    {
        var isWindows = OperatingSystem.IsWindows();
        var shell = isWindows ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh";
        var arguments = isWindows ? new[] { "/c", "ping 127.0.0.1 -n 4 > nul" } : ["-c", "sleep 3"];
        var runner = new ProcessRunner();

        var error = await Assert.ThrowsAsync<AudioToolException>(() => runner.RunAsync(
            new ProcessRequest(shell, arguments, MaximumWorkingSetBytes: 1)));

        Assert.Contains("working-set", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AudioToolPaths GetConfiguredTools()
    {
        var directory = Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS");
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The tool test attribute should skip when DEVIANT_AUDIO_TOOLS is unavailable.");
        return AudioToolPaths.Discover(directory);
    }

    private static async Task<(AudioCatalog Catalog, MediaEntry Media)> CreateAudioSelectionAsync(FormatsFixtureBuilder fixture)
    {
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var resource = Assert.Single(snapshot.Records, row => row.Type == 1033);
        var volume = Assert.Single(snapshot.Volumes, item => item.Package == resource.Package);
        var media = new MediaEntry
        {
            Identity = new Deviant.Assets.ResourceIdentity(snapshot.IndexContentId, resource.Ordinal, "wem"),
            Key = $"{snapshot.IndexContentId}:resource:{resource.Ordinal}:wem",
            Id = resource.Id,
            Name = "Synthetic PCM fixture",
            Slice = new DataSlice(volume.FilePath, resource.Offset, resource.StoredSize),
            State = MediaState.CompleteEmbedded,
            Completeness = MediaCompleteness.Unverified,
            Availability = SourceAvailability.Available,
            ContainerValidity = ContainerValidity.Valid,
            IsRiff = true,
            Codec = "PCM",
            Channels = 1,
            SampleRate = 22050
        };
        var catalog = new AudioCatalog
        {
            SourcePath = snapshot.IndexPath,
            Fingerprint = snapshot.IndexContentId,
            IndexContentId = snapshot.IndexContentId,
            PhysicalSourceStamp = snapshot.PhysicalSourceStamp,
            IndexStamp = snapshot.IndexStamp,
            SourceVolumes = snapshot.Volumes.ToList(),
            IndexedResourceCount = snapshot.Records.Count
        };
        return (catalog, media);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class AudioToolsFactAttribute : FactAttribute
{
    public AudioToolsFactAttribute(bool requireDecoder, bool requireFfmpeg)
    { Skip = AudioToolSkipReason.Get(requireDecoder, requireFfmpeg); }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class AudioToolsTheoryAttribute : TheoryAttribute
{
    public AudioToolsTheoryAttribute(bool requireDecoder, bool requireFfmpeg)
    { Skip = AudioToolSkipReason.Get(requireDecoder, requireFfmpeg); }
}

internal static class AudioToolSkipReason
{
    public static string? Get(bool requireDecoder, bool requireFfmpeg)
    {
        var directory = Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS");
        if (string.IsNullOrWhiteSpace(directory))
            return "Set DEVIANT_AUDIO_TOOLS to a configured portable codec-tool directory to run this integration test.";

        var paths = AudioToolPaths.Discover(directory);
        if (requireDecoder && paths.VgmstreamCli is null)
            return "vgmstream-cli is required; configure it under DEVIANT_AUDIO_TOOLS.";
        else if (requireFfmpeg && (paths.Ffmpeg is null || paths.Ffprobe is null))
            return "ffmpeg and ffprobe are required; configure them under DEVIANT_AUDIO_TOOLS.";
        return null;
    }
}
