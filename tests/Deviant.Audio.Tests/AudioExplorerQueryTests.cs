using System.Text.Json;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioExplorerQueryTests
{
    [Fact]
    public async Task UnnamedBankMediaUsesBankAndMediaIdInsteadOfGenericOrdinal()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var workRoot = Path.Combine(Path.GetTempPath(), "deviant-audio-labels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var cache = new CacheManager(Path.Combine(workRoot, "settings"), new CacheSettings(Path.Combine(workRoot, "cache")));
            await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache,
                new AliasStore(Path.Combine(workRoot, "library")), new AudioToolPaths(null, null, null, null, null, null, null));
            var row = Assert.Single((await session.QueryAsync(new(Limit: 100))).Rows,
                item => item.Media.BankKey.Length > 0 && uint.TryParse(item.Media.Name, out _));

            Assert.Equal($"{row.Media.BankName} · {row.Media.Id}", row.Label);
            Assert.False(row.Label.Contains("Audio clip", StringComparison.OrdinalIgnoreCase));
            Assert.False(row.Label.Contains("Unnamed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IdentityPageHydrationPreservesDescriptorRowsAndFullTypedMedia()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var workRoot = Path.Combine(Path.GetTempPath(), "deviant-audio-query-" + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(workRoot, "cache");
        var settingsRoot = Path.Combine(workRoot, "settings");
        var libraryRoot = Path.Combine(workRoot, "library");
        Directory.CreateDirectory(workRoot);
        try
        {
            var cache = new CacheManager(settingsRoot, new CacheSettings(cacheRoot));
            var aliases = new AliasStore(libraryRoot);
            var tools = new AudioToolPaths(null, null, null, null, null, null, null);
            await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools);
            await session.EnrichmentTask;
            Assert.True(session.Catalog.Media.Count >= 2);
            using var legacyStore = FeatureStore.Open(session.Snapshot, cache.Location);

            var selected = session.Catalog.Media.Where(media => media.Identity is not null)
                .OrderBy(media => media.Identity!.Ordinal).Take(2).ToArray();
            Assert.Equal(2, selected.Length);
            await session.SetAliasAsync(selected[0].Identity!, "Alias Zulu");
            await session.SetAliasAsync(selected[1].Identity!, "Alias Alpha");
            var query = new AudioExplorerQuery(Search: "Alias", Sort: AssetSort.Name, Limit: 10);
            var assetQuery = new AssetQuery(Search: "Alias", Family: AudioExplorerSession.AssetFamily,
                Limit: 10, Sort: AssetSort.Default);
            var legacyDescriptors = legacyStore.Query(assetQuery);
            var page = await session.QueryAsync(query);

            Assert.Equal(legacyStore.Count(assetQuery), page.Total);
            Assert.Equal(legacyDescriptors.Select(descriptor => descriptor.Identity.Key), page.Rows.Select(row => row.Identity.Key));
            Assert.Equal(new[] { "Alias Alpha", "Alias Zulu" }, page.Rows.Select(row => row.Label));
            Assert.All(page.Rows, row =>
            {
                var expected = Assert.Single(legacyDescriptors, descriptor => descriptor.Identity.Key == row.Identity.Key);
                var selectionRow = Assert.Single(session.ResolveSelectionKeys([row.Identity.Key]));
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(row.Descriptor));
                Assert.Equal(new SourceRevision(session.Snapshot.IndexContentId, session.Snapshot.PhysicalSourceStamp), row.Descriptor.Revision);
                Assert.Equal(selectionRow.BankLabel, row.BankLabel);
                Assert.Equal(selectionRow.Bank, row.Bank);
                Assert.Equal(selectionRow.UserAlias, row.UserAlias);
                Assert.Equal(NaturalOrder.Key(row.Label), row.NaturalSortKey);
                Assert.Equal(expected.Names, row.Descriptor.Names);
                Assert.Equal(expected.Extents, row.Descriptor.Extents);
                Assert.Equal(row.Media.Completeness switch
                {
                    Deviant.Formats.Audio.Model.MediaCompleteness.Complete => "",
                    Deviant.Formats.Audio.Model.MediaCompleteness.Fragment => "Partial",
                    _ => ""
                }, row.CompletenessBadge);
                Assert.Equal(expected.Capabilities.HasFlag(AssetCapabilities.Preview), row.CanPreview);
                Assert.Equal(expected.Capabilities.HasFlag(AssetCapabilities.PartialPreview), row.CanPreviewPartial);
                Assert.Equal(expected.Capabilities.HasFlag(AssetCapabilities.Original), row.CanExportOriginal);
                Assert.Equal(expected.Capabilities.HasFlag(AssetCapabilities.Wav) || expected.Capabilities.HasFlag(AssetCapabilities.Ogg), row.CanConvert);
                Assert.True(row.Media.MetadataReadCoverageComplete);
                Assert.NotEmpty(row.Media.MetadataReadDigests);
            });

            var matching = await session.ReadMatchingAsync(query);
            Assert.Equal(page.Rows.Select(row => row.Identity.Key), matching.Select(row => row.Identity.Key));
            Assert.Equal(page.Rows.Select(row => JsonSerializer.Serialize(row.Descriptor)),
                matching.Select(row => JsonSerializer.Serialize(row.Descriptor)));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync(query, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadMatchingAsync(query, cancellationToken: cancelled.Token));
        }
        finally
        {
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
        }
    }
}
