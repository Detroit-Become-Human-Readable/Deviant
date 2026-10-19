using System.Security.Cryptography;
using Deviant.Audio;
using Deviant.Catalog;

namespace Deviant.Browser;

internal sealed class CacheAudioSourceStorage(CacheManager cache) : IAudioSourceStorage, IAudioScratchStorage
{
    public async Task<AudioSourceLease> CreateAsync(string extension, Func<Stream, CancellationToken, Task> producer,
        Func<string, CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        var cached = await cache.AcquireScratchOutputAsync(extension, producer, validator, cancellationToken).ConfigureAwait(false);
        return await PinAsync(cached, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioSourceLease> AcquireAsync(AudioSourceKey source, Func<Stream, CancellationToken, Task> producer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(producer);
        if (source.Length <= 0 || source.PayloadSha256 is not { Length: 64 } || !source.PayloadSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The audio input requires a bounded length and SHA-256 fingerprint.", nameof(source));
        var key = CacheManager.PreparedKey("audio-input", "audio-source-slice-v1", source.Revision,
            source.PayloadSha256 + "/" + source.Extension);
        var cached = await cache.AcquirePreparedOutputAsync("audio-input", key, source.Extension, producer,
            async (path, token) =>
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                return input.Length == source.Length && StringComparer.OrdinalIgnoreCase.Equals(source.PayloadSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)));
            }, cancellationToken).ConfigureAwait(false);
        return await PinAsync(cached, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AudioSourceLease> PinAsync(CacheLease cached, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AudioSourceLease(cached.Path, cached);
        }
        catch (Exception error)
        {
            try { await cached.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["AudioSourceCleanupError"] = cleanup.GetType().Name + ": " + cleanup.Message[..Math.Min(512, cleanup.Message.Length)]; }
            throw;
        }
    }
}
