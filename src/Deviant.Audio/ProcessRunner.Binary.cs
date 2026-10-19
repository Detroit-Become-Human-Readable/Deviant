using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Deviant.Audio;

public sealed partial class ProcessRunner
{
    /// <summary>Copies a child process's binary output to an open caller-owned destination.</summary>
    public async Task<ProcessResult> RunBinaryAsync(ProcessRequest request, Stream destination, long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        var maximumWorkingSetBytes = request.MaximumWorkingSetBytes > 0 ? request.MaximumWorkingSetBytes : defaultMaximumWorkingSetBytes;
        if (maximumWorkingSetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(request.MaximumWorkingSetBytes));
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) start.WorkingDirectory = request.WorkingDirectory;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        if (request.Environment is not null)
            foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        using var timeoutSource = request.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linkedSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var clock = Stopwatch.StartNew();
        Task? copyTask = null, exitTask = null;
        Task<string>? stderrTask = null;
        var started = false;
        try
        {
            linkedSource.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException($"Could not start process '{request.FileName}'.");
            started = true;
            copyTask = CopyBinaryOutputAsync(process.StandardOutput.BaseStream, destination, maximumBytes, linkedSource.Token);
            stderrTask = ReadBoundedAsync(process.StandardError, OutputLimit, linkedSource.Token);
            exitTask = process.WaitForExitAsync(linkedSource.Token);
            while (!copyTask.IsCompleted || !stderrTask.IsCompleted || !exitTask.IsCompleted)
            {
                // Observe copy/admission failures before cancellation or another limit can hide them.
                if (copyTask.IsFaulted || copyTask.IsCanceled) await copyTask.ConfigureAwait(false);
                if (stderrTask.IsFaulted || stderrTask.IsCanceled) await stderrTask.ConfigureAwait(false);
                if (exitTask.IsFaulted || exitTask.IsCanceled) await exitTask.ConfigureAwait(false);
                linkedSource.Token.ThrowIfCancellationRequested();
                if (ExceedsWorkingSet(process, maximumWorkingSetBytes))
                    throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its working-set limit.",
                        $"Limit: {maximumWorkingSetBytes} bytes.");
                if (ExceedsOutputLimit(request))
                    throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its output-file limit.",
                        $"Limit: {request.MaximumOutputFileBytes} bytes.");
                var pending = new List<Task>(4);
                if (!copyTask.IsCompleted) pending.Add(copyTask);
                if (!stderrTask.IsCompleted) pending.Add(stderrTask);
                if (!exitTask.IsCompleted) pending.Add(exitTask);
                pending.Add(Task.Delay(TimeSpan.FromMilliseconds(100), linkedSource.Token));
                await Task.WhenAny(pending).ConfigureAwait(false);
            }
            await copyTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            await exitTask.ConfigureAwait(false);
            linkedSource.Token.ThrowIfCancellationRequested();
            if (ExceedsOutputLimit(request))
                throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its output-file limit.",
                    $"Limit: {request.MaximumOutputFileBytes} bytes.");
            return new ProcessResult(process.ExitCode, "", stderr, clock.Elapsed);
        }
        catch (Exception error)
        {
            Exception failure = error;
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested && timeoutSource?.IsCancellationRequested == true)
                failure = new TimeoutException($"Process '{Path.GetFileName(request.FileName)}' exceeded its time limit.", error);
            var cleanup = await StopBinaryProcessAsync(process, started, linkedSource, copyTask, stderrTask).ConfigureAwait(false);
            // Keep the admission error instance and its existing structured details intact.
            if (!failure.Data.Contains("ProcessCleanup")) failure.Data["ProcessCleanup"] = cleanup;
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static async Task CopyBinaryOutputAsync(Stream source, Stream destination, long maximumBytes, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = maximumBytes - written;
            // Read one extra byte only when needed to distinguish an exact-length result from overflow.
            var requested = remaining < buffer.Length ? (int)remaining + 1 : buffer.Length;
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
            if (read == 0) return;
            var accepted = (int)Math.Min(read, remaining);
            if (accepted > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, accepted), token).ConfigureAwait(false);
                written += accepted;
            }
            if (read > accepted)
                throw new AudioToolException("The process exceeded its binary output limit.", $"Limit: {maximumBytes} bytes.");
        }
    }

    private static async Task<string> StopBinaryProcessAsync(Process process, bool started, CancellationTokenSource linked,
        Task? copy, Task? stderr)
    {
        var clock = Stopwatch.StartNew();
        if (!started) return "Child not started.";
        TryKillTree(process);
        string? cancellationError = null;
        try { linked.Cancel(); }
        catch (Exception error) { cancellationError = error.GetType().Name; }
        Task exit;
        try { exit = process.WaitForExitAsync(CancellationToken.None); }
        catch (Exception error) { exit = Task.FromException(error); }
        var observations = Task.WhenAll(ObserveBinaryCleanupAsync(exit), ObserveBinaryCleanupAsync(copy), ObserveBinaryCleanupAsync(stderr));
        var remaining = TimeSpan.FromSeconds(2) - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                var results = await observations.WaitAsync(remaining).ConfigureAwait(false);
                return $"Exit={results[0]}; stdout={results[1]}; stderr={results[2]}; cancellation={cancellationError ?? "completed"}.";
            }
            catch (TimeoutException) { }
        }
        // Observation wrappers consume any later faults without closing the caller's destination.
        return $"Drain deadline exceeded. Exit={exit.Status}; stdout={copy?.Status}; stderr={stderr?.Status}; cancellation={cancellationError ?? "completed"}.";
    }

    private static async Task<string> ObserveBinaryCleanupAsync(Task? task)
    {
        if (task is null) return "not started";
        try { await task.ConfigureAwait(false); return "completed"; }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception error) { return error.GetType().Name; }
    }
}
