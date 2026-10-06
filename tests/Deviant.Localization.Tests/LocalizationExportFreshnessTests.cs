using System.Text;
using System.Text.Json;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationExportFreshnessTests
{
    [Fact]
    public async Task MatchingExportValidatesInterleavedResourcesInSourceGroups()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("MATCH_A", "First A"), LocalizationFixture.DirectText("MATCH_B", "First B")]))));
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("MATCH_A", "Second A"), LocalizationFixture.DirectText("MATCH_B", "Second B")]))));
        var archiveSnapshot = await fixture.BuildAsync();
        var unreliableStamp = archiveSnapshot.Volumes.Single().Stamp with { Reliable = false };
        var snapshot = archiveSnapshot with
        {
            Volumes = archiveSnapshot.Volumes.Select(volume => volume with { Stamp = unreliableStamp }).ToArray(),
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(archiveSnapshot.Volumes.Select(volume => volume with { Stamp = unreliableStamp }))
        };
        var revisions = new FrozenRevisionProvider(snapshot);
        var output = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "deviant-localization-grouped-export-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath,
                sourceRevisionProvider: revisions);
            await session.EnrichmentTask;
            var report = await session.ExportAsync(LocalizationExportSelection.Matching(
                new LocalizationQuery(Search: "MATCH", Limit: 10_000)), LocalizationExportFormat.Json, output);

            Assert.Equal(4, report.Manifest.MatchedCount);
            Assert.Equal(4, report.Manifest.CompletedCount);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Equal(4, json.RootElement.GetProperty("entries").GetArrayLength());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            if (File.Exists(output + ".manifest.json")) File.Delete(output + ".manifest.json");
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    [Fact]
    public async Task UnreliableVolumeStampRejectsStaleExportAndFreshSessionExportsCurrentText()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("SAMPLE_KEY", "OLD")]))));
        var archiveSnapshot = await fixture.BuildAsync();
        var initialWriteTime = File.GetLastWriteTimeUtc(fixture.VolumePath);
        var unreliableStamp = archiveSnapshot.Volumes.Single().Stamp with { Reliable = false };
        var snapshot = archiveSnapshot with
        {
            Volumes = archiveSnapshot.Volumes.Select(volume => volume with { Stamp = unreliableStamp }).ToArray(),
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(archiveSnapshot.Volumes.Select(volume => volume with { Stamp = unreliableStamp }))
        };
        var revisions = new FrozenRevisionProvider(snapshot);
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath,
                sourceRevisionProvider: revisions);
            await session.EnrichmentTask;
            var originalRecord = Assert.Single((await session.QueryAsync(new LocalizationQuery(ExactKey: "SAMPLE_KEY", Limit: 10))).Records);

            var bytes = await File.ReadAllBytesAsync(fixture.VolumePath);
            var oldText = Encoding.Unicode.GetBytes("OLD");
            var textOffset = bytes.AsSpan().IndexOf(oldText);
            Assert.True(textOffset >= 0);
            Encoding.Unicode.GetBytes("NEW").CopyTo(bytes, textOffset);
            await File.WriteAllBytesAsync(fixture.VolumePath, bytes);
            File.SetLastWriteTimeUtc(fixture.VolumePath, initialWriteTime);

            var outputRoot = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "deviant-localization-freshness-output-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputRoot);
            try
            {
                foreach (var format in new[] { LocalizationExportFormat.Json, LocalizationExportFormat.Csv })
                {
                    var output = Path.Combine(outputRoot, "stale." + format.ToString().ToLowerInvariant());
                    const string existing = "keep existing destination";
                    await File.WriteAllTextAsync(output, existing);
                    var report = await session.ExportAsync(LocalizationExportSelection.Selected([originalRecord.Identity]), format, output);

                    Assert.Equal(1, report.Manifest.FailedCount);
                    Assert.Contains("SourceChanged", report.Manifest.FailureDiagnostic, StringComparison.Ordinal);
                    Assert.Equal(existing, await File.ReadAllTextAsync(output));
                    Assert.Empty(Directory.GetFiles(outputRoot, Path.GetFileName(output) + ".*.tmp"));
                }
            }
            finally { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true); }

            await session.DisposeAsync();
            await using var freshSession = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath,
                sourceRevisionProvider: revisions);
            await freshSession.EnrichmentTask;
            var freshRecord = Assert.Single((await freshSession.QueryAsync(new LocalizationQuery(ExactKey: "SAMPLE_KEY", Limit: 10))).Records);
            Assert.Equal("NEW", freshRecord.Text);
            foreach (var format in new[] { LocalizationExportFormat.Json, LocalizationExportFormat.Csv })
            {
                var output = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "deviant-localization-fresh-" + Guid.NewGuid().ToString("N") + "." + format.ToString().ToLowerInvariant());
                try
                {
                    await freshSession.ExportAsync(LocalizationExportSelection.Selected([freshRecord.Identity]), format, output);
                    var content = await File.ReadAllTextAsync(output, Encoding.UTF8);
                    Assert.Contains("NEW", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("OLD", content, StringComparison.Ordinal);
                    if (format == LocalizationExportFormat.Json)
                    {
                        using var json = JsonDocument.Parse(content);
                        Assert.Equal("NEW", json.RootElement.GetProperty("entries")[0].GetProperty("record").GetProperty("text").GetString());
                    }
                }
                finally
                {
                    if (File.Exists(output)) File.Delete(output);
                    if (File.Exists(output + ".manifest.json")) File.Delete(output + ".manifest.json");
                }
            }
        }
        finally { if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true); }
    }

    private sealed class FrozenRevisionProvider : ISourceRevisionProvider
    {
        private readonly Dictionary<string, FileStamp> stamps;

        public FrozenRevisionProvider(ArchiveSnapshot snapshot)
        {
            stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
            {
                [snapshot.IndexPath] = snapshot.IndexStamp
            };
            foreach (var volume in snapshot.Volumes) stamps[volume.FilePath] = volume.Stamp;
        }

        public FileStamp Capture(string path) => stamps.TryGetValue(path, out var stamp) ? stamp : new(false, "", 0, 0, 0, false);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }
}
