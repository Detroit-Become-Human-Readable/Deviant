using System.Runtime.ExceptionServices;

namespace Deviant.Catalog;

public sealed partial class CacheManager
{
    private const string ScratchDirectory = "scratch-v1";

    /// <summary>Produces an admitted temporary file retained only for the returned consumer lease.</summary>
    public async Task<CacheLease> AcquireScratchOutputAsync(string extension,
        Func<Stream, CancellationToken, Task> producer, Func<string, CancellationToken, Task<bool>> validator,
        CancellationToken token = default)
    {
        if (extension is null || extension.Length is < 2 or > 17 || extension[0] != '.' ||
            extension.Skip(1).Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("Use a single file extension.", nameof(extension));
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(validator);
        token.ThrowIfCancellationRequested();
        var location = await BeginAcquisitionAsync(token).ConfigureAwait(false);
        string? path = null;
        var registered = false;
        var created = false;
        try
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureOutputStorageEnabled(location);
                var directory = Path.Combine(location, ScratchDirectory);
                ValidateLocation(directory);
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());
                lock (leaseGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    knownDirectories.Add(directory);
                    leases.Add(path, 1);
                    registered = true;
                }
            }
            finally { operation.Release(); }

            await using (var output = new CacheArtifactOutput(location, path).OpenWrite())
            {
                created = true;
                await producer(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await ObserveScratchOutputAsync(path, token).ConfigureAwait(false);
            if (!await validator(path, token).ConfigureAwait(false))
                throw new InvalidDataException("The scratch producer created an invalid file.");
            token.ThrowIfCancellationRequested();
            await ObserveScratchOutputAsync(path, token).ConfigureAwait(false);
            var completedPath = path;
            // Acquisition and local pin remain registered until deletion/accounting finish.
            return new CacheLease(completedPath, () => ReleaseScratchOutputAsync(completedPath, location, true, true));
        }
        catch (Exception error)
        {
            try { await ReleaseScratchOutputAsync(path, location, registered, created).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (!error.Data.Contains("ScratchCleanupError"))
                {
                    var message = cleanup.GetType().Name + ": " + cleanup.Message;
                    error.Data["ScratchCleanupError"] = message[..Math.Min(message.Length, 2048)];
                }
            }
            throw;
        }
    }

    private async Task ObserveScratchOutputAsync(string path, CancellationToken token)
    {
        await operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ObserveFile(path)) throw new IOException("The scratch output is unavailable.");
        }
        finally { operation.Release(); }
    }

    private async ValueTask ReleaseScratchOutputAsync(string? path, string location, bool registered, bool created)
    {
        Exception? failure = null;
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (created && path is not null)
            {
                ValidateLocation(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Scratch output files cannot be symbolic links.");
                DeleteAdmittedOutput(path, location);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                lock (leaseGate)
                {
                    if (registered && path is not null) leases.Remove(path);
                    EndAcquisition();
                }
            }
            finally { operation.Release(); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
