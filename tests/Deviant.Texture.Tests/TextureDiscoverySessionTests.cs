using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureDiscoverySessionTests
{
    [Fact]
    public async Task FirstBrowserPageSurvivesRepairBeforeItsReadStarts()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask; discovery.Cancel();
        await using var browsing = await discovery.OpenBrowseAsync(new());
        var type = typeof(TextureDiscoverySession);
        var store = (FeatureStore)type.GetField("features", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var baseline = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)type.GetField("projectionBaseline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        string profile = (string)type.GetProperty("ProjectionProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var entry = Assert.Single(baseline.Values);
        store.InitializeTextureProjections(profile, [entry, entry with { Identity = new(snapshot.IndexContentId, 99) }]);
        long replacement = store.InitializeTextureProjections(profile, [entry]);
        var response = await browsing.ReadAsync(new());
        Assert.Equal(replacement, response.Token.Generation); Assert.Equal(1, response.Page.Total);
        Assert.Equal(TextureNameKind.Fallback, Assert.Single(response.Page.Entries).NameKind);
        Assert.Empty(response.Options.Formats); Assert.Equal(1, response.Coverage.FallbackNames);
    }

    [Fact]
    public async Task CorruptEntryRepairDoesNotPublishMixedGenerations()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask;
        discovery.Cancel();
        await using var browse = await discovery.OpenBrowseAsync(new());
        await using var secondPin = await discovery.OpenBrowseAsync(new());
        var before = await browse.ReadAsync(new());
        Assert.Equal(before.Token, (await secondPin.ReadAsync(new())).Token);
        var entry = Assert.Single(before.Page.Entries);
        Assert.Equal("environment.dds", entry.Label);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString()))
        {
            db.Open(); using var corrupt = db.CreateCommand();
            corrupt.CommandText = "UPDATE texture_projection_name_proofs SET payload=x'0001' WHERE identity=$identity";
            corrupt.Parameters.AddWithValue("$identity", entry.Identity.Key); Assert.True(corrupt.ExecuteNonQuery() > 0);
        }
        var repaired = await browse.ReadAsync(new());
        Assert.True(repaired.Token.Generation > before.Token.Generation);
        Assert.Equal(TextureNameKind.Fallback, Assert.Single(repaired.Page.Entries).NameKind);
        Assert.Empty(repaired.Options.Formats); Assert.Empty(repaired.Options.Contexts);
        Assert.Equal(0, repaired.Coverage.TexturesWithMetadata); Assert.Equal(0, repaired.Coverage.OriginalNames);
        Assert.Equal(1, repaired.Coverage.FallbackNames);
        var invalidated = await Assert.ThrowsAsync<ArchiveException>(() => secondPin.ReadAsync(new()));
        Assert.Equal("TextureBrowseGenerationUnavailable", invalidated.Code);
    }

    [Fact]
    public async Task CatalogPreparationAndBackendSelectionUseTheSameSnapshot()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask; discovery.Cancel();
        var type = typeof(TextureDiscoverySession);
        var ready = type.GetField("projectionReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = (FeatureStore)type.GetField("features", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var baseline = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)type.GetField("projectionBaseline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        string profile = (string)type.GetProperty("ProjectionProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var entry = Assert.Single(baseline.Values);
        store.InitializeTextureProjections(profile, [entry, entry with { Identity = new(snapshot.IndexContentId, 99) }]);
        store.InitializeTextureProjections(profile, [entry]);
        var direct = (Dictionary<ResourceIdentity, TextureCatalogGroup>)type.GetField("directCatalogProjections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        direct.Clear(); ready.SetValue(discovery, false);
        int switches = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new BrowseCounterObserver(value =>
        {
            if (value.Stage != "TextureBrowser.Browse.BackendSnapshot" || value.Key != snapshot.IndexContentId) return;
            Interlocked.Increment(ref switches); ready.SetValue(discovery, true);
        }));
        var group = catalogs.Groups.Single(group => group.UniqueTextureMembers.Count > 0);
        var query = new TextureDiscoveryQuery(CatalogKey: group.CatalogIdentity.Key);
        await using var browse = await discovery.OpenBrowseAsync(query);
        var response = await browse.ReadAsync(query);
        Assert.Equal(1, switches); Assert.Equal(1, response.Page.Total);
        Assert.Contains(response.Options.Catalogs, option => option.Key == group.CatalogIdentity.Key);
    }

    private sealed class BrowseCounterObserver(Action<AssetLoadCounterMeasurement> observe) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadCounterMeasurement counter) observe(counter); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    [Fact]
    public async Task SourceReplacementInvalidatesBrowseSession()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        string volume = snapshot.Volumes.Single().FilePath;
        var bytes = await File.ReadAllBytesAsync(volume);
        bytes[(int)snapshot.Records[2].Offset] ^= 1;
        await File.WriteAllBytesAsync(volume, bytes);
        File.SetLastWriteTimeUtc(volume, new DateTime(snapshot.Volumes.Single().Stamp.LastWriteUtcTicks, DateTimeKind.Utc).AddSeconds(2));
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        var error = await Assert.ThrowsAsync<ArchiveException>(() => discovery.OpenBrowseAsync(new()));
        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task DirectCatalogCanPublishBeforeGlobalDiscoveryEnds()
    {
        using var fixture = new DiscoveryFixture(secondTextureCatalog: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var source = new CatalogReferenceProvider(snapshot, new AssetMemoryBudget());
        var delayed = new DelayedCatalogProvider(source);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: delayed);
        await delayed.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        try
        {
            delayed.FirstRelease.TrySetResult();
            await delayed.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForNameAsync(discovery);
            var group = Assert.Single(catalogs.Groups);
            var query = new TextureDiscoveryQuery(CatalogKey: group.CatalogIdentity.Key);
            await using var browse = await discovery.OpenBrowseAsync(query).WaitAsync(TimeSpan.FromSeconds(5));
            var response = await browse.ReadAsync(query).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, response.Page.Total);
            Assert.Equal(new TextureContextOption(group.CatalogIdentity.Key, group.Label), Assert.Single(response.Options.Catalogs));
            Assert.False(catalogs.EnrichmentTask.IsCompleted);
            delayed.SecondRelease.TrySetResult();
            await catalogs.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
            await discovery.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
            var repeat = await browse.ReadAsync(query);
            Assert.Equal(response.Token, repeat.Token);
            Assert.Single(Assert.Single(repeat.Page.Entries).CatalogKeys);
            await using var current = await discovery.OpenBrowseAsync(query);
            var updated = await current.ReadAsync(query);
            Assert.True(updated.Token.Generation > response.Token.Generation);
            Assert.Equal(2, Assert.Single(updated.Page.Entries).CatalogKeys.Count);
        }
        finally { delayed.FirstRelease.TrySetResult(); delayed.SecondRelease.TrySetResult(); }
    }

    [Fact]
    public async Task BrowsePagesDoNotWaitForTheProjectionWriter()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask;
        var store = (FeatureStore)typeof(TextureDiscoverySession).GetField("features", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var gate = typeof(FeatureStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(); } });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await using var browse = await discovery.OpenBrowseAsync(new()).WaitAsync(TimeSpan.FromSeconds(5));
            var response = await browse.ReadAsync(new()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("environment.dds", Assert.Single(response.Page.Entries).Label);
        }
        finally { release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task BrowseRejectsChangedFiltersAndDrainsOnOwnerDisposal()
    {
        using var fixture = new DiscoveryFixture(additionalTextures: 2);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        await using var browse = await discovery.OpenBrowseAsync(new(Limit: 1));
        var first = await browse.ReadAsync(new(Limit: 1));
        var second = await browse.ReadAsync(new(Offset: 1, Limit: 1));
        Assert.Equal(first.Token, second.Token); Assert.Equal(3, second.Page.Total);
        Assert.NotEqual(first.Page.Entries[0].Identity, second.Page.Entries[0].Identity);
        await Assert.ThrowsAsync<ArgumentException>(() => browse.ReadAsync(new(Search: "changed", Limit: 1)));
        await discovery.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browse.ReadAsync(new(Limit: 1)));
    }

    [Fact]
    public async Task DetailsStreamLargeEvidenceRangesWithoutAllocatingTheRangeInTheAssetBudget()
    {
        using var fixture = new DiscoveryFixture(rawSuffixBytes: 17 * 1024 * 1024);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var budget = new AssetMemoryBudget(8 * 1024 * 1024);
        await using var refs = new CatalogReferenceProvider(snapshot, budget);
        await using var catalogs = new TextureCatalogSession(snapshot, refs.Revision, referenceProvider: refs);
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
        var row = snapshot.Records[1];
        var bytes = new byte[checked((int)row.StoredSize)];
        using (var file = File.OpenRead(snapshot.Volumes.Single().FilePath))
        { file.Position = row.Offset; file.ReadExactly(bytes); }
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var extent = new SourceExtent(row.VolumeName, row.Offset, row.StoredSize, hash);
        var names = entry.NameCandidates.Select(candidate => candidate with
        {
            Evidence = candidate.Evidence.Select(evidence => evidence.Source.Ordinal == row.Ordinal
                ? evidence with { SourceSha256 = hash, PhysicalExtent = extent } : evidence).ToArray()
        }).ToArray();
        var indexed = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)typeof(TextureDiscoverySession)
            .GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        indexed[entry.Identity] = entry with { NameCandidates = names };
        using var concurrent = budget.Reserve(budget.MaximumBytes - budget.UsedBytes - 1024 * 1024,
            "generated concurrent asset use");

        var details = await discovery.GetDetailsAsync(entry.Identity);

        Assert.NotNull(details);
        Assert.Equal(entry.Label, details.Label);
        Assert.InRange(budget.PeakBytes, 1, budget.MaximumBytes);
    }

    [Fact]
    public async Task StoredMeshNameAndMaterialSlotCreateAnEvidenceBackedOrdinaryLabel()
    {
        using var fixture = new DiscoveryFixture(meshNames: ["Panel surface"]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entry = Assert.Single((await discovery.QueryAsync(new(Kind: TextureKindFilter.Images))).Entries);
        Assert.Equal("Panel surface · Texture slot 0", entry.Label);
        Assert.Equal(TextureNameKind.VerifiedOwnerRole, entry.NameKind);
        Assert.Null(entry.ResourceName); Assert.Null(entry.ResourcePath);
        var name = Assert.Single(entry.NameCandidates, candidate => candidate.Kind == TextureNameKind.VerifiedOwnerRole);
        Assert.Equal("Texture slot 0", name.Role);
        Assert.Contains(name.Evidence, evidence => evidence.Field == "MESHDATA stored name" && evidence.DecodedExtent is { Length: 13 });
        Assert.Contains(name.Evidence, evidence => evidence.Field.Contains("part 0 material") && evidence.DecodedExtent is { Length: 8 });
        Assert.Contains(name.Evidence, evidence => evidence.Field == "SHADCUST texture slot 0");
        Assert.Contains(name.Evidence, evidence => evidence.Reader == "TextureFileTextV24Reader");
        Assert.Contains(entry.Owners, owner => owner.Identity.MemberPath!.Contains("/2133/") && owner.Role is null);
        Assert.Single(entry.Owners, owner => owner.Identity.MemberPath!.Contains("/2130/") && owner.Role == "Texture slot 0");
    }

    [Fact]
    public async Task SharedTextureKeepsDistinctMeshOwnersAndOrderedSlotsWithoutDuplicatingParts()
    {
        using var fixture = new DiscoveryFixture(meshNames: ["Panel surface", "Trim surface"], meshSlots: 2);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
        var names = entry.NameCandidates.Where(candidate => candidate.Kind == TextureNameKind.VerifiedOwnerRole).ToArray();
        Assert.Equal(4, names.Length);
        Assert.Equal(2, names.Select(candidate => candidate.Owner).Distinct().Count());
        Assert.Equal(new[] { "Texture slot 0", "Texture slot 1" }, names.Select(candidate => candidate.Role).Distinct().Order().ToArray());
        Assert.Equal(4, entry.Owners.Count(owner => owner.Identity.MemberPath!.Contains("/2130/")));
        Assert.Null(entry.ResourcePath); Assert.Null(entry.ResourceName);
    }

    [Theory]
    [InlineData("shader")]
    [InlineData("texture")]
    [InlineData("material-type")]
    [InlineData("blank-name")]
    [InlineData("invalid-name")]
    public async Task AmbiguousTypedLinksAndInvalidStoredNamesCannotNameATexture(string defect)
    {
        using var fixture = new DiscoveryFixture(meshNames: [defect == "blank-name" ? "   " : "Panel surface"],
            duplicateShader: defect == "shader", duplicateFileText: defect == "texture",
            wrongMaterialType: defect == "material-type", invalidMeshName: defect == "invalid-name");
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
        Assert.Equal(TextureNameKind.Fallback, entry.NameKind);
        Assert.DoesNotContain(entry.NameCandidates, candidate => candidate.Kind == TextureNameKind.VerifiedOwnerRole);
        Assert.DoesNotContain(entry.Owners, owner => owner.Identity.MemberPath!.Contains("/2130/"));
    }

    [Fact]
    public async Task NamesArriveBeforeCatalogCompletionAndFinalPassAddsLaterMembership()
    {
        using var fixture = new DiscoveryFixture(secondTextureCatalog: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var budget = new AssetMemoryBudget();
        await using var source = new CatalogReferenceProvider(snapshot, budget);
        var delayed = new DelayedCatalogProvider(source);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: delayed);
        await delayed.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        try
        {
            delayed.FirstRelease.TrySetResult();
            await delayed.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var entry = await WaitForNameAsync(discovery);
            Assert.Equal("environment.dds", entry.Label);
            Assert.Equal(TextureNameKind.OriginalPath, entry.NameKind);
            Assert.False(catalogs.EnrichmentTask.IsCompleted);
            Assert.False(discovery.EnrichmentTask.IsCompleted);
            Assert.True(discovery.Coverage.DiscoveryIncomplete);
            Assert.Single(entry.CatalogKeys);
        }
        finally { delayed.FirstRelease.TrySetResult(); delayed.SecondRelease.TrySetResult(); }
        await catalogs.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        await discovery.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        var final = Assert.Single((await discovery.QueryAsync(new())).Entries);
        Assert.Equal(2, final.CatalogKeys.Count);
        var membershipMap = (Dictionary<ResourceIdentity, HashSet<ResourceIdentity>>)typeof(TextureDiscoverySession)
            .GetField("supportingCatalogs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        Assert.Equal(2, membershipMap[final.Identity].Count);
        Assert.Equal(1, discovery.Coverage.ContainersInspected);
        Assert.Equal(1, discovery.Coverage.MetadataInspected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedQuarantineRebuildsFromSourceInsteadOfAStoredFeatureRecord(bool missingCheckpoint)
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var budget = new AssetMemoryBudget();
        await using var references = new CatalogReferenceProvider(snapshot, budget);
        await using var catalogs = new TextureCatalogSession(snapshot, references.Revision, referenceProvider: references);
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache)); var aliases = new AliasStore(fixture.Library);
        string profile;
        await using (var discovery = new TextureDiscoverySession(catalogs, native, aliases, cache))
        {
            await discovery.EnrichmentTask; discovery.Cancel();
            var type = typeof(TextureDiscoverySession); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var store = (FeatureStore)type.GetField("features", flags)!.GetValue(discovery)!;
            profile = (string)type.GetProperty("ProjectionProfile", flags)!.GetValue(discovery)!;
            var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
            var checkpoint = Assert.Single(store.LoadTextureProjectionOwners(profile));
            var bindings = (System.Collections.IDictionary)type.GetField("metadataBindings", flags)!.GetValue(discovery)!;
            var bindingArray = Array.CreateInstance(type.GetNestedType("Binding", BindingFlags.NonPublic)!, 1);
            bindingArray.SetValue(((System.Collections.IEnumerable)bindings[entry.Identity]!).Cast<object>().First(), 0);
            var derivedType = type.GetNestedType("ContainerFeatures", BindingFlags.NonPublic)!;
            var derived = Activator.CreateInstance(derivedType, checkpoint.MetadataExtent.PayloadSha256!, bindingArray,
                Array.CreateInstance(type.GetNestedType("Owner", BindingFlags.NonPublic)!, 0), Array.Empty<string>())!;
            string hash = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(derived, derivedType))).ToLowerInvariant();
            var row = snapshot.Records.Single(row => row.Ordinal == checkpoint.Identity.Ordinal);
            var recordType = type.GetNestedType("NameIndexRecord", BindingFlags.NonPublic)!;
            var record = Activator.CreateInstance(recordType, discovery.Revision, row.StoredSize, row.AlternativeSize,
                checkpoint.MetadataExtent, checkpoint.MembershipKey, derived, hash)!;
            string key = (string)type.GetMethod("NameIndexKey", flags)!.Invoke(discovery, [checkpoint.Identity])!;
            string version = (string)type.GetProperty("NameIndexVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            typeof(FeatureStore).GetMethod("Save")!.MakeGenericMethod(recordType).Invoke(store, [key, version, record, CancellationToken.None]);
            Assert.NotNull(type.GetMethod("LoadNameIndex", flags)!.Invoke(discovery, [checkpoint.Identity, row, CancellationToken.None]));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString()))
            {
                db.Open(); using var corrupt = db.CreateCommand();
                corrupt.CommandText = "UPDATE texture_projection_name_proofs SET payload=x'00' WHERE identity=$identity";
                corrupt.Parameters.AddWithValue("$identity", entry.Identity.Key); Assert.True(corrupt.ExecuteNonQuery() > 0);
            }
            await using var browse = await discovery.OpenBrowseAsync(new());
            Assert.Equal(TextureNameKind.Fallback, Assert.Single((await browse.ReadAsync(new())).Page.Entries).NameKind);
            Assert.NotEmpty(store.LoadTextureProjectionOwnerRebuilds(profile));
            if (missingCheckpoint)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
                db.Open(); using var remove = db.CreateCommand(); remove.CommandText = "DELETE FROM texture_projection_owners";
                remove.ExecuteNonQuery();
            }
        }
        if (missingCheckpoint)
        {
            using var held = budget.Reserve(budget.MaximumBytes - budget.UsedBytes, "Generated foreground asset use");
            await using var blocked = new TextureDiscoverySession(catalogs, native, aliases, cache);
            await blocked.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Assert.Single((await blocked.QueryAsync(new())).Entries);
            Assert.Null(pending.Width); Assert.Empty(pending.MetadataIdentities);
        }
        await using var resumed = new TextureDiscoverySession(catalogs, native, aliases, cache);
        await resumed.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(5));
        var actual = Assert.Single((await resumed.QueryAsync(new())).Entries);
        Assert.Equal("environment.dds", actual.Label); Assert.Equal(TextureNameKind.OriginalPath, actual.NameKind);
        using var reopened = FeatureStore.Open(snapshot, fixture.Cache);
        Assert.Empty(reopened.LoadTextureProjectionOwnerRebuilds(profile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HydrationDoesNotRestoreAnOlderVersionAfterRejectingTheCurrentProof(bool alreadyHydrated)
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask; discovery.Cancel();
        var type = typeof(TextureDiscoverySession); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var store = (FeatureStore)type.GetField("features", flags)!.GetValue(discovery)!;
        string profile = (string)type.GetProperty("ProjectionProfile", flags)!.GetValue(discovery)!;
        var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
        var checkpoint = Assert.Single(store.LoadTextureProjectionOwners(profile));
        var bindings = (System.Collections.IDictionary)type.GetField("metadataBindings", flags)!.GetValue(discovery)!;
        var bindingType = type.GetNestedType("Binding", BindingFlags.NonPublic)!;
        var bindingArray = Array.CreateInstance(bindingType, 1);
        bindingArray.SetValue(((System.Collections.IEnumerable)bindings[entry.Identity]!).Cast<object>().First(), 0);
        var derivedType = type.GetNestedType("ContainerFeatures", BindingFlags.NonPublic)!;
        var derived = Activator.CreateInstance(derivedType, checkpoint.MetadataExtent.PayloadSha256!, bindingArray,
            Array.CreateInstance(type.GetNestedType("Owner", BindingFlags.NonPublic)!, 0), Array.Empty<string>())!;
        store.CommitTextureProjectionOwner(profile, checkpoint, [entry with { Label = "Older cached texture" }]);
        long rejected = store.CommitTextureProjectionOwner(profile, checkpoint, [entry]);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString()))
        {
            db.Open(); using var corrupt = db.CreateCommand();
            corrupt.CommandText = "UPDATE texture_projection_name_proofs SET payload=x'00' WHERE generation=$generation";
            corrupt.Parameters.AddWithValue("$generation", rejected); Assert.Equal(1, corrupt.ExecuteNonQuery());
        }
        var entries = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)type.GetField("entries", flags)!.GetValue(discovery)!;
        var baseline = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)type.GetField("projectionBaseline", flags)!.GetValue(discovery)!;
        entries[entry.Identity] = alreadyHydrated ? entry : baseline[entry.Identity];
        type.GetMethod("HydrateProjectionTargets", flags)!.Invoke(discovery, [derived, CancellationToken.None]);
        Assert.True(store.CaptureTextureProjectionGeneration(profile) > rejected);
        Assert.Equal(TextureNameKind.Fallback, entries[entry.Identity].NameKind);
        Assert.Empty(entries[entry.Identity].MetadataIdentities);
        Assert.NotNull(store.LoadTextureProjectionOwner(profile, checkpoint.Identity));
    }

    [Fact]
    public async Task FailedSummaryPreparationRetainsAnUpdateQueuedDuringTheFailure()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask;
        var type = typeof(TextureDiscoverySession);
        var preparation = type.GetField("summaryPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)preparation.GetValue(discovery)!).WaitAsync(TimeSpan.FromSeconds(2));
        var publish = type.GetMethod("PublishUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int batches = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new BrowseCounterObserver(value =>
        {
            if (value.Stage != "TextureBrowser.Store.SummaryBackfillBatch") return;
            if (Interlocked.Increment(ref batches) != 1) { retried.TrySetResult(); return; }
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
            db.Open(); using var change = db.CreateCommand();
            change.CommandText = "INSERT OR IGNORE INTO texture_browse_summary_dirty SELECT revision,profile,identity FROM texture_projection_rows";
            change.ExecuteNonQuery();
            publish.Invoke(discovery, null);
            throw new IOException("Generated transient summary failure");
        }));
        publish.Invoke(discovery, null);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ((Task)preparation.GetValue(discovery)!).WaitAsync(TimeSpan.FromSeconds(2));
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        check.Open(); using var pending = check.CreateCommand(); pending.CommandText = "SELECT COUNT(*) FROM texture_browse_summary_dirty";
        Assert.Equal(0L, Convert.ToInt64(pending.ExecuteScalar()));
    }

    [Fact]
    public async Task BrowserPreparesAndDrainsItsPersistedSummaryWork()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), cache);
        try
        {
            await discovery.EnrichmentTask;
            var field = typeof(TextureDiscoverySession).GetField("summaryPreparation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            await ((Task)field.GetValue(discovery)!).WaitAsync(TimeSpan.FromSeconds(2));
            int prepared = 0;
            using var observer = AssetLoadDiagnostics.Listener.Subscribe(new BrowseCounterObserver(value =>
            { if (value.Stage == "TextureBrowser.Store.PreparedSummaryRead") Interlocked.Increment(ref prepared); }));
            await using var browse = await discovery.OpenBrowseAsync(new());
            Assert.Equal("environment.dds", Assert.Single((await browse.ReadAsync(new())).Page.Entries).Label);
            Assert.Equal(1, Volatile.Read(ref prepared));
        }
        finally { await discovery.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredIncompleteOwnerOnlyRebuildsWhenItsFinalMembershipChanges(bool changedMembership)
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        var aliases = new AliasStore(fixture.Library);
        string profile;
        await using (var catalogs = new TextureCatalogSession(snapshot, revision))
        await using (var native = new NativeTextureSession(catalogs))
        await using (var cold = new TextureDiscoverySession(catalogs, native, aliases, cache))
        {
            await cold.EnrichmentTask;
            Assert.Equal("environment.dds", Assert.Single((await cold.QueryAsync(new())).Entries).Label);
            profile = (string)typeof(TextureDiscoverySession).GetProperty("ProjectionProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cold)!;
        }
        long generation;
        using (var store = FeatureStore.Open(snapshot, fixture.Cache))
        {
            generation = store.CaptureTextureProjectionGeneration(profile);
            var checkpoint = Assert.Single(store.LoadTextureProjectionOwners(profile));
            checkpoint = checkpoint with { MembershipComplete = false,
                MembershipKey = changedMembership ? checkpoint.MembershipKey + "/earlier-membership" : checkpoint.MembershipKey };
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE texture_projection_owners SET payload=$payload WHERE profile=$profile";
            command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(checkpoint));
            command.Parameters.AddWithValue("$profile", profile); Assert.Equal(1, command.ExecuteNonQuery());
        }
        for (int launch = 0; launch < 2; launch++)
        {
            await using var catalogs = new TextureCatalogSession(snapshot, revision);
            await catalogs.EnrichmentTask;
            await using var native = new NativeTextureSession(catalogs);
            await using var warm = new TextureDiscoverySession(catalogs, native, aliases, cache);
            await warm.EnrichmentTask;
            Assert.Equal("environment.dds", Assert.Single((await warm.QueryAsync(new())).Entries).Label);
            using var store = FeatureStore.Open(snapshot, fixture.Cache);
            Assert.True(Assert.Single(store.LoadTextureProjectionOwners(profile)).MembershipComplete);
            Assert.Equal(generation + (changedMembership ? 1 : 0), store.CaptureTextureProjectionGeneration(profile));
        }
    }

    [Fact]
    public async Task WarmNamesRestoreWhileCatalogsAreBlockedAndNoOwnerReadBudgetIsAvailable()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        var aliases = new AliasStore(fixture.Library);
        await using (var coldCatalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)))
        await using (var coldNative = new NativeTextureSession(coldCatalogs))
        await using (var cold = new TextureDiscoverySession(coldCatalogs, coldNative, aliases, cache))
        { await cold.EnrichmentTask; Assert.Equal("environment.dds", Assert.Single((await cold.QueryAsync(new())).Entries).Label); }
        var budget = new AssetMemoryBudget();
        await using var source = new CatalogReferenceProvider(snapshot, budget);
        await source.ValidateSnapshotAsync();
        using var held = budget.Reserve(budget.MaximumBytes - budget.UsedBytes, "generated concurrent asset use");
        var delayed = new DelayedCatalogProvider(source);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: delayed);
        await delayed.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var native = new NativeTextureSession(catalogs);
        await using var warm = new TextureDiscoverySession(catalogs, native, aliases, cache);
        try
        {
            var entry = await WaitForNameAsync(warm);
            Assert.Equal("environment.dds", entry.Label); Assert.Equal(TextureNameKind.OriginalPath, entry.NameKind);
            Assert.Empty(catalogs.Groups); Assert.False(catalogs.EnrichmentTask.IsCompleted);
            Assert.Equal(1, warm.Coverage.ContainersInspected);
            Assert.Empty(warm.Coverage.RemainingBlockers);
            Assert.Equal(budget.MaximumBytes, budget.UsedBytes);
        }
        finally { held.Dispose(); delayed.FirstRelease.TrySetResult(); delayed.SecondRelease.TrySetResult(); }
        await catalogs.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        await warm.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("environment.dds", Assert.Single((await warm.QueryAsync(new())).Entries).Label);
    }

    [Fact]
    public async Task LargeOwnerSuffixDoesNotPreventMetadataNamesOrConsumeItsStoredSize()
    {
        using var fixture = new DiscoveryFixture(rawSuffixBytes: 17 * 1024 * 1024);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var budget = new AssetMemoryBudget(8 * 1024 * 1024);
        await using var source = new CatalogReferenceProvider(snapshot, budget);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: source);
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(snapshot.Records[1].StoredSize > 16 * 1024 * 1024);
        var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
        Assert.Equal("environment.dds", entry.Label); Assert.Equal(TextureNameKind.OriginalPath, entry.NameKind);
        Assert.Equal(1, discovery.Coverage.ContainersInspected);
        Assert.InRange(budget.PeakBytes, 1, budget.MaximumBytes);
        Assert.Empty(discovery.Coverage.RemainingBlockers);
        Assert.NotNull(await discovery.GetDetailsAsync(entry.Identity));
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        var owned = await discovery.QueryByOwnerAsync(owner);
        Assert.Equal(TextureOwnerQueryState.Ready, owned.State);
        Assert.Equal(entry.Identity, Assert.Single(owned.Page.Entries).Identity);
    }

    [Fact]
    public async Task CancellationBetweenCatalogGroupsKeepsNamesForAResumedFinalMembershipPass()
    {
        using var fixture = new DiscoveryFixture(secondTextureCatalog: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var source = new CatalogReferenceProvider(snapshot, new AssetMemoryBudget());
        var delayed = new DelayedCatalogProvider(source);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: delayed);
        await delayed.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache)); var aliases = new AliasStore(fixture.Library);
        try
        {
            await using (var interrupted = new TextureDiscoverySession(catalogs, native, aliases, cache))
            {
                delayed.FirstRelease.TrySetResult();
                await delayed.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("environment.dds", (await WaitForNameAsync(interrupted)).Label);
                interrupted.Cancel();
                await interrupted.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(interrupted.Progress.IsRunning); Assert.False(catalogs.EnrichmentTask.IsCompleted);
                Assert.True(interrupted.Coverage.DiscoveryIncomplete);
            }
            await using var resumed = new TextureDiscoverySession(catalogs, native, aliases, cache);
            Assert.Equal("environment.dds", (await WaitForNameAsync(resumed)).Label);
            delayed.SecondRelease.TrySetResult();
            await catalogs.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
            await resumed.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Assert.Single((await resumed.QueryAsync(new())).Entries).CatalogKeys.Count);
            Assert.Equal(1, resumed.Coverage.ContainersInspected); Assert.Equal(1, resumed.Coverage.MetadataInspected);
        }
        finally { delayed.FirstRelease.TrySetResult(); delayed.SecondRelease.TrySetResult(); }
    }

    [Fact]
    public async Task ImageScopeIncludesUnnamedImagesAndSeparatesLightingMetadataAndLodBuffers()
    {
        using var fixture = new DiscoveryFixture(additionalTextures: 2, additionalLodBuffers: 1);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var images = await discovery.QueryAsync(new(Kind: TextureKindFilter.Images, SortByName: false));
        Assert.Equal(2, images.Total);
        Assert.All(images.Entries, entry =>
        {
            Assert.Equal(TextureKind.Image, entry.Kind);
            Assert.Equal(TextureNameKind.Fallback, entry.NameKind);
            Assert.Empty(entry.MetadataIdentities);
        });
        var lighting = await discovery.QueryAsync(new(Kind: TextureKindFilter.Lighting));
        Assert.Equal(1, lighting.Total);
        Assert.Equal(TextureKind.Lighting, Assert.Single(lighting.Entries).Kind);
        Assert.Single(lighting.Entries[0].MetadataIdentities);
        Assert.Equal(3, (await discovery.QueryAsync(new())).Total);
        var allResources = await discovery.QueryAsync(new(IncludeLodBuffers: true));
        Assert.Equal(4, allResources.Total);
        Assert.Single(allResources.Entries, entry => entry.Kind == TextureKind.LodBuffer);
        Assert.Equal(4, discovery.Coverage.IndexedResources);
        Assert.Equal(2, discovery.Coverage.Images);
        Assert.Equal(1, discovery.Coverage.LightingImages);
        Assert.Equal(1, discovery.Coverage.LodBuffers);
        Assert.Equal(1, discovery.Coverage.MetadataRecords);
    }

    [Fact]
    public async Task BindingVersionChangesWhenCatalogHashChangesWithoutChangingBindingCount()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        await using var native = new NativeTextureSession(catalogs);
        var identity = new ResourceIdentity(snapshot.IndexContentId, 2);
        var before = native.BindingVersion(identity);
        var count = native.KnownCatalogCount(identity);
        var groups = (Dictionary<long, TextureCatalogGroup>)typeof(TextureCatalogSession)
            .GetField("groupsByOrdinal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalogs)!;
        var group = Assert.Single(groups.Values);
        groups[group.CatalogIdentity.Ordinal] = group with { ReferenceTableSha256 = new string('a', 64) };
        Assert.Equal(count, native.KnownCatalogCount(identity));
        Assert.NotEqual(before, native.BindingVersion(identity));
    }

    [Fact]
    public async Task OwnerQueryStaysPendingUntilContainerEnrichmentCanRun()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var source = new CatalogReferenceProvider(snapshot, new AssetMemoryBudget());
        var delayed = new DelayedCatalogProvider(source);
        await using var catalogs = new TextureCatalogSession(snapshot, source.Revision, referenceProvider: delayed);
        await delayed.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        try
        {
            var pending = await discovery.QueryByOwnerAsync(owner);
            Assert.Equal(TextureOwnerQueryState.Pending, pending.State);
            Assert.True(pending.DiscoveryIncomplete); Assert.Empty(pending.Page.Entries);
        }
        finally { delayed.FirstRelease.TrySetResult(); delayed.SecondRelease.TrySetResult(); }
        await discovery.EnrichmentTask;
        var ready = await discovery.QueryByOwnerAsync(owner);
        Assert.Equal(TextureOwnerQueryState.Ready, ready.State); Assert.Single(ready.Page.Entries);
    }

    [Fact]
    public async Task OwnerPageRevalidatesAnAliasUpdatedWhileDetailsWerePending()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        var detailsGate = (SemaphoreSlim)typeof(TextureDiscoverySession).GetField("detailsGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        await detailsGate.WaitAsync();
        Task<TextureOwnerQueryResult> pending;
        try
        {
            pending = discovery.QueryByOwnerAsync(owner);
            Assert.False(pending.IsCompleted);
            await discovery.SetAliasAsync(new(snapshot.IndexContentId, 2), "Updated label");
        }
        finally { detailsGate.Release(); }
        var page = await pending;
        Assert.Equal("Updated label", Assert.Single(page.Page.Entries).Alias);
        Assert.Equal(owner, Assert.Single(Assert.Single(page.Page.Entries).Owners).Identity);
    }

    [Fact]
    public async Task OwnerQueriesPreserveExactNativeScopeAndPagedVerifiedMatches()
    {
        using var fixture = new DiscoveryFixture(duplicateContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        var result = await discovery.QueryByOwnerAsync(owner, limit: 1);
        Assert.Equal(TextureOwnerQueryState.Ready, result.State);
        Assert.Equal(owner, result.Owner); Assert.Equal(discovery.Revision, result.Revision);
        Assert.Equal(1, result.Page.Total); Assert.Equal(owner, Assert.Single(Assert.Single(result.Page.Entries).Owners).Identity);
        var after = await discovery.QueryByOwnerAsync(owner, offset: 1, limit: 1);
        Assert.Equal(1, after.Page.Total); Assert.Empty(after.Page.Entries); Assert.Equal(1, after.Page.Offset);
        var otherContainer = await discovery.QueryByOwnerAsync(owner with { Ordinal = 4 });
        Assert.Equal(TextureOwnerQueryState.Ready, otherContainer.State); Assert.Empty(otherContainer.Page.Entries);
        Assert.Equal(0, otherContainer.Page.Total); Assert.NotNull(otherContainer.Reason);
        var unsupportedUsage = await discovery.QueryByOwnerAsync(owner with { MemberPath = "dc-object/0/2137/42" });
        Assert.Equal(TextureOwnerQueryState.Ready, unsupportedUsage.State); Assert.Empty(unsupportedUsage.Page.Entries);
        foreach (var invalid in new[] { owner with { IndexContentId = "other-index" }, owner with { Ordinal = 2 },
            owner with { MemberPath = "filetext/1" }, owner with { MemberPath = "dc-object/01/2211/43" },
            owner with { MemberPath = "dc-object/1/2211/44" } })
        {
            var error = await Assert.ThrowsAsync<ArchiveException>(() => discovery.QueryByOwnerAsync(invalid));
            Assert.Equal("InvalidTextureOwner", error.Code);
        }
    }

    [Fact]
    public async Task OwnerQueriesReportPendingBudgetAndStoppedDiscoveryWithoutInventingMatches()
    {
        using var fixture = new DiscoveryFixture(duplicateContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library), options: new(MaximumContainers: 1));
        var owner = new ResourceIdentity(snapshot.IndexContentId, 4, "dc-object/1/2211/43");
        var stateGate = typeof(TextureDiscoverySession).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        Task<TextureOwnerQueryResult> pending;
        lock (stateGate) pending = discovery.QueryByOwnerAsync(owner);
        var initial = await pending;
        Assert.True(initial.State is TextureOwnerQueryState.Pending or TextureOwnerQueryState.Incomplete);
        Assert.True(initial.DiscoveryIncomplete); Assert.Empty(initial.Page.Entries);
        await discovery.EnrichmentTask;
        var limited = await discovery.QueryByOwnerAsync(owner);
        Assert.Equal(TextureOwnerQueryState.Incomplete, limited.State); Assert.True(limited.DiscoveryIncomplete);
        Assert.Empty(limited.Page.Entries); discovery.Cancel();
        var stopped = await discovery.QueryByOwnerAsync(owner with { Ordinal = 1 });
        Assert.Equal(TextureOwnerQueryState.Incomplete, stopped.State); Assert.True(stopped.DiscoveryIncomplete);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.QueryByOwnerAsync(owner, token: canceled.Token));
    }

    [Fact]
    public async Task OwnerQueriesFailClosedAfterSupportingSourceChanges()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        string path = snapshot.Volumes.Single().FilePath;
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[(int)snapshot.Records[2].Offset] ^= 1;
        await using (var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)))
        await using (var native = new NativeTextureSession(catalogs))
        await using (var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library)))
        {
            await discovery.EnrichmentTask;
            Assert.Single((await discovery.QueryByOwnerAsync(owner)).Page.Entries);
            await Assert.ThrowsAnyAsync<IOException>(() => File.WriteAllBytesAsync(path, bytes));
        }
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(snapshot.Volumes.Single().Stamp.LastWriteUtcTicks, DateTimeKind.Utc).AddSeconds(2));
        await using (var staleCatalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)))
        await using (var staleNative = new NativeTextureSession(staleCatalogs))
        await using (var stale = new TextureDiscoverySession(staleCatalogs, staleNative, new AliasStore(fixture.Library)))
        {
            await stale.EnrichmentTask;
            var error = await Assert.ThrowsAsync<ArchiveException>(() => stale.QueryByOwnerAsync(owner));
            Assert.Equal("SourceChanged", error.Code);
        }
        var current = await new ArchiveReader().OpenAsync(fixture.Index);
        Assert.NotEqual(snapshot.PhysicalSourceStamp, current.PhysicalSourceStamp);
        await using var currentCatalogs = new TextureCatalogSession(current, new(current.IndexContentId, current.PhysicalSourceStamp));
        await using var currentNative = new NativeTextureSession(currentCatalogs);
        await using var refreshed = new TextureDiscoverySession(currentCatalogs, currentNative, new AliasStore(fixture.Library));
        await refreshed.EnrichmentTask;
        Assert.Single((await refreshed.QueryByOwnerAsync(owner)).Page.Entries);
    }

    [Fact]
    public async Task OwnerQueriesRejectDuplicateNativeOwnerPairsInsteadOfChoosingAFrame()
    {
        using var fixture = new DiscoveryFixture(duplicateOwner: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        var error = await Assert.ThrowsAsync<ArchiveException>(() => discovery.QueryByOwnerAsync(owner));
        Assert.Equal("AmbiguousTextureOwner", error.Code);
    }

    [Fact]
    public async Task ParsedOwnersWithoutAUsageAdapterReturnScopedEmptyReadyWhileGlobalCoverageRemainsIncomplete()
    {
        using var fixture = new DiscoveryFixture(duplicateContainer: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), sessionOptions: new(MaximumCatalogs: 1));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var noAdapterOwner = new ResourceIdentity(snapshot.IndexContentId, 4, "dc-object/0/2137/42");
        var result = await discovery.QueryByOwnerAsync(noAdapterOwner);
        Assert.Equal(TextureOwnerQueryState.Ready, result.State); Assert.True(result.DiscoveryIncomplete);
        Assert.Empty(result.Page.Entries); Assert.Equal("No texture relationships are provided by the current adapters for this owner type.", result.Reason);
        var knownUsageOwner = noAdapterOwner with { MemberPath = "dc-object/1/2211/43" };
        var incomplete = await discovery.QueryByOwnerAsync(knownUsageOwner);
        Assert.Equal(TextureOwnerQueryState.Incomplete, incomplete.State); Assert.True(incomplete.DiscoveryIncomplete);
        Assert.Empty(incomplete.Page.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameSortingUsesVisibleProjectionAndLiteralAliasesWithStableIdentityTies(bool namedFirst)
    {
        using var fixture = new DiscoveryFixture(additionalTextures: 2);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var entries = (Dictionary<ResourceIdentity, TextureDiscoveryEntry>)typeof(TextureDiscoverySession)
            .GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        var labels = new[] { "ffffffff-ffff-ffff-ffff-ffffffffffff_Alpha.dds", "00000000-0000-0000-0000-000000000000_Zebra.dds", "11111111-1111-1111-1111-111111111111_Alpha.dds" };
        var identities = entries.Keys.OrderBy(identity => identity.Ordinal).ToArray();
        for (var index = 0; index < identities.Length; index++)
            entries[identities[index]] = entries[identities[index]] with { Label = labels[index], ResourcePath = "textures/" + labels[index], NameKind = TextureNameKind.OriginalPath };
        var sorted = await discovery.QueryAsync(new(NamedFirst: namedFirst));
        Assert.Equal(new[] { identities[0], identities[2], identities[1] }, sorted.Entries.Select(entry => entry.Identity));
        Assert.Equal(labels[0], sorted.Entries[0].Label);
        Assert.Equal("textures/" + labels[0], sorted.Entries[0].ResourcePath);
        var stem = entries[identities[1]].ExportStem;
        const string alias = "00000000-0000-0000-0000-000000000000_User label";
        await discovery.SetAliasAsync(identities[1], alias);
        var aliased = await discovery.QueryAsync(new(NamedFirst: namedFirst));
        Assert.Equal(identities[1], aliased.Entries[0].Identity);
        Assert.Equal(alias, aliased.Entries[0].Alias);
        Assert.Equal(labels[1], aliased.Entries[0].Label);
        Assert.Equal(stem, aliased.Entries[0].ExportStem);
        Assert.Equal(identities, (await discovery.QueryAsync(new(SortByName: false))).Entries.Select(entry => entry.Identity));
    }

    [Fact]
    public async Task ConcurrentQueriesReturnTheirOwnFiltersWithoutCancelingOtherCallers()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var stateGate = typeof(TextureDiscoverySession).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        Task<TextureDiscoveryPage> populated;
        Task<TextureDiscoveryPage> empty;
        lock (stateGate)
        {
            populated = discovery.QueryAsync(new(Search: "environment.dds"));
            empty = discovery.QueryAsync(new(Search: "absent search term"));
        }
        Assert.Single((await populated).Entries);
        Assert.Empty((await empty).Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRefreshAndExplicitLocationKeepTheirOwnCancellation(bool refreshFirst)
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var identity = new ResourceIdentity(snapshot.IndexContentId, 2);
        var stateGate = typeof(TextureDiscoverySession).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        Task<TextureDiscoveryPage?> location;
        Task<TextureDiscoveryPage> refresh;
        lock (stateGate)
        {
            if (refreshFirst)
            {
                refresh = discovery.QueryAsync(new(Search: "absent search term"));
                location = discovery.LocateAsync(identity, new());
            }
            else
            {
                location = discovery.LocateAsync(identity, new());
                refresh = discovery.QueryAsync(new(Search: "absent search term"));
            }
        }
        var page = await location;
        Assert.Equal(identity, Assert.Single(Assert.IsType<TextureDiscoveryPage>(page).Entries).Identity);
        Assert.Empty((await refresh).Entries);
        Assert.Equal(identity, Assert.Single((await discovery.QueryAsync(new())).Entries).Identity);
    }

    [Fact]
    public async Task ExplicitLocationStillHonorsCallerCancellation()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        using var cancellation = new CancellationTokenSource();
        var stateGate = typeof(TextureDiscoverySession).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(discovery)!;
        Task<TextureDiscoveryPage?> pending;
        lock (stateGate)
        {
            pending = discovery.LocateAsync(new(snapshot.IndexContentId, 2), new(), cancellation.Token);
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single((await discovery.QueryAsync(new())).Entries);
    }

    [Theory]
    [InlineData("NameEvidence", "revision")]
    [InlineData("PathEvidence", "extent")]
    [InlineData("DirectoryEvidence", "source")]
    [InlineData("ArchiveEvidence", "hash")]
    public async Task InvalidSupportingCacheEvidenceIsRebuiltBeforeNamesArePublished(string field, string corruption)
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), sessionOptions: new(MaximumCatalogs: 1));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        var aliases = new AliasStore(fixture.Library);
        await using (var discovery = new TextureDiscoverySession(catalogs, native, aliases, cache))
        {
            await discovery.EnrichmentTask;
            var entry = Assert.Single((await discovery.QueryAsync(new())).Entries);
            Assert.Equal("environment.dds", entry.Label);
            Assert.Equal(TextureNameKind.OriginalPath, entry.NameKind);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString()))
        {
            connection.Open();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT revision,profile,identity,generation,payload FROM texture_projection_evidence ORDER BY generation DESC LIMIT 1";
            using var rows = read.ExecuteReader(); Assert.True(rows.Read());
            string revision = rows.GetString(0), profile = rows.GetString(1), key = rows.GetString(2); long generation = rows.GetInt64(3);
            var payload = JsonNode.Parse((byte[])rows.GetValue(4))!; rows.Close();
            string expectedField = field switch
            {
                "NameEvidence" => "Stored light name", "PathEvidence" => "Stored light source path",
                "DirectoryEvidence" => "Native texture 2137/0x0000002A", _ => ""
            };
            var evidence = payload["NameCandidates"]!.AsArray().SelectMany(candidate => candidate!["Evidence"]!.AsArray())
                .First(item => field == "ArchiveEvidence" ? item!["Reader"]!.GetValue<string>() == "TextureCatalogMembershipReader"
                    : item!["Field"]!.GetValue<string>() == expectedField)!;
            if (corruption == "revision") evidence["Revision"]!["PhysicalSourceStamp"] = "foreign revision";
            if (corruption == "extent") evidence["PhysicalExtent"]!["Offset"] = -1;
            if (corruption == "source") evidence["Source"]!["MemberPath"] = "foreign/member";
            if (corruption == "hash") evidence["SourceSha256"] = new string('0', 64);
            payload["Label"] = "forged.dds"; payload["ResourceName"] = "forged.dds"; payload["ResourcePath"] = "textures/forged.dds";
            foreach (var candidate in payload["NameCandidates"]!.AsArray().Where(candidate => candidate!["Kind"]!.GetValue<int>() == (int)TextureNameKind.OriginalPath))
                candidate!["Text"] = "textures/forged.dds";
            byte[] encoded = Encoding.UTF8.GetBytes(payload.ToJsonString());
            using var write = connection.CreateCommand();
            // Keep the checksum valid so each case exercises supporting-evidence qualification, rather than only byte corruption.
            write.CommandText = "UPDATE texture_projection_evidence SET payload=$payload,sha256=$sha WHERE revision=$revision AND profile=$profile AND identity=$key AND generation=$generation;" +
                "UPDATE texture_projection_rows SET label='forged.dds',search='forged.dds',thin=json_set(thin,'$.Label','forged.dds','$.ResourceName','forged.dds','$.ResourcePath','textures/forged.dds') " +
                "WHERE revision=$revision AND profile=$profile AND identity=$key AND generation=$generation";
            write.Parameters.AddWithValue("$revision", revision); write.Parameters.AddWithValue("$profile", profile); write.Parameters.AddWithValue("$key", key);
            write.Parameters.AddWithValue("$generation", generation); write.Parameters.AddWithValue("$payload", encoded);
            write.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant()); write.ExecuteNonQuery();
        }
        await using var reopened = new TextureDiscoverySession(catalogs, native, aliases, cache);
        await reopened.EnrichmentTask;
        var pending = await reopened.QueryAsync(new());
        Assert.DoesNotContain(pending.Entries, entry => entry.Label == "forged.dds" || entry.ResourcePath == "textures/forged.dds");
        await reopened.EnrichmentTask;
        var actual = Assert.Single((await reopened.QueryAsync(new())).Entries);
        Assert.Equal("environment.dds", actual.Label);
        Assert.Equal("textures/environment.dds", actual.ResourcePath);
        Assert.Equal(TextureNameKind.OriginalPath, actual.NameKind);
        Assert.NotNull(await reopened.GetDetailsAsync(actual.Identity));
        Assert.True(reopened.Coverage.DiscoveryIncomplete);
    }

    [Fact]
    public async Task LateCatalogUpdateEnrichesAnAlreadyInspectedContainerWithoutDoubleCounting()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), sessionOptions: new(MaximumCatalogs: 1));
        await catalogs.EnrichmentTask;
        var known = Assert.Single(catalogs.Groups);
        var groupMap = (Dictionary<long, TextureCatalogGroup>)typeof(TextureCatalogSession).GetField("groupsByOrdinal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalogs)!;
        groupMap.Clear();
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        Assert.Equal(TextureNameKind.Fallback, Assert.Single((await discovery.QueryAsync(new())).Entries).NameKind);
        Assert.Equal(1, discovery.Coverage.ContainersInspected);
        groupMap.Add(known.CatalogIdentity.Ordinal, known);
        typeof(TextureCatalogSession).GetMethod("PublishUpdate", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!.Invoke(catalogs, null);
        await discovery.EnrichmentTask;
        var actual = Assert.Single((await discovery.QueryAsync(new())).Entries);
        Assert.Equal("environment.dds", actual.Label);
        Assert.Equal(TextureNameKind.OriginalPath, actual.NameKind);
        Assert.Equal(1, discovery.Coverage.ContainersInspected);
        Assert.Equal(1, discovery.Coverage.MetadataInspected);
        Assert.True(discovery.Coverage.DiscoveryIncomplete);
    }

    private static async Task<TextureDiscoveryEntry> WaitForNameAsync(TextureDiscoverySession discovery)
    {
        var ready = new TaskCompletionSource<TextureDiscoveryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Check()
        {
            try
            {
                var page = await discovery.QueryAsync(new(Search: "environment.dds"));
                if (page.Entries.FirstOrDefault(entry => entry.NameKind == TextureNameKind.OriginalPath) is { } entry)
                    ready.TrySetResult(entry);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }
        EventHandler handler = (sender, arguments) => { _ = Check(); };
        discovery.Updated += handler;
        try { await Check(); return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { discovery.Updated -= handler; }
    }

    private sealed class DelayedCatalogProvider(ICatalogReferenceProvider source) : ICatalogReferenceProvider
    {
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ArchiveSnapshot Snapshot => source.Snapshot;
        public SourceRevision Revision => source.Revision;
        public IReadOnlyList<ResourceIdentity> CatalogIdentities => source.CatalogIdentities;
        public int MaximumBatchCatalogs => source.MaximumBatchCatalogs;
        public AssetMemoryBudget SharedMemoryBudget => source.SharedMemoryBudget;
        public Task ValidateSnapshotAsync(CancellationToken token = default) => source.ValidateSnapshotAsync(token);
        public Task RevalidateAsync(LocalizationReferenceReadResult result, CancellationToken token = default) => source.RevalidateAsync(result, token);
        public Task<CatalogReferenceBatch> ReadBatchAsync(int start, int maximum, CancellationToken token = default) => source.ReadBatchAsync(start, maximum, token);
        public async Task<CatalogReferenceLease> ReadAsync(ResourceIdentity identity, CancellationToken token = default)
        {
            if (identity == CatalogIdentities[0]) { FirstEntered.TrySetResult(); await FirstRelease.Task.WaitAsync(token); }
            if (identity == CatalogIdentities[1]) { SecondEntered.TrySetResult(); await SecondRelease.Task.WaitAsync(token); }
            return await source.ReadAsync(identity, token);
        }
    }

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantDiscoveryTests", Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(root, "archive", "sample.idx");
        public string Cache => Path.Combine(root, "cache");
        public string Settings => Path.Combine(root, "settings");
        public string Library => Path.Combine(root, "library");
        public DiscoveryFixture(int additionalTextures = 0, bool duplicateContainer = false, bool duplicateOwner = false,
            int additionalLodBuffers = 0, bool secondTextureCatalog = false, int rawSuffixBytes = 0,
            string[]? meshNames = null, int meshSlots = 1, bool duplicateShader = false,
            bool duplicateFileText = false, bool wrongMaterialType = false, bool invalidMeshName = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Index)!);
            var catalog = Catalog(); var container = Container(duplicateOwner, meshNames, meshSlots, duplicateShader,
                duplicateFileText, wrongMaterialType, invalidMeshName); var emptyCatalog = new byte[20];
            if (rawSuffixBytes > 0) Array.Resize(ref container, checked(container.Length + rawSuffixBytes));
            "COM_CONT"u8.CopyTo(emptyCatalog); Little(emptyCatalog, 8, 6); Little(emptyCatalog, 12, 4);
            var resources = new[] { (Type: 3u, Id: 1u, Bytes: catalog), (Type: 29u, Id: 80u, Bytes: container),
                (Type: 2229u, Id: 70u, Bytes: new byte[] { 1 }), (Type: 3u, Id: 2u, Bytes: secondTextureCatalog ? Catalog() : emptyCatalog) }
                .Concat(Enumerable.Range(1, additionalTextures).Select(index => (Type: 2229u, Id: checked((uint)(70 + index)), Bytes: new byte[] { 1 }))).ToArray();
            resources = resources.Concat(Enumerable.Range(1, additionalLodBuffers)
                .Select(index => (Type: 2230u, Id: checked((uint)(90 + index)), Bytes: new byte[] { 1 }))).ToArray();
            if (duplicateContainer) resources = resources.Append((Type: 29u, Id: 81u, Bytes: container)).ToArray();
            var index = new byte[IndexLayout.HeaderLength + resources.Length * IndexLayout.RecordLength];
            IndexLayout.Magic.CopyTo(index); Big(index, 20, 18); Big(index, 101, (uint)resources.Length);
            using var volume = File.Create(Path.Combine(Path.GetDirectoryName(Index)!, "sample.dat"));
            for (var i = 0; i < resources.Length; i++)
            {
                var row = resources[i]; var offset = IndexLayout.HeaderLength + i * IndexLayout.RecordLength;
                Big(index, offset, row.Type); Big(index, offset + 8, row.Id); Big(index, offset + 12, (uint)volume.Position); Big(index, offset + 16, (uint)row.Bytes.Length);
                volume.Write(row.Bytes);
            }
            File.WriteAllBytes(Index, index);
        }
        private static byte[] Catalog()
        {
            var bytes = new byte[38]; "COM_CONT"u8.CopyTo(bytes); Little(bytes, 8, 6); Little(bytes, 12, 22); Little(bytes, 16, 2);
            Little(bytes, 20, 2229); Little(bytes, 24, 70); Little(bytes, 29, 29); Little(bytes, 33, 80); return bytes;
        }
        private static byte[] Container(bool duplicateOwner, string[]? meshNames, int meshSlots,
            bool duplicateShader, bool duplicateFileText, bool wrongMaterialType, bool invalidMeshName)
        {
            using var metadata = new MemoryStream(); using (var writer = new BinaryWriter(metadata, Encoding.ASCII, true))
            {
                writer.Write("FILETEXT"u8); writer.Write(24u); var prefix = new byte[84];
                BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 4); BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1); prefix[7] = 1; prefix[12] = 1;
                writer.Write(prefix); writer.Write(new byte[4]); writer.Write(32u); writer.Write(2229u); writer.Write(70u); writer.Write(32u);
                writer.Write("FTEXCRAW"u8); writer.Write(1u);
            }
            using var context = new MemoryStream(); using (var writer = new BinaryWriter(context, Encoding.ASCII, true))
            {
                writer.Write("IBLLIGHT"u8); writer.Write(13u); var prefix = new byte[83]; Little(prefix, 75, 1); writer.Write(prefix);
                writer.Write(2137u); writer.Write(42u); writer.Write(0u);
                foreach (var value in new[] { "Environment owner", "textures/environment.dds" }) { var bytes = Encoding.ASCII.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes); }
                writer.Write(new byte[8]);
            }
            var records = new List<(uint Type, uint Id, byte[] Bytes)> { (2137, 42, metadata.ToArray()) };
            if (meshNames is null)
            {
                records.Add((2211, 43, context.ToArray()));
                if (duplicateOwner) records.Add((2211, 43, context.ToArray()));
            }
            else
            {
                records.Add((2133, 51, Shader(meshSlots)));
                if (duplicateShader) records.Add((2133, 51, Shader(meshSlots)));
                if (duplicateFileText) records.Add((2137, 42, metadata.ToArray()));
                for (int i = 0; i < meshNames.Length; i++)
                    records.Add((2130, (uint)(60 + i), MeshData(meshNames[i], wrongMaterialType, invalidMeshName)));
            }
            using var body = new MemoryStream(); using (var writer = new BinaryWriter(body, Encoding.ASCII, true))
                foreach (var record in records) { writer.Write((uint)record.Bytes.Length); writer.Write((byte)0); writer.Write(1u); writer.Write(0u); writer.Write(record.Bytes); }
            using var decoded = new MemoryStream(); using (var writer = new BinaryWriter(decoded, Encoding.ASCII, true))
            {
                writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write((uint)(8 + records.Count * 8)); writer.Write(0u); writer.Write((uint)records.Count);
                foreach (var record in records) { writer.Write(record.Type); writer.Write(record.Id); }
                writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length); writer.Write(body.ToArray());
            }
            using var compressed = new MemoryStream(); using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded.ToArray());
            var block = compressed.ToArray(); var segs = new byte[32 + block.Length]; "segs"u8.CopyTo(segs);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
            Little(segs, 8, (uint)decoded.Length); Little(segs, 12, (uint)block.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length); Little(segs, 20, 1); block.CopyTo(segs, 32); return segs;
        }
        private static byte[] Shader(int slots)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
            writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("SHADCUST"u8); writer.Write(91u);
            writer.Write(new byte[35]); writer.Write((uint)slots);
            for (int i = 0; i < slots; i++) { writer.Write(0u); writer.Write(2137u); writer.Write(42u); writer.Write((byte)0); }
            return stream.ToArray();
        }
        private static byte[] MeshData(string name, bool wrongMaterialType, bool invalidName)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(3u); writer.Write("MESHDATA"u8); writer.Write(41u);
            writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0); // one empty vertex buffer
            writer.Write(1u); writer.Write(0u); // one empty index buffer
            writer.Write(1u); writer.Write(2u); writer.Write(0u); writer.Write(0u); // two parts sharing one material
            for (int part = 0; part < 2; part++)
            {
                writer.Write(0u); writer.Write((byte)0); writer.Write((byte)0);
                for (int field = 0; field < 7; field++) writer.Write(field == 6 ? 1u : 0u);
                writer.Write(0u); writer.Write(wrongMaterialType ? 2134u : 2133u); writer.Write(51u);
                writer.Write(0u); writer.Write(0u); writer.Write(new byte[19 * 4]);
            }
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(new byte[19 * 4]);
            writer.Write(0u); writer.Write(0u); // reference control, stored word
            for (int i = 0; i < 4; i++) writer.Write(0u); // stream strides
            byte[] text = invalidName ? [0xff] : Encoding.UTF8.GetBytes(name);
            writer.Write((uint)text.Length); writer.Write(text);
            writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0); writer.Write((byte)0); writer.Write(0u);
            writer.Write("CLUPSKME"u8); writer.Write(2u); writer.Write((byte)0);
            writer.Write("BLSHAPES"u8); writer.Write(3u); writer.Write(0u);
            return stream.ToArray();
        }
        private static void Little(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        private static void Big(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
