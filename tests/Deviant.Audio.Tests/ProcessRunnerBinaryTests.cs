using System.Diagnostics;
using System.Globalization;
using Deviant.Audio;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class ProcessRunnerBinaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-process-stream-" + Guid.NewGuid().ToString("N"));
    public ProcessRunnerBinaryTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task BinaryOutputPreservesBytesAndLeavesTheDestinationOpen()
    {
        const int bytes = 300_007;
        using var destination = new ObservedStream();
        var request = Request(bytes, errors: 3 * 1024 * 1024, exitCode: 7);
        var result = await new ProcessRunner().RunBinaryAsync(request, destination, bytes);
        Assert.Equal(7, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("[output truncated]", result.StandardError);
        Assert.InRange(result.StandardError.Length, 1, 2 * 1024 * 1024 + 32);
        Assert.Equal(bytes, destination.Length);
        Assert.All(destination.ToArray().Select((value, index) => (value, index)), pair => Assert.Equal((byte)(pair.index % 251), pair.value));
        Assert.InRange(destination.MaximumWrite, 1, 128 * 1024);
        Assert.True(destination.CanWrite);
        AssertStopped();
    }

    [Fact]
    public async Task ByteLimitStopsTheProducerWithoutPublishingExcessBytes()
    {
        using var destination = new ObservedStream();
        var error = await Assert.ThrowsAsync<AudioToolException>(() => new ProcessRunner().RunBinaryAsync(
            Request(16 * 1024 * 1024, linger: 10_000), destination, 8193));
        Assert.Contains("output", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(destination.Length, 0, 8193);
        Assert.True(destination.CanWrite);
        AssertStopped();
    }

    [Fact]
    public async Task DestinationFailurePreservesTheOriginalExceptionAndDrainsTheChild()
    {
        var expected = new IOException("Generated admission refusal");
        expected.Data["ErrorCode"] = "CacheStorageLimit";
        using var destination = new ObservedStream(expected);
        var actual = await Assert.ThrowsAsync<IOException>(() => new ProcessRunner().RunBinaryAsync(
            Request(16 * 1024 * 1024, errors: 3 * 1024 * 1024, linger: 10_000), destination, 32 * 1024 * 1024));
        Assert.Same(expected, actual);
        Assert.Equal("CacheStorageLimit", actual.Data["ErrorCode"]);
        Assert.True(destination.CanWrite);
        AssertStopped();
    }

    [Fact]
    public async Task CancellationKillsAndDrainsTheProducer()
    {
        using var cancellation = new CancellationTokenSource();
        using var destination = new ObservedStream(onWrite: cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunBinaryAsync(
            Request(16 * 1024 * 1024, linger: 10_000), destination, 32 * 1024 * 1024, cancellation.Token));
        Assert.True(destination.CanWrite);
        AssertStopped();
    }

    [Fact]
    public async Task TimeoutStopsAnIdleProducer()
    {
        using var destination = new ObservedStream();
        await Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunBinaryAsync(
            Request(0, linger: 10_000) with { Timeout = TimeSpan.FromSeconds(2) }, destination, 1024));
        Assert.True(destination.CanWrite);
        AssertStopped();
    }

    [Fact]
    public async Task WorkingSetLimitStopsTheProducer()
    {
        using var destination = new ObservedStream();
        var error = await Assert.ThrowsAsync<AudioToolException>(() => new ProcessRunner().RunBinaryAsync(
            Request(0, linger: 10_000) with { MaximumWorkingSetBytes = 1 }, destination, 1024));
        Assert.Contains("working-set", error.Message, StringComparison.OrdinalIgnoreCase);
        // Admission may stop the runtime before managed entry writes its ready file.
        if (File.Exists(ReadyPath)) AssertStopped();
    }

    [Fact]
    public async Task MonitoredFileLimitRemainsEnforced()
    {
        var monitored = Path.Combine(root, "output.bin");
        await File.WriteAllBytesAsync(monitored, new byte[2048]);
        using var destination = new ObservedStream();
        var error = await Assert.ThrowsAsync<AudioToolException>(() => new ProcessRunner().RunBinaryAsync(
            Request(0, linger: 10_000) with { MonitoredOutputFiles = [monitored], MaximumOutputFileBytes = 1024 }, destination, 1024));
        Assert.Contains("output-file", error.Message, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(ReadyPath)) AssertStopped();
    }

    [Fact]
    public async Task InvalidInputAndPreCancellationDoNotStartAChild()
    {
        using var destination = new ObservedStream();
        var request = Request(10);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ProcessRunner().RunBinaryAsync(request, destination, 0));
        using var readOnly = new MemoryStream([], writable: false);
        await Assert.ThrowsAsync<ArgumentException>(() => new ProcessRunner().RunBinaryAsync(request, readOnly, 10));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunBinaryAsync(request, destination, 10, cancelled.Token));
        Assert.False(File.Exists(ReadyPath));
    }

    private string ReadyPath => Path.Combine(root, "ready.txt");
    private ProcessRequest Request(long bytes, int errors = 0, int linger = 0, int exitCode = 0) => new(
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        [Path.Combine(AppContext.BaseDirectory, "fixtures", "BinaryOutputWorker.dll"),
            bytes.ToString(CultureInfo.InvariantCulture), errors.ToString(CultureInfo.InvariantCulture),
            linger.ToString(CultureInfo.InvariantCulture), exitCode.ToString(CultureInfo.InvariantCulture), ReadyPath],
        Timeout: TimeSpan.FromSeconds(8));

    private void AssertStopped()
    {
        var parts = File.ReadAllText(ReadyPath).Split(':');
        var pid = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var birth = long.Parse(parts[1], CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited || process.StartTime.ToUniversalTime().Ticks != birth, "The owned child is still running.");
        }
        catch (ArgumentException) { }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class ObservedStream(Exception? failure = null, Action? onWrite = null) : MemoryStream
    {
        public int MaximumWrite { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
            if (failure is not null) return ValueTask.FromException(failure);
            var result = base.WriteAsync(buffer, cancellationToken);
            onWrite?.Invoke();
            return result;
        }
    }
}
