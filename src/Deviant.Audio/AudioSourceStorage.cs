namespace Deviant.Audio;

public sealed record AudioSourceKey(string Revision, string PayloadSha256, string Extension, long Length);

/// <summary>
/// Admits input writes and validates length and SHA-256 on hits and new files. The producer leaves
/// its stream open; the returned lease keeps the input available until its consumer finishes.
/// </summary>
public interface IAudioSourceStorage
{
    Task<AudioSourceLease> AcquireAsync(AudioSourceKey source, Func<Stream, CancellationToken, Task> producer,
        CancellationToken cancellationToken = default);
}

/// <summary>Admits temporary producer output and retains it until its native consumer finishes.</summary>
public interface IAudioScratchStorage
{
    Task<AudioSourceLease> CreateAsync(string extension, Func<Stream, CancellationToken, Task> producer,
        Func<string, CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default);
}

/// <summary>Holds the decoder input open for reading until the operation releases its storage lease.</summary>
public sealed class AudioSourceLease : IAsyncDisposable
{
    private readonly string path;
    private FileStream? pin;
    private IAsyncDisposable? owner;

    public AudioSourceLease(string path, IAsyncDisposable? owner = null)
    {
        this.path = System.IO.Path.GetFullPath(path);
        pin = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        this.owner = owner;
    }

    public string Path { get { ObjectDisposedException.ThrowIf(pin is null, this); return path; } }
    internal Stream Content => pin ?? throw new ObjectDisposedException(nameof(AudioSourceLease));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref pin, null) is not { } released) return;
        try { await released.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            if (Interlocked.Exchange(ref owner, null) is { } storage)
                await storage.DisposeAsync().ConfigureAwait(false);
        }
    }
}
