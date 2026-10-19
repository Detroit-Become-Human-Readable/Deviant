using System.Diagnostics;
using System.Text;

namespace Deviant.Audio;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    long MaximumWorkingSetBytes = 0,
    IReadOnlyList<string>? MonitoredOutputFiles = null,
    long MaximumOutputFileBytes = (long)uint.MaxValue + 8);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed);

public sealed partial class ProcessRunner(long defaultMaximumWorkingSetBytes = 2L * 1024 * 1024 * 1024)
{
    private const int OutputLimit = 2 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) start.WorkingDirectory = request.WorkingDirectory;
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        if (request.Environment is not null)
            foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var clock = Stopwatch.StartNew();
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        using var timeoutSource = request.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linkedSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start process '{request.FileName}'.");
            stdoutTask = ReadBoundedAsync(process.StandardOutput, OutputLimit, linkedSource.Token);
            stderrTask = ReadBoundedAsync(process.StandardError, OutputLimit, linkedSource.Token);
            var waitTask = process.WaitForExitAsync(linkedSource.Token);
            while (!waitTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                linkedSource.Token.ThrowIfCancellationRequested();
                if (ExceedsWorkingSet(process, maximumWorkingSetBytes))
                {
                    TryKillTree(process);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await ObserveReaderAsync(stdoutTask).ConfigureAwait(false);
                    await ObserveReaderAsync(stderrTask).ConfigureAwait(false);
                    throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its working-set limit.",
                        $"Limit: {maximumWorkingSetBytes} bytes.");
                }
                if (ExceedsOutputLimit(request))
                {
                    TryKillTree(process);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await ObserveReaderAsync(stdoutTask).ConfigureAwait(false);
                    await ObserveReaderAsync(stderrTask).ConfigureAwait(false);
                    throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its output-file limit.",
                        $"Limit: {request.MaximumOutputFileBytes} bytes.");
                }
                await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(100), linkedSource.Token)).ConfigureAwait(false);
            }
            await waitTask.ConfigureAwait(false);
            if (ExceedsOutputLimit(request))
                throw new AudioToolException($"Process '{Path.GetFileName(request.FileName)}' exceeded its output-file limit.",
                    $"Limit: {request.MaximumOutputFileBytes} bytes.");
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            clock.Stop();
            return new ProcessResult(process.ExitCode, stdout, stderr, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { throw new IOException($"Could not stop '{Path.GetFileName(request.FileName)}'."); }
            await ObserveReaderAsync(stdoutTask).ConfigureAwait(false);
            await ObserveReaderAsync(stderrTask).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && timeoutSource?.IsCancellationRequested == true)
                throw new TimeoutException($"Process '{Path.GetFileName(request.FileName)}' exceeded its time limit.");
            throw;
        }
        catch
        {
            TryKillTree(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(limit, 8192));
        var buffer = new char[8192];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var take = Math.Min(read, Math.Max(0, limit - result.Length));
            if (take > 0) result.Append(buffer, 0, take);
            if (!truncated && take != read && result.Length == limit)
            {
                result.Append("\n[output truncated]");
                truncated = true;
            }
        }
        return result.ToString();
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static bool ExceedsWorkingSet(Process process, long maximumBytes)
    {
        try { return !process.HasExited && process.WorkingSet64 > maximumBytes; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool ExceedsOutputLimit(ProcessRequest request)
    {
        if (request.MaximumOutputFileBytes <= 0) return true;
        if (request.MonitoredOutputFiles is null) return false;
        foreach (var path in request.MonitoredOutputFiles)
            if (File.Exists(path) && new FileInfo(path).Length > request.MaximumOutputFileBytes) return true;
        return false;
    }

    private static async Task ObserveReaderAsync(Task<string>? readerTask)
    {
        if (readerTask is null) return;
        try { await readerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
    }
}
