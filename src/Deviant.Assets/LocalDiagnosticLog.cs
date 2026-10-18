using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Deviant.Assets;

public sealed record WorkflowDiagnosticEvent(DateTimeOffset Timestamp, string BuildId, Guid OperationId,
    string Operation, string Phase, string State, double ElapsedMilliseconds, long? Completed = null,
    long? Total = null, string? Unit = null, long? CacheGeneration = null, string? AssetKey = null,
    string? MaterialKey = null, string? ErrorCode = null, string? Exception = null,
    string? ExceptionType = null, string[]? ExceptionFrames = null, bool Retryable = false,
    bool Partial = false, bool Truncated = false);

/// <summary>Bounded local diagnostics with asynchronous writes and an explicit redacted export.</summary>
public sealed class LocalDiagnosticLog : IAsyncDisposable
{
    private sealed record Pending(WorkflowDiagnosticEvent? Event, TaskCompletionSource? Flush = null);
    private readonly Channel<Pending> queue;
    private readonly Task writer;
    private readonly object fileGate = new();
    private readonly object failureGate = new();
    private WorkflowDiagnosticEvent? pendingFailure;
    private bool activeTailChecked;
    private long recoveredTailBytes;
    private readonly int maximumFileBytes, retainedFiles;
    private WorkflowDiagnosticEvent? lastFailure;
    private long dropped;
    private string? lastWriteError;
    private int disposed;
    public string DirectoryPath { get; }
    public string BuildId { get; }
    public WorkflowDiagnosticEvent? LastFailure => Volatile.Read(ref lastFailure);
    public long DroppedRecords => Interlocked.Read(ref dropped);
    public string? LastWriteError => Volatile.Read(ref lastWriteError);

