using System.Security.Cryptography;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class OggScratchAdmissionTests
{
    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task OggConversionCannotEscapeTheBudgetThroughDecodedScratch()
    {
        using var fixture = CreateFixture(88200);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        await using (var preview = await session.PreviewAsync(row.Identity)) { }
        var before = StoredBytes(workspace.Cache.Location);
        await workspace.Cache.SetLimitAsync(before + 8192);

        var report = await session.ExportAsync(session.Review([row]), new(workspace.Output, AudioExportFormat.Ogg));

        var entry = Assert.Single(report.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Failed, entry.Status);
        Assert.Equal("CacheStorageLimit", entry.ErrorCode);
        Assert.True(StoredBytes(workspace.Cache.Location) <= before + 8192);
        Assert.Empty(Directory.EnumerateFiles(workspace.Output, "*.ogg", SearchOption.AllDirectories));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task OggConversionOwnsAndRemovesItsAdmittedScratch()
    {
        using var fixture = CreateFixture(22050);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        bool sawEncoding = false;

        var report = await session.ExportAsync(session.Review([row]), new(workspace.Output, AudioExportFormat.Ogg),
            new Callback(value =>
            {
                if (value.Phase != "Encoding Ogg Vorbis") return;
                sawEncoding = true;
                Assert.NotEmpty(Directory.EnumerateFiles(workspace.Cache.Location, "*.wav", SearchOption.AllDirectories));
                Assert.True(workspace.Cache.ActivePreviewLeases >= 2);
            }));

        Assert.True(sawEncoding);
        var entry = Assert.Single(report.Manifest.Entries);
        Assert.True(entry.Status == AudioExportStatus.Completed, entry.ErrorCode + ": " + entry.Diagnostic);
        Assert.Equal(AudioOperation.Reencode, entry.Operation);
        Assert.Equal(22050, entry.OutputSampleRate);
        Assert.Equal(1, entry.OutputChannels);
        var output = Path.Combine(workspace.Output, entry.RelativeOutput);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output))).ToLowerInvariant(), entry.Sha256);
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.ogg", SearchOption.AllDirectories));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: true)]
    public async Task CancellationAfterDecodeRemovesScratchWithoutCommittingOgg()
    {
        using var fixture = CreateFixture(22050);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        using var cancellation = new CancellationTokenSource();

        var report = await session.ExportAsync(session.Review([row]), new(workspace.Output, AudioExportFormat.Ogg),
            new Callback(value => { if (value.Phase == "Encoding Ogg Vorbis") cancellation.Cancel(); }), cancellation.Token);

        Assert.True(report.Manifest.Cancelled);
        Assert.Equal(AudioExportStatus.Cancelled, Assert.Single(report.Manifest.Entries).Status);
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.ogg", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Output, "*.ogg", SearchOption.AllDirectories));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
    }

    private static FormatsFixtureBuilder CreateFixture(int frames)
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Sample_ENG", FormatsFixtureBuilder.MakePcmWave(22050, frames)));
        fixture.WriteIndex();
        return fixture;
    }
    private static Task<AudioExplorerSession> OpenAsync(FormatsFixtureBuilder fixture, Workspace workspace)
    {
        var tools = AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")) with { Ww2Ogg = null, Revorb = null };
        return AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, tools);
    }
    private static long StoredBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    private sealed class Callback(Action<AudioProgress> action) : IProgress<AudioProgress>
    { public void Report(AudioProgress value) => action(value); }
    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-ogg-scratch-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public string Output => Path.Combine(root, "exports");
        public Workspace() { Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"))); Aliases = new(Path.Combine(root, "library")); }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
