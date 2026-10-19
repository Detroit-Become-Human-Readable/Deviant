using System.Security.Cryptography;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioExportInputAdmissionTests
{
    [InlineData(0)]
    [InlineData(512)]
    [AudioToolsTheory(requireDecoder: true, requireFfmpeg: false)]
    public async Task NativeExportCannotMaterializeInputOutsideTheCacheBudget(long limit)
    {
        using var fixture = CreateFixture();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        var review = session.Review([row]);
        await workspace.Cache.SetLimitAsync(limit);
        long before = StoredBytes(workspace.Cache.Location);

        var report = await session.ExportAsync(review, new(workspace.ExportPath, AudioExportFormat.Wav));

        var entry = Assert.Single(report.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Failed, entry.Status);
        Assert.Equal("CacheStorageLimit", entry.ErrorCode);
        Assert.Equal(before, StoredBytes(workspace.Cache.Location));
        Assert.Empty(Directory.EnumerateFiles(workspace.ExportPath, "*.wav", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);

        var original = await session.ExportAsync(review, new(workspace.ExportPath, AudioExportFormat.Original));
        Assert.Equal(AudioExportStatus.Completed, Assert.Single(original.Manifest.Entries).Status);
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: false)]
    public async Task PreviewAndNativeExportShareTheVerifiedInput()
    {
        using var fixture = CreateFixture();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        await using var preview = await session.PreviewAsync(row.Identity);
        var input = Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories));
        var expected = await File.ReadAllBytesAsync(input);
        bool sawDecode = false;
        var report = await session.ExportAsync(session.Review([row]), new(workspace.ExportPath, AudioExportFormat.Wav),
            new Callback(value =>
            {
                if (value.Phase != "Decoding") return;
                sawDecode = true;
                Assert.Equal(input, Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories)));
                Assert.Equal(expected, File.ReadAllBytes(input));
                var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Snapshot.IndexContentId)))
                    .ToLowerInvariant()[..16];
                var legacyWorkspace = Path.Combine(Path.GetTempPath(), "DeviantAudio", fingerprint);
                if (Directory.Exists(legacyWorkspace))
                    Assert.Empty(Directory.EnumerateFiles(legacyWorkspace, "input-*.wem", SearchOption.AllDirectories));
            }));

        var entry = Assert.Single(report.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Completed, entry.Status);
        Assert.True(sawDecode);
        var output = Path.Combine(workspace.ExportPath, entry.RelativeOutput);
        Assert.Equal(2205, AudioService.ReadWavMetadata(output).SampleFrames);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output))).ToLowerInvariant(), entry.Sha256);
        Assert.Equal(input, Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories)));
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(1, workspace.Cache.ActivePreviewLeases);
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: false)]
    public async Task CorruptPreparedInputIsRepairedBeforeNativeExport()
    {
        using var fixture = CreateFixture();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        await using var preview = await session.PreviewAsync(row.Identity);
        var input = Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories));
        var expected = await File.ReadAllBytesAsync(input);
        await File.WriteAllBytesAsync(input, new byte[] { 1, 2, 3 });

        var report = await session.ExportAsync(session.Review([row]), new(workspace.ExportPath, AudioExportFormat.Wav));

        Assert.Equal(AudioExportStatus.Completed, Assert.Single(report.Manifest.Entries).Status);
        Assert.Equal(expected, await File.ReadAllBytesAsync(input));
        Assert.Equal(1, workspace.Cache.ActivePreviewLeases);
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.tmp", SearchOption.AllDirectories));
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: false)]
    public async Task CancelledNativeExportReleasesItsInputAndPreservesTheDestination()
    {
        using var fixture = CreateFixture();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        var review = session.Review([row]);
        var first = await session.ExportAsync(review, new(workspace.ExportPath, AudioExportFormat.Wav));
        var entry = Assert.Single(first.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Completed, entry.Status);
        var destination = Path.Combine(workspace.ExportPath, entry.RelativeOutput);
        var original = await File.ReadAllBytesAsync(destination);
        using var cancellation = new CancellationTokenSource();

        var cancelled = await session.ExportAsync(review, new(workspace.ExportPath, AudioExportFormat.Wav, Overwrite: true),
            new Callback(value => { if (value.Phase == "Decoding") cancellation.Cancel(); }), cancellation.Token);

        Assert.True(cancelled.Manifest.Cancelled);
        Assert.Equal(AudioExportStatus.Cancelled, Assert.Single(cancelled.Manifest.Entries).Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
        Assert.Empty(Directory.EnumerateFiles(workspace.ExportPath, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.tmp", SearchOption.AllDirectories));
    }

    [AudioToolsFact(requireDecoder: true, requireFfmpeg: false)]
    public async Task ConcurrentExportsShareOneInputAndReleaseBothLeases()
    {
        using var fixture = CreateFixture();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var row = await session.PrepareSelectedMediaAsync(Assert.Single(session.Catalog.Media).Identity!);
        var review = session.Review([row]);

        var reports = await Task.WhenAll(session.ExportAsync(review, new(Path.Combine(workspace.ExportPath, "a"), AudioExportFormat.Wav)),
            session.ExportAsync(review, new(Path.Combine(workspace.ExportPath, "b"), AudioExportFormat.Wav)));

        Assert.All(reports, report => Assert.Equal(AudioExportStatus.Completed, Assert.Single(report.Manifest.Entries).Status));
        Assert.Equal(reports[0].Manifest.Entries[0].Sha256, reports[1].Manifest.Entries[0].Sha256);
        Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location, "*.wem", SearchOption.AllDirectories));
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
        Assert.Empty(Directory.EnumerateFiles(workspace.Cache.Location, "*.tmp", SearchOption.AllDirectories));
    }

    private static FormatsFixtureBuilder CreateFixture()
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Sample_ENG",
            FormatsFixtureBuilder.MakePcmWave(22050, 2205)));
        fixture.WriteIndex();
        return fixture;
    }

    private static Task<AudioExplorerSession> OpenAsync(FormatsFixtureBuilder fixture, Workspace workspace) =>
        AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
    private static long StoredBytes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    private sealed class Callback(Action<AudioProgress> action) : IProgress<AudioProgress>
    { public void Report(AudioProgress value) => action(value); }
    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-export-admission-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public string ExportPath => Path.Combine(root, "exports");
        public Workspace() { Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"))); Aliases = new(Path.Combine(root, "library")); }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
