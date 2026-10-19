using Deviant.Archives;
using Deviant.Audio;
using Deviant.Core;
using Deviant.Formats.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioSourceStorageTests
{
    [Fact]
    public async Task SourceVerificationErrorSurvivesAStorageCleanupFailure()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033, 1, FormatsFixtureBuilder.MakeDialogueMedia("Generated_Sample_ENG",
            FormatsFixtureBuilder.MakePcmWave(22050, 32)));
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = (await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var root = Path.Combine(Path.GetTempPath(), "deviant-source-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CorruptStorage(Path.Combine(root, "input.wem"));
            var decoder = Path.Combine(AppContext.BaseDirectory, "fixtures", "BinaryOutputWorker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var service = new AudioService(new(decoder, null, null, null, null, null, null), sourceStorage: store);

            var error = await Assert.ThrowsAsync<ArchiveException>(() => service.DecodeToWavAsync(catalog,
                Assert.Single(catalog.Media), Path.Combine(root, "result.wav")));

            Assert.Equal("SourceChanged", error.Code);
            Assert.True(store.Released);
            Assert.NotNull(error.Data["AudioSourceCleanupError"]);
            using var unpinned = new FileStream(Path.Combine(root, "input.wem"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.False(File.Exists(Path.Combine(root, "result.wav")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CorruptStorage(string path) : IAudioSourceStorage, IAsyncDisposable
    {
        public bool Released { get; private set; }
        public async Task<AudioSourceLease> AcquireAsync(AudioSourceKey source, Func<Stream, CancellationToken, Task> producer,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(path, new byte[checked((int)source.Length)], cancellationToken);
            return new(path, this);
        }
        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.FromException(new IOException("Generated storage cleanup failure"));
        }
    }
}
