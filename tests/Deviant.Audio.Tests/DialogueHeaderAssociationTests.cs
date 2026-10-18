using System.Collections.Concurrent;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Audio;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class DialogueHeaderAssociationTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task ColdAndHeaderOnlyReadersKeepExactTypedTargetsAndProvenance()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var reader = new DetroitAudioSnapshotReader();
        var cold = await reader.ReadSnapshotAsync(snapshot);
        var association = Assert.Single(cold.Catalog.DialogueHeaderAssociations);
        Assert.Equal(1, cold.Catalog.DialogueHeaderVersion);
        Assert.Equal(DetroitAudioSnapshotReader.EventMediaVersion, cold.Catalog.EventMediaVersion);
        Assert.Equal(ReferenceStatus.Unique, association.AudioResolution.Status);
        Assert.Equal(ReferenceStatus.Unique, association.EventResolution.Status);
        Assert.Equal(1033u, snapshot.Records[(int)Assert.Single(association.AudioResolution.Candidates).Ordinal].Type);
        Assert.Equal(1031u, snapshot.Records[(int)Assert.Single(association.EventResolution.Candidates).Ordinal].Type);
        Assert.True(association.EvidenceCoverageComplete);
        Assert.NotEmpty(association.ReadDigests);
        Assert.All(association.ReadDigests, digest => Assert.Equal(association.HeaderIdentity, digest.Owner));
        var headers = await reader.ReadDialogueHeaderAssociationsAsync(snapshot);
        Assert.Equal(association.HeaderIdentity, Assert.Single(headers).HeaderIdentity);
        Assert.Equal(association.AudioResolution.Candidates, headers[0].AudioResolution.Candidates);
        Assert.Equal(association.ReadDigests, headers[0].ReadDigests);
    }

    [Fact]
    public async Task DuplicateTypedTargetsKeepAllCandidates()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog(duplicateDialogueAudio: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var association = Assert.Single(await new DetroitAudioSnapshotReader().ReadDialogueHeaderAssociationsAsync(snapshot));
        Assert.Equal(ReferenceStatus.Ambiguous, association.AudioResolution.Status);
        Assert.Equal(2, association.AudioResolution.Candidates.Count);
        Assert.Equal(ReferenceStatus.Unique, association.EventResolution.Status);
    }

    [Fact]
    public async Task MissingTypedTargetsAndMalformedHeadersRemainDistinct()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(4091, 12, Header((1033, 21), (1031, 22)));
        fixture.AddResource(29, 21, [0, 1]);
        fixture.AddResource(4091, 13, [0, 1, 2]);
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var associations = await new DetroitAudioSnapshotReader().ReadDialogueHeaderAssociationsAsync(snapshot);
        Assert.Equal(2, associations.Count);
        Assert.Equal(ReferenceStatus.Missing, associations[0].AudioResolution.Status);
        Assert.Empty(associations[0].AudioResolution.Candidates);
        Assert.Equal(ReferenceStatus.Missing, associations[0].EventResolution.Status);
        Assert.Equal(ReferenceStatus.Unresolved, associations[1].AudioResolution.Status);
        Assert.NotEmpty(associations[1].ReadDigests);
    }

    [Fact]
    public async Task OldNamedCacheUpgradesHeadersWithoutReplayingAudioAndThenRestoresWarm()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        string[] names;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools))
        {
            Assert.True(initial.AreDialogueHeadersReady);
            names = initial.Catalog.Media.Select(media => media.Name).ToArray();
        }
        workspace.Mutate("DELETE FROM asset_features WHERE feature LIKE '%/dialogue-headers'");
        var phases = new Phases();
        await using (var upgraded = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, phases, CancellationToken.None, startEnrichment: false))
        {
            Assert.False(upgraded.AreDialogueHeadersReady);
            Assert.False(upgraded.IsEnrichmentStarted);
            await upgraded.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(upgraded.AreDialogueHeadersReady);
            Assert.True(upgraded.IsEnrichmentComplete);
            Assert.Single(upgraded.Catalog.DialogueHeaderAssociations);
            Assert.Equal(names, upgraded.Catalog.Media.Select(media => media.Name));
            Assert.DoesNotContain(phases.Values, phase => phase is "Reading banks" or "Reading events" or "Reading dialogue media" or "Preparing audio library");
        }
        await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, null, CancellationToken.None, startEnrichment: false);
        Assert.False(warm.AreDialogueHeadersReady);
        Assert.Empty(warm.Catalog.Media);
        Assert.False(warm.IsEnrichmentStarted);
        var warmPage = await warm.QueryAsync(new(Limit: 100));
        Assert.Equal(names.Length, warmPage.Total);
        Assert.True(warm.AreDialogueHeadersReady);
        Assert.True(warm.IsEnrichmentComplete);
        Assert.False(warm.IsEnrichmentStarted);
        await warm.PrepareDialogueLookupAsync();
        Assert.Single(warm.Catalog.DialogueHeaderAssociations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrOversizedEnvelopeDoesNotBecomeReady(bool oversized)
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools)) { }
        workspace.Mutate(oversized
            ? "UPDATE asset_features SET payload=CAST(zeroblob(134217729) AS TEXT) WHERE feature LIKE '%/dialogue-headers'"
            : "UPDATE asset_features SET payload='null' WHERE feature LIKE '%/dialogue-headers'");
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, null, CancellationToken.None, startEnrichment: false);
        Assert.False(session.AreDialogueHeadersReady);
        Assert.False(session.IsEnrichmentStarted);
        Assert.Empty(session.Catalog.Media);
        var page = await session.QueryAsync(new(Limit: 100));
        Assert.NotEmpty(session.Catalog.Media);
        Assert.NotEmpty(page.Rows);
        Assert.False(session.AreDialogueHeadersReady);
        Assert.False(session.IsEnrichmentComplete);
        Assert.False(session.IsEnrichmentStarted);
        Assert.Contains(page.Rows, row => row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
        await session.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.AreDialogueHeadersReady);
        Assert.True(session.IsEnrichmentComplete);
    }

    [Fact]
    public async Task CompletedEmptyHeadersRestoreWithoutStartingWork()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty(); fixture.WriteIndex();
        using var workspace = new Workspace();
        await using (var first = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools))
        { Assert.True(first.AreDialogueHeadersReady); Assert.Empty(first.Catalog.DialogueHeaderAssociations); }
        await using var second = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, null, CancellationToken.None, startEnrichment: false);
        Assert.False(second.AreDialogueHeadersReady);
        Assert.False(second.IsEnrichmentComplete);
        Assert.False(second.IsEnrichmentStarted);
        Assert.Equal(0, (await second.QueryAsync(new())).Total);
        Assert.True(second.AreDialogueHeadersReady);
        Assert.True(second.IsEnrichmentComplete);
        Assert.Empty(second.Catalog.DialogueHeaderAssociations);
        Assert.True(second.EnrichmentTask.IsCompletedSuccessfully);
        Assert.False(second.IsEnrichmentStarted);
    }

    [Fact]
    public async Task PrecancelledHeaderReadDoesNotReadTheSource()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DetroitAudioSnapshotReader()
            .ReadDialogueHeaderAssociationsAsync(snapshot, cancellation.Token));
    }

    private static byte[] Header(params (uint Type, uint Id)[] references)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("COM_CONT")); writer.Write(6u); writer.Write((uint)(4 + 9 * references.Length));
        writer.Write((uint)references.Length);
        foreach (var reference in references) { writer.Write(reference.Type); writer.Write(reference.Id); writer.Write((byte)1); }
        return stream.ToArray();
    }

    private sealed class Phases : IProgress<AudioExplorerProgress>
    {
        public ConcurrentQueue<string> Values { get; } = new();
        public void Report(AudioExplorerProgress value) => Values.Enqueue(value.Phase);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-dialogue-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public Workspace()
        {
            Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
            Aliases = new(Path.Combine(root, "library"));
        }
        public void Mutate(string sql)
        {
            var file = Directory.GetFiles(Cache.Location, "catalog-*.sqlite3").Single();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=file,Pooling=false}.ToString());
            db.Open(); using var command=db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery();
        }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
