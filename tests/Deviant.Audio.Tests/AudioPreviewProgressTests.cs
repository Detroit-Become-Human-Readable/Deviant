using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioPreviewProgressTests
{
    [AudioToolsFact(requireDecoder: true, requireFfmpeg: false)]
    public async Task PreviewReportsPreparationBoundariesAndDecoderProgress()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Sample_ENG",
            FormatsFixtureBuilder.MakePcmWave(22050, 2205)));
        fixture.WriteIndex();
        var root = Path.Combine(Path.GetTempPath(), "deviant-preview-progress-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
            await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache,
                new AliasStore(Path.Combine(root, "library")),
                AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
            var phases = new List<string>();
            await using var preview = await session.PreviewAsync(Assert.Single(session.Catalog.Media).Identity!,
                progress: new CaptureProgress(phases));
            Assert.Contains("Preparing selected clip", phases);
            Assert.Contains("Verifying audio metadata", phases);
            Assert.Contains("Reading decoder identity", phases);
            Assert.Contains("Preparing preview output", phases);
            Assert.Contains("Decoding", phases);
            Assert.Contains("Verifying prepared preview", phases);
            Assert.Equal("Preview ready", phases[^1]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CaptureProgress(List<string> phases) : IProgress<AudioProgress>
    {
        public void Report(AudioProgress value) => phases.Add(value.Phase);
    }
}
