using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationGenerationExportTests
{
    [Fact]
    public async Task ExportStaysOnItsAdmittedGenerationDespiteNewHeadAndLegacyAssetDecoys()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("PINNED_KEY", "PINNED_TEXT")]))));
        var original = await fixture.BuildAsync();
        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
        {
            [original.IndexPath] = ReliableStamp(original.IndexPath)
        };
        var volumes = original.Volumes.Select(volume =>
        {
            var updated = volume with { Stamp = ReliableStamp(volume.FilePath) };
            stamps[updated.FilePath] = updated.Stamp;
            return updated;
        }).ToArray();
        var snapshot = original with
        {
            IndexStamp = stamps[original.IndexPath],
            Volumes = volumes,
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(volumes)
        };
        var revisions = new FixedRevisionProvider(stamps);
        var output = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "generation-export-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions);
            await session.EnrichmentTask;
            var originalRecord = Assert.Single((await session.QueryAsync(new LocalizationQuery(Search: "PINNED", Limit: 10))).Records);

            using (var legacyStore = FeatureStore.Open(snapshot, fixture.CachePath))
            {
                var decoyIdentity = new ResourceIdentity(snapshot.IndexContentId, 999, "generated-legacy-decoy");
                legacyStore.UpsertAssets([Descriptor(snapshot, decoyIdentity, "PINNED legacy decoy", "PINNED")]);
            }

            using (var publisher = FeatureStore.Open(snapshot, fixture.CachePath))
            {
                var writer = publisher.BeginLocalizationGeneration(LocalizationCacheContract.RecordStoreVersion,
                    LocalizationCacheContract.SearchProjectionVersion);
                var changed = originalRecord with { Text = "SECOND_GENERATION" };
                publisher.AppendLocalizationGeneration<LocalizationRecord>(writer, [new(changed.Identity.Key, changed,
                    Descriptor(snapshot, changed.Identity, "SECOND_GENERATION", "SECOND_GENERATION"))]);
                var inputId = "generated-input-after-publication";
                var head = new LocalizationGenerationHead(snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
                    snapshot.IndexStamp, snapshot.Volumes, inputId, "generated-result-after-publication",
                    LocalizationCacheContract.RecordStoreVersion, LocalizationCacheContract.SearchProjectionVersion,
                    LocalizationCacheContract.JsonPolicyVersion, LocalizationCacheContract.InputDigestVersion,
                    1, 1, [new(0, LocalizationInputStatus.Read, new string('a', 64))],
                    new Dictionary<string, long> { [changed.ResourceIdentity.Key] = 1 },
                    new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<string, long>(),
                    new Dictionary<string, long>(), [changed.Language]);
                using var newer = publisher.PublishLocalizationGeneration(writer, head, HeadIdentity);
            }

            var report = await session.ExportAsync(LocalizationExportSelection.Matching(
                new LocalizationQuery(Search: "PINNED", Limit: 10)), LocalizationExportFormat.Json, output);
            Assert.Equal(1, report.Manifest.MatchedCount);
            Assert.Equal(1, report.Manifest.CompletedCount);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Equal("PINNED_TEXT", json.RootElement.GetProperty("entries")[0]
                .GetProperty("record").GetProperty("text").GetString());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            if (File.Exists(output + ".manifest.json")) File.Delete(output + ".manifest.json");
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, ResourceIdentity identity, string label, string search) =>
        new(identity, new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), LocalizationSchema.AssetFamily,
            label, new ResourceIdentity(snapshot.IndexContentId, 0).Key, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.None, [], [], Language: "ENG", Codec: "Text", SortKey: label)
        { SearchTerms = search, LookupKey = search };

    private static LocalizationGenerationIdentity HeadIdentity(LocalizationGenerationHead head) => new(
        head.InputSnapshotId, head.ParsedResultId, head.RecordStoreVersion, head.SearchProjectionVersion,
        head.JsonPolicyVersion, head.RecordCount);

    private static FileStamp ReliableStamp(string path)
    {
        var file = new FileInfo(path);
        return new(true, Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks,
            file.LastWriteTimeUtc.Ticks, true);
    }

    private sealed class FixedRevisionProvider(IReadOnlyDictionary<string, FileStamp> stamps) : ISourceRevisionProvider
    {
        public FileStamp Capture(string path) => stamps.TryGetValue(path, out var stamp)
            ? stamp : new(false, "", 0, 0, 0, false);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }
}
