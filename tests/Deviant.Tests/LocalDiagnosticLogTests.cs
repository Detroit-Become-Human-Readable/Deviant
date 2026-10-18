using System.IO.Compression;
using System.Text.Json;
using Deviant.Assets;
using Xunit;

namespace Deviant.Tests;

public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public async Task FailureKeepsTheExceptionChainAndOperationContext()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        using var operation = log.Begin("Generated.Operation", "generated:1", 17);
        operation.Progress("Reading", 2, 3, "items");
        operation.Fail(new InvalidOperationException("Outer generated failure", new IOException("Inner generated failure")), "GeneratedFailure");
        await log.FlushAsync();
        var failure = Assert.IsType<WorkflowDiagnosticEvent>(log.LastFailure);
        Assert.Equal(operation.Id, failure.OperationId); Assert.Equal(17, failure.CacheGeneration);
        Assert.Contains("Outer generated failure", failure.Exception); Assert.Contains("Inner generated failure", failure.Exception);
        var saved = Read(fixture.Root).Single(value => value.State == "Failed");
        Assert.Equal("generated-build", saved.BuildId); Assert.Equal(2, saved.Completed); Assert.Equal(3, saved.Total);
        Assert.Equal("Reading", saved.Phase); Assert.Equal(failure.Exception, saved.Exception);
    }

    [Fact]
    public async Task RotationBoundsTheOwnedLogsAndKeepsTheLatestFailure()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build", maximumFileBytes: 4096, retainedFiles: 3);
        for (int index = 0; index < 24; index++)
        {
            using var operation = log.Begin("Generated.Operation");
            operation.Fail(new IOException(new string('x', 500)), "GeneratedFailure");
            await log.FlushAsync();
        }
        var files = Directory.GetFiles(fixture.Root, "diagnostics-*.jsonl");
        Assert.InRange(files.Length, 1, 3); Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 4096));
        Assert.Contains(Read(fixture.Root), value => value.OperationId == log.LastFailure!.OperationId);
    }

    [Fact]
    public async Task RedactedBundleOmitsMessagesPathsAndAssetText()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        using var operation = log.Begin("Private dialogue words", "PrivateAssetText");
        operation.Progress("Private scene words", 1, 2, "items");
        operation.Fail(new IOException("Private dialogue words " + Path.Combine(fixture.Root, "private-source.bin")), "GeneratedFailure");
        string bundle = Path.Combine(fixture.Root, "diagnostics.zip");
        await log.ExportRedactedAsync(bundle);
        using var archive = ZipFile.OpenRead(bundle);
        Assert.DoesNotContain(archive.Entries, entry => entry.Name.EndsWith(".dmp", StringComparison.Ordinal));
        using var reader = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open());
        string exported = await reader.ReadToEndAsync();
        Assert.DoesNotContain(fixture.Root, exported); Assert.DoesNotContain("PrivateAssetText", exported);
        Assert.DoesNotContain("Private dialogue words", exported); Assert.DoesNotContain("Private scene words", exported);
        Assert.Contains("System.IO.IOException", exported); Assert.Contains("generated-build", exported);
    }

    [Fact]
    public async Task CancelledOperationsNeverBecomeSuccessfulOnDisposal()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        Guid id;
        using (var operation = log.Begin("Generated.Operation")) { id = operation.Id; operation.Cancel(); }
        await log.FlushAsync();
        var records = Read(fixture.Root).Where(value => value.OperationId == id).ToArray();
        Assert.Contains(records, value => value.State == "Cancelled"); Assert.DoesNotContain(records, value => value.State == "Completed");
    }

    [Fact]
    public async Task RestartDiscardsAnIncompleteTailBeforeAppendingNewRecords()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "diagnostics-0.jsonl"), "{\"Timestamp\":");
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        using var operation = log.Begin("Generated.Operation"); operation.Complete(); await log.FlushAsync();
        Assert.Contains(Read(fixture.Root), value => value.OperationId == operation.Id && value.State == "Started");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportCannotOverwriteAnOwnedLogFile(bool trailingSeparator)
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""), "generated-build");
        using var operation = log.Begin("Generated.Operation"); operation.Complete(); await log.FlushAsync();
        string active = Path.Combine(fixture.Root, "diagnostics-0.jsonl"); byte[] before = await File.ReadAllBytesAsync(active);
        await Assert.ThrowsAsync<ArgumentException>(() => log.ExportRedactedAsync(active));
        Assert.Equal(before, await File.ReadAllBytesAsync(active));
    }

    [Fact]
    public async Task SaturatedQueueStillPersistsAndExportsTheLatestFailure()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build", queueCapacity: 1);
        var fileGate = typeof(LocalDiagnosticLog).GetField("fileGate", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!.GetValue(log)!;
        lock (fileGate)
            for (int index = 0; index < 100; index++)
            {
                using var operation = log.Begin("Generated.Operation"); operation.Fail(new IOException("Generated failure " + index));
            }
        await log.FlushAsync();
        var latest = log.LastFailure!;
        Assert.Contains(Read(fixture.Root), value => value.OperationId == latest.OperationId && value.State == "Failed");
        string bundle = Path.Combine(fixture.Root, "diagnostics.zip"); await log.ExportRedactedAsync(bundle);
        using var archive = ZipFile.OpenRead(bundle); using var reader = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open());
        Assert.Contains(latest.OperationId.ToString(), await reader.ReadToEndAsync());
        Assert.True(log.DroppedRecords > 0);
    }

    [Fact]
    public async Task UnwritableLogDoesNotLoseTheErrorFromTheDiagnosticExport()
    {
        using var fixture = new Fixture();
        string active = Path.Combine(fixture.Root, "diagnostics-0.jsonl");
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        using (var held = new FileStream(active, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var operation = log.Begin("Generated.Operation"); operation.Fail(new IOException("Generated failure"));
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(log.LastWriteError); Assert.True(log.DroppedRecords > 0);
        }
        string bundle = Path.Combine(fixture.Root, "diagnostics.zip"); await log.ExportRedactedAsync(bundle);
        using var archive = ZipFile.OpenRead(bundle); using var reader = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open());
        Assert.Contains(log.LastFailure!.OperationId.ToString(), await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task RedactedBundleRemovesPrivateBuildPathsFromEveryEntry()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, Path.Combine(fixture.Root, "PrivateBuildMarker"));
        using var operation = log.Begin("Generated.Operation"); operation.Complete();
        string bundle = Path.Combine(fixture.Root, "diagnostics.zip"); await log.ExportRedactedAsync(bundle);
        using var archive = ZipFile.OpenRead(bundle);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("PrivateBuildMarker", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task ProgressAndFailureCarryTheLatestPublishedCacheGeneration()
    {
        using var fixture = new Fixture();
        await using var log = new LocalDiagnosticLog(fixture.Root, "generated-build");
        using var operation = log.Begin("Generated.Operation", cacheGeneration: 7);
        operation.Progress("Reading", 1, 2, "items", cacheGeneration: 8);
        operation.Fail(new IOException("Generated failure"));
        await log.FlushAsync(); Assert.Equal(8, log.LastFailure!.CacheGeneration);
        Assert.Contains(Read(fixture.Root), value => value.State == "Progress" && value.CacheGeneration == 8);
    }

    private static WorkflowDiagnosticEvent[] Read(string root) => Directory.GetFiles(root, "diagnostics-*.jsonl")
        .SelectMany(File.ReadLines).Select(line => JsonSerializer.Deserialize<WorkflowDiagnosticEvent>(line)!).ToArray();
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantDiagnosticTests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
