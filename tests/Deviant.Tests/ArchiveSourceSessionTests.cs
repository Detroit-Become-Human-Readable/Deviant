using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Tests;

public sealed class ArchiveSourceSessionTests
{
    [Fact]
    public async Task VerificationLeaseChecksOnlyConsumedVolumes()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 1, 0, 4, 0, 0), (17, 0, 2, 0, 4, 0, 1));
        fixture.WriteVolume(0, [1, 2, 3, 4]); fixture.WriteVolume(1, [5, 6, 7, 8]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var revisions = new RecordingRevisions();
        using var source = ArchiveSourceSession.Acquire(snapshot, revisions: revisions);
        using var other = await new SourceExtentVerifier().AcquireAsync(snapshot, [Extent(snapshot, 1, [5, 6, 7, 8])]);
        using var selected = await new SourceExtentVerifier().AcquireAsync(snapshot, [Extent(snapshot, 0, [1, 2, 3, 4])]);
        revisions.Captures.Clear();
        selected.EnsureUnchanged();
        Assert.Equal(2, revisions.Captures.GetValueOrDefault(fixture.Index));
        Assert.Equal(2, revisions.Captures.GetValueOrDefault(fixture.Volume(0)));
        Assert.Equal(0, revisions.Captures.GetValueOrDefault(fixture.Volume(1)));

        revisions.ChangedPath = fixture.Volume(1);
        selected.EnsureUnchanged();
        using var repeated = await new SourceExtentVerifier().AcquireAsync(snapshot, [Extent(snapshot, 0, [1, 2, 3, 4])]);
        repeated.EnsureUnchanged();
        Assert.Equal("SourceChanged", Assert.Throws<ArchiveException>(other.EnsureUnchanged).Code);
        Assert.Equal("SourceChanged", Assert.Throws<ArchiveException>(() => source.Session.EnsureCurrent()).Code);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("first")]
    [InlineData("second")]
    public async Task VerificationLeaseStillChecksIndexAndEveryConsumedVolume(string changed)
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 1, 0, 4, 0, 0), (17, 0, 2, 0, 4, 0, 1));
        fixture.WriteVolume(0, [1, 2, 3, 4]); fixture.WriteVolume(1, [5, 6, 7, 8]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var revisions = new RecordingRevisions();
        using var source = ArchiveSourceSession.Acquire(snapshot, revisions: revisions);
        using var selected = await new SourceExtentVerifier().AcquireAsync(snapshot,
            [Extent(snapshot, 0, [1, 2, 3, 4]), Extent(snapshot, 1, [5, 6, 7, 8])]);
        revisions.ChangedPath = changed switch { "index" => fixture.Index, "first" => fixture.Volume(0), _ => fixture.Volume(1) };
        Assert.Equal("SourceChanged", Assert.Throws<ArchiveException>(selected.EnsureUnchanged).Code);
    }

    private static SourceExtent Extent(ArchiveSnapshot snapshot, uint package, byte[] bytes) =>
        new(snapshot.Volumes.Single(volume => volume.Package == package).RelativeName, 0, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class RecordingRevisions : ISourceRevisionProvider
    {
        private readonly FileRevisionProvider inner = new();
        public ConcurrentDictionary<string, int> Captures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ChangedPath { get; set; }
        public FileStamp Capture(string path) => Record(path, inner.Capture(path));
        public FileStamp Capture(SafeFileHandle handle, string path) => Record(path, inner.Capture(handle, path));
        private FileStamp Record(string path, FileStamp stamp)
        {
            Captures.AddOrUpdate(path, 1, (_, count) => count + 1);
            return StringComparer.OrdinalIgnoreCase.Equals(ChangedPath, path)
                ? stamp with { LastWriteUtcTicks = stamp.LastWriteUtcTicks + 1 } : stamp;
        }
    }

    [Fact]
    public async Task RepeatedVerifiedRangesShareOneIndexAndExtentReadWhilePinned()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 9, 0, 4, 0, 0)); fixture.WriteVolume(0, [1, 2, 3, 4]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        using var measurements = new Measurements();
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(measurements);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
        var extent = new SourceExtent(snapshot.Volumes[0].RelativeName, 0, 4, hash);
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        { using var verified = await new SourceExtentVerifier().AcquireAsync(snapshot, [extent]); verified.EnsureUnchanged(); });
        await Task.WhenAll(tasks);
        if (snapshot.IndexStamp.Reliable && snapshot.Volumes[0].Stamp.Reliable)
        {
            Assert.Equal(1, measurements.Items.Count(item => item.Stage == "VerificationRead" && item.RelativeSource == Path.GetFileName(snapshot.IndexPath)));
            Assert.Equal(1, measurements.Items.Count(item => item.Stage == "VerificationRead" && item.RelativeSource == extent.RelativeSource));
        }
        using var borrowed = ArchiveSourceSession.TryAcquire(snapshot);
        Assert.NotNull(borrowed);
    }

    [Fact]
    public async Task ResolverKeepsIndependentBuffersAndTypedDuplicateCandidates()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 9, 0, 2, 0, 0), (18, 0, 9, 2, 2, 0, 0)); fixture.WriteVolume(0, [1, 2, 3, 4]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        using var source = ArchiveSourceSession.Acquire(snapshot);
        var resolver = new ArchiveResourceResolver(snapshot);
        Assert.Equal(ReferenceStatus.Ambiguous, resolver.Resolve(new(IdentifierNamespace.Archive, "9")).Status);
        Assert.Equal(1, Assert.Single(resolver.Resolve(new(IdentifierNamespace.Archive, "9", ResourceType: 18)).Candidates).Ordinal);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => resolver.ReadAsync(new(snapshot.IndexContentId, 1), 0, 2)));
        results[0][0] = 99;
        Assert.All(results.Skip(1), bytes => Assert.Equal(new byte[] { 3, 4 }, bytes));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ReadAsync(new(snapshot.IndexContentId, 0), 0, 2, cancelled.Token));
    }

    [Fact]
    public async Task BadExtentDigestIsRejectedAndVerificationLeaseOutlivesOwner()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 9, 0, 4, 0, 0)); fixture.WriteVolume(0, [1, 2, 3, 4]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var source = ArchiveSourceSession.Acquire(snapshot);
        var extent = new SourceExtent(snapshot.Volumes[0].RelativeName, 0, 4, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })));
        using var verified = await new SourceExtentVerifier().AcquireAsync(snapshot, [extent]);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new SourceExtentVerifier().AcquireAsync(snapshot, [extent with { PayloadSha256 = new string('0', 64) }]));
        Assert.Equal("SourceChanged", error.Code);
        source.Dispose(); verified.EnsureUnchanged(); verified.Dispose();
        Assert.Null(ArchiveSourceSession.TryAcquire(snapshot));
        // Final release removes every read-only source pin.
        fixture.WriteVolume(0, [4, 3, 2, 1]);
    }

    private sealed class Measurements : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly object gate = new();
        private readonly List<AssetLoadMeasurement> values = [];
        public IReadOnlyList<AssetLoadMeasurement> Items { get { lock (gate) return values.ToArray(); } }
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is AssetLoadMeasurement row) lock (gate) values.Add(row); }
        public void OnCompleted() { } public void OnError(Exception error) { } public void Dispose() { }
    }
}