    public LocalDiagnosticLog(string directory, string buildId, int maximumFileBytes = 2 * 1024 * 1024,
        int retainedFiles = 4, int queueCapacity = 1024)
    {
        if (maximumFileBytes < 4096 || maximumFileBytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (retainedFiles is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); Directory.CreateDirectory(DirectoryPath);
        if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("The log directory cannot be a link.");
        if (string.IsNullOrWhiteSpace(buildId) || buildId.Length > 256) throw new ArgumentException("Invalid build identifier.", nameof(buildId));
        BuildId = buildId; this.maximumFileBytes = maximumFileBytes; this.retainedFiles = retainedFiles;
        queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(queueCapacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        writer = Task.Run(WriteLoopAsync);
    }

    public WorkflowDiagnosticOperation Begin(string operation, string? assetKey = null, long? cacheGeneration = null,
        string? materialKey = null) => new(this, operation, assetKey, cacheGeneration, materialKey);

    internal void Record(WorkflowDiagnosticEvent value)
    {
        if (value.State == "Failed")
        {
            lock (failureGate)
            {
                if (disposed != 0) { Interlocked.Increment(ref dropped); return; }
                Volatile.Write(ref lastFailure, value);
                if (pendingFailure is not null) Interlocked.Increment(ref dropped);
                pendingFailure = value;
                queue.Writer.TryWrite(new(null));
            }
            return;
        }
        if (Volatile.Read(ref disposed) != 0 || !queue.Writer.TryWrite(new(value))) Interlocked.Increment(ref dropped);
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref disposed) != 0) { await writer.WaitAsync(token).ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await queue.Writer.WriteAsync(new(null, completion), token).ConfigureAwait(false); }
        catch (ChannelClosedException) { await writer.WaitAsync(token).ConfigureAwait(false); return; }
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var pending in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (pending.Event is { } value) WriteEvent(value);
            DrainFailure();
            pending.Flush?.TrySetResult();
        }
        DrainFailure();
    }

    private void DrainFailure()
    {
        WorkflowDiagnosticEvent? value;
        lock (failureGate) { value = pendingFailure; pendingFailure = null; }
        if (value is not null) WriteEvent(value);
    }

    private void WriteEvent(WorkflowDiagnosticEvent value)
    {
        try
        {
            string line = JsonSerializer.Serialize(value);
            while (Encoding.UTF8.GetByteCount(line) + 1 > maximumFileBytes)
            {
                value = value with { Operation = Shorten(value.Operation)!, Phase = Shorten(value.Phase)!,
                    Unit = Shorten(value.Unit), ErrorCode = Shorten(value.ErrorCode), ExceptionType = Shorten(value.ExceptionType),
                    AssetKey = Shorten(value.AssetKey), MaterialKey = Shorten(value.MaterialKey),
                    Exception = Shorten(value.Exception), ExceptionFrames = null, Truncated = true };
                line = JsonSerializer.Serialize(value);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (fileGate)
            {
                string active = LogPath(0);
                if (!activeTailChecked) { RepairTail(active); activeTailChecked = true; }
                if (File.Exists(active) && new FileInfo(active).Length > maximumFileBytes - bytes.Length)
                {
                    for (int index = retainedFiles - 1; index >= 1; index--)
                        if (File.Exists(LogPath(index - 1))) File.Move(LogPath(index - 1), LogPath(index), true);
                    if (retainedFiles == 1) File.Delete(active);
                }
                using var file = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Volatile.Write(ref lastWriteError, error.ToString()); Interlocked.Increment(ref dropped); }
    }

    private void RepairTail(string path)
    {
        if (!File.Exists(path)) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long length = file.Length;
        if (length == 0) return;
        file.Position = length - 1;
        if (file.ReadByte() == '\n') return;
        byte[] buffer = new byte[Math.Min(maximumFileBytes, 4096)];
        long position = length, minimum = Math.Max(0, length - maximumFileBytes), safeEnd = 0;
        while (position > minimum)
        {
            long start = Math.Max(minimum, position - buffer.Length);
            int count = checked((int)(position - start)); file.Position = start; file.ReadExactly(buffer.AsSpan(0, count));
            int newline = buffer.AsSpan(0, count).LastIndexOf((byte)'\n');
            if (newline >= 0) { safeEnd = start + newline + 1; break; }
            position = start;
        }
        if (length - safeEnd <= maximumFileBytes)
        {
            byte[] tail = new byte[checked((int)(length - safeEnd))]; file.Position = safeEnd; file.ReadExactly(tail);
            try
            {
                if (JsonSerializer.Deserialize<WorkflowDiagnosticEvent>(tail) is not null)
                { file.Position = length; file.WriteByte((byte)'\n'); return; }
            }
            catch (JsonException) { }
        }
        file.SetLength(safeEnd); Interlocked.Add(ref recoveredTailBytes, length - safeEnd);
    }

    private static string? Shorten(string? value) => value is null ? null : value.Length <= 8 ? "" : value[..(value.Length / 2)];
    private string LogPath(int index) => Path.Combine(DirectoryPath, $"diagnostics-{index}.jsonl");

    public async Task ExportRedactedAsync(string destination, CancellationToken token = default)
    {
        string fullDestination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetDirectoryName(fullDestination), DirectoryPath, comparison) &&
            Path.GetFileName(fullDestination).StartsWith("diagnostics-", comparison) &&
            fullDestination.EndsWith(".jsonl", comparison))
            throw new ArgumentException("Choose a destination outside the log files.", nameof(destination));
        await FlushAsync(token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            string temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    using (var text = new StreamWriter(archive.CreateEntry("diagnostics.jsonl").Open(), new UTF8Encoding(false)))
                    lock (fileGate)
                    {
                        var latest = LastFailure; bool latestIncluded = false;
                        for (int index = retainedFiles - 1; index >= 0; index--)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!File.Exists(LogPath(index))) continue;
                            foreach (string line in File.ReadLines(LogPath(index)))
                            {
                                token.ThrowIfCancellationRequested(); WorkflowDiagnosticEvent? value;
                                try { value = JsonSerializer.Deserialize<WorkflowDiagnosticEvent>(line); } catch (JsonException) { continue; }
                                if (value is null) continue;
                                text.WriteLine(Redacted(value));
                                latestIncluded |= value.State == "Failed" && value.OperationId == latest?.OperationId;
                            }
                        }
                        if (!latestIncluded && latest is not null) text.WriteLine(Redacted(latest));
                    }
                    using var summary = new StreamWriter(archive.CreateEntry("summary.json").Open(), new UTF8Encoding(false));
                    summary.Write(JsonSerializer.Serialize(new { BuildId = RedactBuildId(BuildId), DroppedRecords, WriteFailure = LastWriteError is not null,
                        RecoveredTailBytes = Interlocked.Read(ref recoveredTailBytes),
                        Redaction = "Free text, paths, messages and identifiers omitted or fingerprinted. No dumps included." }));
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, fullDestination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token).ConfigureAwait(false);
    }

    private static string Redacted(WorkflowDiagnosticEvent value) => JsonSerializer.Serialize(new
    {
        value.Timestamp, BuildId = RedactBuildId(value.BuildId), value.OperationId,
        Operation = Fingerprint(value.Operation), Phase = Fingerprint(value.Phase), value.State,
        value.ElapsedMilliseconds, value.Completed, value.Total, Unit = Fingerprint(value.Unit),
        value.CacheGeneration, AssetKey = Fingerprint(value.AssetKey), MaterialKey = Fingerprint(value.MaterialKey),
        ErrorCode = Fingerprint(value.ErrorCode), value.ExceptionType, value.ExceptionFrames,
        value.Retryable, value.Partial, value.Truncated
    });

    private static string? RedactBuildId(string? value) => value is not null &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_') ? value : Fingerprint(value);

    private static string? Fingerprint(string? value) => value is null ? null :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    public async ValueTask DisposeAsync()
    {
        lock (failureGate)
            if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }
}

