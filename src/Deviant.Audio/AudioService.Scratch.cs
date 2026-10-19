using System.Runtime.ExceptionServices;
using Deviant.Formats.Audio.Model;

namespace Deviant.Audio;

public sealed partial class AudioService
{
    private static bool CanRetryNativeFailure(AudioToolException error) => error.Data.Contains("NativeExitCode") &&
        !HasArtifactFailure(error);

    private static bool HasArtifactFailure(Exception error) => error.Data.Contains("ScratchCleanupError") ||
        error.Data.Contains("AudioScratchCleanupError") || error.Data.Contains("AudioSourceCleanupError") ||
        error.Data.Contains("ProcessCleanup");

    private async Task<AudioSourceLease> CreateScratchAsync(string extension, string workspace,
        Func<Stream, CancellationToken, Task> producer, CancellationToken token)
    {
        if (sourceStorage is IAudioScratchStorage scratch)
            return await scratch.CreateAsync(extension, producer,
                (path, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return Task.FromResult(new FileInfo(path).Length > 0);
                }, token).ConfigureAwait(false);

        var path = Path.Combine(workspace, Guid.NewGuid().ToString("N") + extension);
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await producer(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnsureFile(path, "The audio producer created no output.");
            return new AudioSourceLease(path, new ScratchFileOwner(path));
        }
        catch (Exception error)
        {
            try { File.Delete(path); }
            catch (Exception cleanup) { AttachScratchCleanup(error, cleanup); }
            throw;
        }
    }

    private Task<AudioSourceLease> DecodeScratchAsync(MediaEntry media, string source, string workspace,
        IProgress<AudioProgress>? progress, CancellationToken token) =>
        CreateScratchAsync(".wav", workspace, async (output, cancellation) =>
        {
            await DecodeMaterializedToWavAsync(media, source, output, WavFormat.Pcm16, progress,
                cancellation, DecodePurpose.Conversion).ConfigureAwait(false);
        }, token);

    private sealed class AudioArtifactScope : IAsyncDisposable
    {
        private readonly List<AudioSourceLease> leases = [];
        public Exception? Failure { get; set; }
        public AudioSourceLease Add(AudioSourceLease lease) { leases.Add(lease); return lease; }

        public async ValueTask DisposeAsync()
        {
            Exception? cleanupFailure = null;
            for (int index = leases.Count - 1; index >= 0; index--)
            {
                try { await leases[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            leases.Clear();
            if (cleanupFailure is null) return;
            if (Failure is null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            AttachScratchCleanup(Failure!, cleanupFailure);
        }
    }

    private sealed class ScratchFileOwner(string path) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { File.Delete(path); return ValueTask.CompletedTask; }
    }

    private static void AttachScratchCleanup(Exception failure, Exception cleanup)
    {
        if (failure.Data.Contains("AudioScratchCleanupError")) return;
        var diagnostic = cleanup.GetType().Name + ": " + cleanup.Message;
        failure.Data["AudioScratchCleanupError"] = diagnostic[..Math.Min(diagnostic.Length, 2048)];
    }
}
