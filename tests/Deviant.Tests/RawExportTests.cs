using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Core;
using Deviant.Export;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Tests;

public sealed class RawExportTests
{
    [Fact]
    public async Task RawBytesAndZeroLengthFilesCommitWithHashes()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((17, 2, 9, 1, 3, 20, 0), (17, 2, 9, 4, 0, 0, 0)); f.WriteVolume(0, [0, 7, 8, 9]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var service = new RawExportService(); var plan = service.Plan(s, new(f.Output), s.Records);
        var report = await service.RunAsync(plan); Assert.All(report.Manifest.Entries, e => Assert.Equal(ExportStatus.Completed, e.Status));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(f.Output, plan.Items[0].RelativeOutput)));
        Assert.Empty(File.ReadAllBytes(Path.Combine(f.Output, plan.Items[1].RelativeOutput)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 7, 8, 9 })).ToLowerInvariant(), report.Manifest.Entries[0].Sha256);
        Assert.NotEqual(plan.Items[0].RelativeOutput, plan.Items[1].RelativeOutput); Assert.True(File.Exists(report.ManifestPath));
    }
    [Fact]
    public async Task CancellationManifestPreservesCompletedSkippedAndFailedEntries()
    {
        using var f = new ArchiveFixture(); var data = new byte[2 * 1024 * 1024]; Array.Fill(data, (byte)17); f.WriteVolume(0, data);
        f.WriteIndex((1, 1, 1, 0, 1, 0, 0), (2, 1, 2, 0, 1, 0, 7), (3, 1, 3, 0, 1, 0, 0), (4, 1, 4, 0, (uint)data.Length, 0, 0), (5, 1, 5, 1, 1, 0, 0));
        var s = await new ArchiveReader().OpenAsync(f.Index); var service = new RawExportService(); var plan = service.Plan(s, new(f.Output), s.Records);
        var existing = Path.Combine(f.Output, plan.Items[2].RelativeOutput); Directory.CreateDirectory(Path.GetDirectoryName(existing)!); File.WriteAllBytes(existing, [99]);
        using var stop = new CancellationTokenSource();
        var report = await service.RunAsync(plan, new InlineProgress<ExportProgress>(p => { if (p.Ordinal == 3 && p.BytesWritten > 0) stop.Cancel(); }), stop.Token);
        Assert.Equal(new[] { ExportStatus.Completed, ExportStatus.Failed, ExportStatus.Skipped, ExportStatus.Cancelled, ExportStatus.Cancelled }, report.Manifest.Entries.Select(e => e.Status));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(existing)); Assert.Empty(Directory.GetFiles(f.Output, "*.tmp", SearchOption.AllDirectories));
        using var manifest = JsonDocument.Parse(File.ReadAllText(report.ManifestPath)); Assert.Equal(5, manifest.RootElement.GetProperty("Entries").GetArrayLength());
    }
    [Fact]
    public async Task ExplicitOverwriteReplacesOnlyAfterSuccessfulCopy()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 3, 0, 0)); f.WriteVolume(0, [1, 2, 3]); var s = await new ArchiveReader().OpenAsync(f.Index);
        var service = new RawExportService(); var plan = service.Plan(s, new(f.Output, true), s.Records); var destination = Path.Combine(f.Output, plan.Items[0].RelativeOutput);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, [7]);
        var report = await service.RunAsync(plan); Assert.Equal(ExportStatus.Completed, report.Manifest.Entries[0].Status); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }
    [Fact]
    public async Task ChangedIndexFailsEveryPlannedEntry()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 1, 0, 0), (1, 1, 2, 1, 1, 0, 0)); f.WriteVolume(0, [1, 2]); var s = await new ArchiveReader().OpenAsync(f.Index);
        var bytes = File.ReadAllBytes(f.Index); bytes[40] = 5; File.WriteAllBytes(f.Index, bytes);
        var service = new RawExportService(); var report = await service.RunAsync(service.Plan(s, new(f.Output), s.Records));
        Assert.All(report.Manifest.Entries, e => { Assert.Equal(ExportStatus.Failed, e.Status); Assert.Equal("SourceChanged", e.ErrorCode); });
        Assert.Empty(Directory.GetFiles(f.Output, "*.bin", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task ReplacedSameLengthVolumeWithPreservedWriteTimeIsDetected()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 2, 0, 0)); f.WriteVolume(0, [1, 2]); var s = await new ArchiveReader().OpenAsync(f.Index);
        var replacement = Path.Combine(f.Root, "replacement"); File.WriteAllBytes(replacement, [7, 8]); File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(f.Volume(0))); File.Move(replacement, f.Volume(0), true);
        var service = new RawExportService(); var result = await service.RunAsync(service.Plan(s, new(f.Output), s.Records)); Assert.Equal("SourceChanged", result.Manifest.Entries[0].ErrorCode);
    }
    [Fact]
    public async Task ExistingWriterProducesSourceBusy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 1, 0, 0)); f.WriteVolume(0, [1]); var s = await new ArchiveReader().OpenAsync(f.Index);
        using var writer = new FileStream(f.Volume(0), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var service = new RawExportService(); var result = await service.RunAsync(service.Plan(s, new(f.Output), s.Records)); Assert.Equal("SourceBusy", result.Manifest.Entries[0].ErrorCode);
    }
    [Fact]
    public async Task MidReadChangeBeatsCancellationAndBlocksAffectedVolume()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 1024 * 1024, 0, 0), (2, 1, 2, 0, 1, 0, 0), (3, 1, 3, 0, 1, 0, 1));
        f.WriteVolume(0, new byte[1024 * 1024]); f.WriteVolume(1, [9]); var s = await new ArchiveReader().OpenAsync(f.Index);
        var provider = new ChangingProvider(f.Volume(0)); var service = new RawExportService(provider); using var stop = new CancellationTokenSource();
        var report = await service.RunAsync(service.Plan(s, new(f.Output), s.Records), new InlineProgress<ExportProgress>(p => { provider.Changed = true; stop.Cancel(); }), stop.Token);
        Assert.Equal("SourceChanged", report.Manifest.Entries[0].ErrorCode); Assert.Equal("SourceChanged", report.Manifest.Entries[1].ErrorCode); Assert.Equal(ExportStatus.Cancelled, report.Manifest.Entries[2].Status);
        Assert.Empty(Directory.GetFiles(f.Output, "*.bin", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(f.Output, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task MidReadIndexChangePreventsCurrentCommitAndStopsRemainingBatch()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 500000, 0, 0), (2, 1, 2, 0, 1, 0, 1)); f.WriteVolume(0, new byte[500000]); f.WriteVolume(1, [7]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var provider = new ChangingProvider(f.Index); var service = new RawExportService(provider);
        var report = await service.RunAsync(service.Plan(s, new(f.Output), s.Records), new InlineProgress<ExportProgress>(_ => provider.Changed = true));
        Assert.All(report.Manifest.Entries, e => Assert.Equal("SourceChanged", e.ErrorCode)); Assert.Empty(Directory.GetFiles(f.Output, "*.bin", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task BusyOrChangedVolumeDoesNotBlockOtherVolumes()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 500000, 0, 0), (2, 1, 2, 0, 1, 0, 1)); f.WriteVolume(0, new byte[500000]); f.WriteVolume(1, [7]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var provider = new ChangingProvider(f.Volume(0)); var service = new RawExportService(provider);
        var report = await service.RunAsync(service.Plan(s, new(f.Output), s.Records), new InlineProgress<ExportProgress>(p => { if (p.Ordinal == 0) provider.Changed = true; }));
        Assert.Equal(ExportStatus.Failed, report.Manifest.Entries[0].Status); Assert.Equal(ExportStatus.Completed, report.Manifest.Entries[1].Status);
    }
    [Fact]
    public async Task CancellationDoesNotReplaceExistingOutput()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 500000, 0, 0)); f.WriteVolume(0, new byte[500000]); var s = await new ArchiveReader().OpenAsync(f.Index);
        var service = new RawExportService(); var plan = service.Plan(s, new(f.Output, true), s.Records); var dest = Path.Combine(f.Output, plan.Items[0].RelativeOutput);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, [8]); using var stop = new CancellationTokenSource();
        var report = await service.RunAsync(plan, new InlineProgress<ExportProgress>(_ => stop.Cancel()), stop.Token);
        Assert.Equal(ExportStatus.Cancelled, report.Manifest.Entries[0].Status); Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(dest));
    }
    [Fact]
    public async Task CallerCannotForgeSelectedRange()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 1, 0, 0)); f.WriteVolume(0, [1]); var s = await new ArchiveReader().OpenAsync(f.Index);
        Assert.Equal("InvalidSelection", Assert.Throws<ArchiveException>(() => new RawExportService().Plan(s, new(f.Output), [s.Records[0] with { Offset = 100 }])).Code);
    }
    private sealed class ChangingProvider(string changedPath) : ISourceRevisionProvider
    {
        private readonly FileRevisionProvider inner = new(); public bool Changed { get; set; }
        private FileStamp Change(FileStamp stamp, string path) => Changed && path == changedPath ? stamp with { ChangeTimeUtcTicks = stamp.ChangeTimeUtcTicks + 1 } : stamp;
        public FileStamp Capture(string path) => Change(inner.Capture(path), path);
        public FileStamp Capture(SafeFileHandle handle, string path) => Change(inner.Capture(handle, path), path);
    }

    [Fact]
    public async Task ManifestFailureRetainsCommittedFilesAndCompleteReport()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 3, 0, 0)); f.WriteVolume(0, [1, 2, 3]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var service = new RawExportService(manifestWriter: new FailingWriter());
        var plan = service.Plan(s, new(f.Output), s.Records);
        var failure = await Assert.ThrowsAsync<ManifestWriteException>(() => service.RunAsync(plan));
        Assert.Equal(ExportStatus.Completed, Assert.Single(failure.Manifest.Entries).Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Output, plan.Items[0].RelativeOutput)));
        Assert.Empty(Directory.GetFiles(f.Output, "*.tmp", SearchOption.AllDirectories));
    }
    private sealed class FailingWriter : IExportManifestWriter
    { public Task WriteAsync(string path, ExportManifest manifest) => Task.FromException(new IOException("Simulated report storage failure.")); }

    [Fact]
    public async Task DirectoryLeasePreventsOutputAncestorRenameDuringCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 500000, 0, 0)); f.WriteVolume(0, new byte[500000]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var service = new RawExportService(); var attempted = false;
        var result = await service.RunAsync(service.Plan(s, new(f.Output), s.Records), new InlineProgress<ExportProgress>(p =>
        {
            if (attempted || p.BytesWritten == 0) return; attempted = true;
            Assert.ThrowsAny<IOException>(() => Directory.Move(f.Output, f.Output + "-moved"));
        }));
        Assert.True(attempted); Assert.Equal(ExportStatus.Completed, result.Manifest.Entries[0].Status);
    }
    [Fact]
    public async Task SourceLeasePreventsWriterDuringCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 500000, 0, 0)); f.WriteVolume(0, new byte[500000]);
        var s = await new ArchiveReader().OpenAsync(f.Index); var service = new RawExportService(); var attempted = false;
        var result = await service.RunAsync(service.Plan(s, new(f.Output), s.Records), new InlineProgress<ExportProgress>(_ =>
        {
            if (attempted) return; attempted = true;
            Assert.ThrowsAny<IOException>(() => { using var writer = new FileStream(f.Volume(0), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
        }));
        Assert.True(attempted); Assert.Equal(ExportStatus.Completed, result.Manifest.Entries[0].Status);
    }
}