public sealed class WorkflowDiagnosticOperation : IDisposable
{
    private readonly LocalDiagnosticLog log;
    private readonly string operation;
    private readonly string? asset, material;
    private long? generation;
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private long lastProgress;
    private string phase = "Starting";
    private long? completed, total;
    private string? unit;
    private bool terminal;
    public Guid Id { get; } = Guid.NewGuid();

    internal WorkflowDiagnosticOperation(LocalDiagnosticLog log, string operation, string? asset, long? generation, string? material)
    {
        this.log = log; this.operation = operation; this.asset = asset; this.generation = generation; this.material = material;
        log.Record(Create("Started"));
    }
    private WorkflowDiagnosticEvent Create(string state) => new(DateTimeOffset.UtcNow, log.BuildId, Id,
        operation, phase, state, Stopwatch.GetElapsedTime(started).TotalMilliseconds, completed, total, unit, generation, asset, material);
    public void Progress(string phase, long? completed = null, long? total = null, string? unit = null, long? cacheGeneration = null)
    {
        lock (gate)
        {
            if (terminal) return;
            bool changed = this.phase != phase || cacheGeneration is not null && generation != cacheGeneration;
            if (cacheGeneration is not null) generation = cacheGeneration;
            this.phase = phase; this.completed = completed; this.total = total; this.unit = unit;
            if (!changed && lastProgress != 0 && Stopwatch.GetElapsedTime(lastProgress) < TimeSpan.FromSeconds(1)) return;
            lastProgress = Stopwatch.GetTimestamp(); log.Record(Create("Progress"));
        }
    }
    public void Fail(Exception error, string? code = null, bool retryable = false, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            if (terminal) return; terminal = true;
            string exception = error.ToString(); bool truncated = exception.Length > 64 * 1024;
            if (truncated) exception = exception[..(64 * 1024)];
            var frames = new StackTrace(error, false).GetFrames()?.Select(frame => frame.GetMethod())
                .Where(method => method is not null).Select(method => method!.DeclaringType?.FullName + "." + method.Name).Take(128).ToArray();
            log.Record(Create("Failed") with { ErrorCode = code ?? error.GetType().Name, Exception = exception,
                ExceptionType = error.GetType().FullName, ExceptionFrames = frames, Retryable = retryable, Partial = partial, Truncated = truncated });
        }
    }
    public void Complete() => Finish("Completed");
    public void Cancel() => Finish("Cancelled");
    public void Partial() => Finish("Partial", true);
    private void Finish(string state, bool partial = false) { lock (gate) { if (terminal) return; terminal = true; log.Record(Create(state) with { Partial = partial }); } }
    public void Dispose() => Finish("Stopped");
}
