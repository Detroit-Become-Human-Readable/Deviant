using System.Collections.Concurrent;
using System.Reflection;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioEventNameCacheTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task EventNamesAreBrowsableSearchableAndPreservedOnWarmReopen()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        ResourceIdentity identity;
        await using (var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools))
        {
            var row = Assert.Single((await session.QueryAsync(new(Search: "Play_Fixture_Sound"))).Rows);
            identity = row.Identity;
            Assert.Equal("Play_Fixture_Sound", row.Label);
            Assert.Equal(row.Media.Id.ToString(), row.Media.Name);
            var audioEvent = Assert.Single(session.Catalog.Events, item => item.Name == row.Label);
            Assert.Contains(audioEvent.Key, row.Media.EventKeys);
            Assert.Contains(row.Media.Key, audioEvent.MediaKeys);
            Assert.Contains(row.Descriptor.Names, name => name.Text == row.Label && name.Origin == NameOrigin.Description);
            Assert.All(session.Catalog.Banks, bank => Assert.Empty(bank.Objects));
            var byId = await session.QueryAsync(new(Search: row.Media.Id.ToString()));
            Assert.Contains(byId.Rows, item => item.Identity == identity);
            await session.SetAliasAsync(identity, "My clip");
            Assert.Equal("My clip", Assert.Single((await session.QueryAsync(new(Search: "Play_Fixture_Sound"))).Rows).Label);
        }
        var phases = new ConcurrentQueue<string>();
        await using var cached = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache,
            workspace.Aliases, NoTools, new InlineProgress(progress => phases.Enqueue(progress.Phase)), CancellationToken.None, startEnrichment: false);
        var cachedPage = await cached.QueryAsync(new(Search: "Play_Fixture_Sound"));
        Assert.True(cached.IsEnrichmentComplete);
        Assert.False(cached.IsEnrichmentStarted);
        Assert.Equal(DetroitAudioSnapshotReader.EventMediaVersion, cached.Catalog.EventMediaVersion);
        Assert.DoesNotContain("Reading banks", phases);
        Assert.DoesNotContain("Updating audio names", phases);
        Assert.Equal("My clip", Assert.Single(cachedPage.Rows).Label);
        await cached.SetAliasAsync(identity, null);
        Assert.Equal("Play_Fixture_Sound", Assert.Single((await cached.QueryAsync(new(Search: "Play_Fixture_Sound"))).Rows).Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LegacyCacheUpgradesNamesWithoutRestartingMediaIndexing(int version)
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        ArchiveSnapshot snapshot;
        AudioCatalog legacy;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools))
        {
            snapshot = initial.Snapshot;
            legacy = initial.Catalog;
        }
        legacy.EventMediaVersion = version;
        foreach (var media in legacy.Media.Where(item => item.BankKey.Length > 0))
        {
            media.DisplayLabel = ""; media.Aliases.Clear();
            if (version == 0) media.EventKeys.Clear();
            media.Names.RemoveAll(item => item.Namespace == "MediaLabel");
            media.NameKind = NameKind.Stored;
        }
        foreach (var entry in legacy.Events.Where(item => !item.IsDialogue))
        {
            if (version == 0) { entry.MediaKeys.Clear(); entry.MediaLinks.Clear(); }
            else { entry.Name = entry.Id.ToString(); entry.Names.Clear(); }
        }
        using (var store = FeatureStore.Open(snapshot, workspace.Cache.Location))
        {
            var snapshotVersion = (string)typeof(AudioExplorerSession).GetField("SnapshotVersion", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;
            store.SaveItems("audio.media", snapshotVersion, legacy.Media);
            store.SaveItems("audio.events", snapshotVersion, legacy.Events);
            var key = (string)typeof(AudioExplorerSession).GetMethod("BuildSnapshotFeatureKey", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [snapshot])!;
            store.Save(key, snapshotVersion, new
            {
                legacy.SourcePath, legacy.Fingerprint, legacy.IndexContentId, legacy.PhysicalSourceStamp,
                legacy.IndexStamp, legacy.SourceVolumes, legacy.IndexedResourceCount, legacy.EventMediaVersion
            });
        }
        var phases = new ConcurrentQueue<string>();
        await using (var upgraded = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache,
            workspace.Aliases, NoTools, new InlineProgress(progress => phases.Enqueue(progress.Phase)), CancellationToken.None, startEnrichment: false))
        {
            Assert.False(upgraded.IsEnrichmentStarted);
            Assert.False(upgraded.IsEnrichmentComplete);
            await upgraded.EnrichmentTask;
            Assert.True(upgraded.IsEnrichmentComplete);
            if (version == 0) Assert.Contains("Updating audio names", phases);
            else Assert.DoesNotContain("Updating audio names", phases);
            Assert.DoesNotContain("Reading banks", phases);
            Assert.DoesNotContain("Reading dialogue media", phases);
            Assert.Equal("Play_Fixture_Sound", Assert.Single((await upgraded.QueryAsync(new(Search: "Play_Fixture_Sound"))).Rows).Label);
            Assert.Equal(legacy.Media.Select(item => item.Identity), upgraded.Catalog.Media.Select(item => item.Identity));
            Assert.Equal(legacy.SourceDeclarations.Count, upgraded.Catalog.SourceDeclarations.Count);
        }
        await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache,
            workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        var warmPage = await warm.QueryAsync(new(Search: "Play_Fixture_Sound"));
        Assert.True(warm.IsEnrichmentComplete);
        Assert.False(warm.IsEnrichmentStarted);
        Assert.Equal("Play_Fixture_Sound", Assert.Single(warmPage.Rows).Label);
    }

    private sealed class InlineProgress(Action<AudioExplorerProgress> action) : IProgress<AudioExplorerProgress>
    { public void Report(AudioExplorerProgress value) => action(value); }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-event-names-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public Workspace()
        {
            Cache = new(Path.Combine(root, "settings"), new CacheSettings(Path.Combine(root, "cache")));
            Aliases = new(Path.Combine(root, "aliases"));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
