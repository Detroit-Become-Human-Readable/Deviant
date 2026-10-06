using System.Security.Cryptography;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationPayloadVerifierTests
{
    [Fact]
    public async Task ReaderStoresSha256OfTheExactBytesParsed()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("SAMPLE_KEY", "A source line")]))));
        var snapshot = await fixture.BuildAsync();
        var row = Assert.Single(snapshot.Records);
        var bytes = await File.ReadAllBytesAsync(fixture.VolumePath);

        var parsed = await new LocalizationSnapshotReader().ReadResourceAsync(snapshot, row);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes.AsSpan(checked((int)row.Offset), checked((int)row.StoredSize)))).ToLowerInvariant(),
            parsed.InputSha256);
    }

    [Fact]
    public async Task VerifiesEachResourceDigestAndDetectsSameLengthPayloadEditsAndTruncation()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("FIRST_KEY", "First text")]))));
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("FRE", [LocalizationFixture.DirectText("SECOND_KEY", "Deuxième texte")]))));
        var snapshot = await fixture.BuildAsync();
        var rows = snapshot.Records.ToArray();
        var bytes = await File.ReadAllBytesAsync(fixture.VolumePath);
        var expected = rows.ToDictionary(row => row.Ordinal, row =>
            Convert.ToHexString(SHA256.HashData(bytes.AsSpan(checked((int)row.Offset), checked((int)row.StoredSize)))).ToLowerInvariant());
        var revisions = new FrozenRevisionProvider(snapshot);
        var verifier = new LocalizationPayloadVerifier();

        await using (var verified = await verifier.VerifyAsync(snapshot, expected, rows, revisions))
        {
            Assert.True(verified.DigestsMatch);
            verified.EnsureUnchanged();
        }

        var oldWriteTime = File.GetLastWriteTimeUtc(fixture.VolumePath);
        var edited = await File.ReadAllBytesAsync(fixture.VolumePath);
        edited[checked((int)rows[1].Offset + 10)] ^= 1;
        await File.WriteAllBytesAsync(fixture.VolumePath, edited);
        File.SetLastWriteTimeUtc(fixture.VolumePath, oldWriteTime);
        await using (var changed = await verifier.VerifyAsync(snapshot, expected, rows, revisions))
        {
            Assert.False(changed.DigestsMatch);
            changed.EnsureUnchanged();
        }

        using (var volume = new FileStream(fixture.VolumePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            volume.SetLength(rows[1].Offset + rows[1].StoredSize - 1);
        var truncated = await Assert.ThrowsAsync<ArchiveException>(() => verifier.VerifyAsync(snapshot, expected, rows, revisions));
        Assert.Equal("SourceChanged", truncated.Code);
    }

    [Fact]
    public async Task CancellationDuringHashingReleasesPinsAndAllowsRetry()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("SAMPLE_KEY", "A source line")]))));
        var snapshot = await fixture.BuildAsync();
        var row = Assert.Single(snapshot.Records);
        var bytes = await File.ReadAllBytesAsync(fixture.VolumePath);
        var expected = new Dictionary<long, string>
        {
            [row.Ordinal] = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(checked((int)row.Offset), checked((int)row.StoredSize)))).ToLowerInvariant()
        };
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalizationPayloadVerifier().VerifyAsync(snapshot,
            expected, [row], new CancelOnVolumeCapture(snapshot, cancellation), cancellation.Token));

        await using var retry = await new LocalizationPayloadVerifier().VerifyAsync(snapshot, expected, [row]);
        Assert.True(retry.DigestsMatch);
        retry.EnsureUnchanged();
    }

    private sealed class FrozenRevisionProvider(ArchiveSnapshot snapshot) : ISourceRevisionProvider
    {
        private readonly Dictionary<string, FileStamp> stamps = CreateStamps(snapshot);

        private static Dictionary<string, FileStamp> CreateStamps(ArchiveSnapshot snapshot)
        {
            var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
            {
                [snapshot.IndexPath] = snapshot.IndexStamp
            };
            foreach (var volume in snapshot.Volumes) result[volume.FilePath] = volume.Stamp;
            return result;
        }

        public FileStamp Capture(string path) => stamps.TryGetValue(path, out var stamp) ? stamp : new(false, "", 0, 0, 0, false);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }

    private sealed class CancelOnVolumeCapture(ArchiveSnapshot snapshot, CancellationTokenSource cancellation) : ISourceRevisionProvider
    {
        private readonly FrozenRevisionProvider inner = new(snapshot);
        public FileStamp Capture(string path)
        {
            if (string.Equals(path, snapshot.Volumes[0].FilePath, StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
            return inner.Capture(path);
        }
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
        {
            if (string.Equals(path, snapshot.Volumes[0].FilePath, StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
            return inner.Capture(path);
        }
    }
}
