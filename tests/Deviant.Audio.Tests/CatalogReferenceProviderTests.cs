using System.Buffers.Binary;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CatalogReferenceProviderTests : IDisposable
{
    private readonly string cacheRoot = Path.Combine(Path.GetTempPath(), "catalog-reference-provider-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReturnsCompleteOrderedNonTextureRelationshipsAndMultipleCompanions()
    {
        var unknownType = NextId();
        using var fixture = Fixture.Create([
            new(29, NextId()), new(29, NextId()), new(unknownType, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget);
        var catalog = Assert.Single(provider.CatalogIdentities);

        using var resultLease = await provider.ReadAsync(catalog);
        var result = resultLease.Result;

        Assert.Equal(LocalizationReferenceReadStatus.Verified, result.Status);
        Assert.Equal(3, result.Relationships.Count);
        Assert.Equal(new uint?[] { 29, 29, unknownType }, result.Relationships.Select(row => row.Reference.ResourceType));
        Assert.All(result.Relationships, row =>
        {
            Assert.Equal(catalog, row.Source);
            Assert.Equal(catalog, row.Reference.Owner);
            Assert.True(row.Evidence.Verified);
            Assert.Equal(9, row.Evidence.PhysicalExtent!.Length);
        });
        Assert.All(result.Relationships.Take(2), row => Assert.Equal(ReferenceStatus.Unique, row.Resolution.Status));
        using var hotLease = await provider.ReadAsync(catalog);
        Assert.Same(result, hotLease.Result);
    }

    [Fact]
    public async Task BatchCursorResumesAcrossVerifiedAndNotContainerRows()
    {
        using var fixture = Fixture.CreateMany(
            new (uint Type, uint Id)[] { (29, NextId()) },
            null,
            new (uint Type, uint Id)[] { (29, NextId()), (2229, NextId()) });
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget,
            providerOptions: new(MaximumBatchCatalogs: 2));

        using var first = await provider.ReadBatchAsync(0, 2);
        Assert.Equal(0, first.StartCatalogIndex);
        Assert.Equal(2, first.NextCatalogIndex);
        Assert.False(first.IsComplete);
        Assert.Equal(2, first.Rows.Count);
        Assert.Equal(LocalizationReferenceReadStatus.Verified, first.Rows[0].Lease!.Result.Status);
        Assert.Equal(LocalizationReferenceReadStatus.NotContainer, first.Rows[1].Lease!.Result.Status);

        using var resumed = await provider.ReadBatchAsync(first.NextCatalogIndex, 2);
        Assert.Equal(2, resumed.StartCatalogIndex);
        Assert.Equal(provider.CatalogIdentities.Count, resumed.NextCatalogIndex);
        Assert.True(resumed.IsComplete);
        Assert.Equal(LocalizationReferenceReadStatus.Verified, resumed.Rows[0].Lease!.Result.Status);
    }

    [Fact]
    public async Task TextureSessionUsesSharedProviderWithoutDroppingItsFullTable()
    {
        using var fixture = Fixture.Create([new(29, NextId()), new(2229, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget);
        await using (var texture = new TextureCatalogSession(snapshot,
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), referenceProvider: provider))
        {
            await texture.EnrichmentTask;
            Assert.Single(texture.Groups);
            Assert.Single(Assert.Single(texture.Groups).References);
            Assert.Same(provider, texture.ReferenceProvider);
        }

        using var fullLease = await provider.ReadAsync(Assert.Single(provider.CatalogIdentities));
        var full = fullLease.Result;
        Assert.Equal(2, full.Relationships.Count);
        Assert.Contains(full.Relationships, row => row.Reference.ResourceType == 29);
        Assert.Contains(full.Relationships, row => row.Reference.ResourceType == 2229);
    }

    [Fact]
    public async Task LegacyTextureConstructorCanReuseAnAlreadyUsedCallerReader()
    {
        using var fixture = Fixture.Create([new(29, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var reader = new LocalizationReferenceReader();
        var catalog = new ResourceIdentity(snapshot.IndexContentId,
            Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);
        var prior = await reader.ReadAsync(snapshot, catalog);
        Assert.Equal(LocalizationReferenceReadStatus.Verified, prior.Status);

        await using (var texture = new TextureCatalogSession(snapshot,
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), referenceReader: reader))
        {
            await texture.EnrichmentTask;
            using var lease = await texture.ReferenceProvider.ReadAsync(catalog);
            Assert.Equal(prior.ReferenceTableSha256, lease.Result.ReferenceTableSha256);
        }

        Assert.Equal(LocalizationReferenceReadStatus.Verified, (await reader.ReadAsync(snapshot, catalog)).Status);
        reader.Dispose();
    }

    [Fact]
    public async Task ProviderDisposalDoesNotDisposeCallerOwnedStoreAndReleasesHotBudget()
    {
        using var fixture = Fixture.Create([new(29, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var store = FeatureStore.Open(snapshot, cacheRoot);
        var budget = new AssetMemoryBudget();
        var provider = new CatalogReferenceProvider(snapshot, budget, store);
        var catalog = Assert.Single(provider.CatalogIdentities);

        var lease = await provider.ReadAsync(catalog);
        Assert.Equal(LocalizationReferenceReadStatus.Verified, lease.Result.Status);
        Assert.True(budget.UsedBytes > 0);
        await provider.DisposeAsync();

        Assert.True(budget.UsedBytes > 0);
        Assert.Single(lease.Result.Relationships);
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ReadAsync(catalog));
        store.Save("caller-owned", "v1", new { Ready = true });
        Assert.True(store.Load<System.Text.Json.JsonElement>("caller-owned", "v1")!.GetProperty("Ready").GetBoolean());
    }

    [Fact]
    public async Task ReturnedLeaseSurvivesHotEvictionAndProviderDisposalUntilCallerRelease()
    {
        using var fixture = Fixture.CreateMany(
            new (uint Type, uint Id)[] { (29, NextId()) },
            new (uint Type, uint Id)[] { (29, NextId()) });
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var provider = new CatalogReferenceProvider(snapshot, budget,
            providerOptions: new(MaximumHotCatalogs: 1));
        var first = await provider.ReadAsync(provider.CatalogIdentities[0]);
        var second = await provider.ReadAsync(provider.CatalogIdentities[1]);

        Assert.Single(first.Result.Relationships);
        Assert.Single(second.Result.Relationships);
        Assert.True(budget.UsedBytes > 0);
        await provider.DisposeAsync();
        Assert.Single(first.Result.Relationships);
        Assert.Single(second.Result.Relationships);
        Assert.True(budget.UsedBytes > 0);

        first.Dispose();
        Assert.True(budget.UsedBytes > 0);
        second.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task WarmCacheIsValidatedAndCorruptCacheFallsBackToTheCurrentTable()
    {
        using var fixture = Fixture.Create([new(29, NextId()), new(2229, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var store = FeatureStore.Open(snapshot, cacheRoot);
        var budget = new AssetMemoryBudget();
        var count = new CountingRevisionProvider(snapshot.Volumes.Single().FilePath);
        using var reader = new LocalizationReferenceReader(count);
        var providerOptions = new CatalogReferenceProviderOptions();
        var catalog = new ResourceIdentity(snapshot.IndexContentId, Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);

        string originalHash;
        await using (var cold = new CatalogReferenceProvider(snapshot, budget, store, reader, providerOptions))
        {
            using var coldLease = await cold.ReadAsync(catalog);
            originalHash = coldLease.Result.ReferenceTableSha256!;
        }
        var coldVolumeCaptures = count.VolumeCaptures;

        await using (var warm = new CatalogReferenceProvider(snapshot, budget, store, reader, providerOptions))
        {
            using var cachedLease = await warm.ReadAsync(catalog);
            var cached = cachedLease.Result;
            Assert.Equal(originalHash, cached.ReferenceTableSha256);
            Assert.True(count.VolumeCaptures - coldVolumeCaptures < coldVolumeCaptures);

            var tampered = cached with
            {
                Relationships = cached.Relationships.Select((row, index) => index == 0
                    ? row with { Resolution = new(ReferenceStatus.Missing, []) }
                    : row).ToArray()
            };
            store.SaveBounded(CatalogReferenceProvider.GetCacheFeatureKey(catalog),
                CatalogReferenceProvider.CacheFeatureVersion, tampered, providerOptions.MaximumCachedTableBytes);
        }

        await using var recovered = new CatalogReferenceProvider(snapshot, budget, store, reader, providerOptions);
        using var freshLease = await recovered.ReadAsync(catalog);
        var fresh = freshLease.Result;
        Assert.Equal(originalHash, fresh.ReferenceTableSha256);
        Assert.Equal(ReferenceStatus.Unique, fresh.Relationships[0].Resolution.Status);
        await recovered.RevalidateAsync(fresh);
        freshLease.Dispose();
        await recovered.DisposeAsync();
        reader.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task OversizedCompleteTablesRemainReadableWithoutPersistentAdmission()
    {
        var references = Enumerable.Range(0, 32).Select(_ => (29u, NextId())).ToArray();
        using var fixture = Fixture.Create(references);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        using var store = FeatureStore.Open(snapshot, cacheRoot);
        var budget = new AssetMemoryBudget();
        var options = new CatalogReferenceProviderOptions(MaximumCachedTableBytes: 1024);
        var catalog = new ResourceIdentity(snapshot.IndexContentId,
            Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);

        await using (var provider = new CatalogReferenceProvider(snapshot, budget, store,
            providerOptions: options))
        {
            using var lease = await provider.ReadAsync(catalog);
            Assert.Equal(LocalizationReferenceReadStatus.Verified, lease.Result.Status);
            Assert.Equal(32, lease.Result.Relationships.Count);
        }
        Assert.Null(store.LoadBounded<LocalizationReferenceReadResult>(
            CatalogReferenceProvider.GetCacheFeatureKey(catalog), CatalogReferenceProvider.CacheFeatureVersion, 1024));

        await using var resumed = new CatalogReferenceProvider(snapshot, budget, store, providerOptions: options);
        using var reread = await resumed.ReadAsync(catalog);
        Assert.Equal(32, reread.Result.Relationships.Count);
    }

    [Fact]
    public async Task MissingAndDuplicateTypedCandidatesRemainVisible()
    {
        var missingId = NextId();
        var ambiguousId = NextId();
        var missingType = NextId();
        var ambiguousType = NextId();
        using var fixture = Fixture.CreateWithExtraRows(
            [new(missingType, missingId), new(ambiguousType, ambiguousId)],
            [(ambiguousType, ambiguousId), (ambiguousType, ambiguousId)]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget);
        using var lease = await provider.ReadAsync(Assert.Single(provider.CatalogIdentities));
        var result = lease.Result;

        Assert.Equal(ReferenceStatus.Missing, result.Relationships[0].Resolution.Status);
        Assert.Empty(result.Relationships[0].Resolution.Candidates);
        Assert.Equal(ReferenceStatus.Ambiguous, result.Relationships[1].Resolution.Status);
        Assert.Equal(2, result.Relationships[1].Resolution.Candidates.Count);
    }

    [Fact]
    public async Task CancellationAndRevisionMismatchDoNotPublishPartialRows()
    {
        using var fixture = Fixture.Create([new(29, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(
            Assert.Single(provider.CatalogIdentities), cancelled.Token));
        Assert.Equal(0, budget.UsedBytes);
        await Assert.ThrowsAsync<ArchiveException>(() => provider.ReadAsync(
            new ResourceIdentity("different-index", provider.CatalogIdentities[0].Ordinal)));
    }

    [Fact]
    public async Task LowBudgetFailsBeforeTableAllocationAndHotResultsRejectChangedSources()
    {
        using var fixture = Fixture.Create([new(29, NextId())]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog = new ResourceIdentity(snapshot.IndexContentId,
            Assert.Single(snapshot.Records, row => row.Type == 3).Ordinal);

        var lowBudget = new AssetMemoryBudget(1024);
        await using (var limited = new CatalogReferenceProvider(snapshot, lowBudget))
        {
            await Assert.ThrowsAsync<ArchiveException>(() => limited.ReadAsync(catalog));
            Assert.Equal(0, lowBudget.UsedBytes);
        }

        var budget = new AssetMemoryBudget();
        await using var provider = new CatalogReferenceProvider(snapshot, budget);
        using (var lease = await provider.ReadAsync(catalog))
            Assert.Equal(LocalizationReferenceReadStatus.Verified, lease.Result.Status);
        var volume = Assert.Single(snapshot.Volumes);
        Assert.Throws<IOException>(() => new FileStream(volume.FilePath, FileMode.Append, FileAccess.Write, FileShare.Read));
        await provider.DisposeAsync();
        Assert.Equal(0, budget.UsedBytes);
        using (var stream = new FileStream(volume.FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.WriteByte(0x7f);

        await using var stale = new CatalogReferenceProvider(snapshot, budget);
        var changed = await Assert.ThrowsAsync<ArchiveException>(() => stale.ReadAsync(catalog));
        Assert.Equal("SourceChanged", changed.Code);
        await stale.DisposeAsync();
        Assert.Equal(0, budget.UsedBytes);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static uint NextId() => unchecked((uint)Random.Shared.Next(1, int.MaxValue));

    private sealed class CountingRevisionProvider(string trackedVolumePath) : ISourceRevisionProvider
    {
        private readonly FileRevisionProvider inner = new();
        private int volumeCaptures;
        public int VolumeCaptures => Volatile.Read(ref volumeCaptures);
        public FileStamp Capture(string path)
        {
            if (Path.GetFullPath(path) == Path.GetFullPath(trackedVolumePath)) Interlocked.Increment(ref volumeCaptures);
            return inner.Capture(path);
        }
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
        {
            if (Path.GetFullPath(path) == Path.GetFullPath(trackedVolumePath)) Interlocked.Increment(ref volumeCaptures);
            return inner.Capture(handle, path);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private Fixture(FormatsFixtureBuilder builder) => this.builder = builder;
        public string IndexPath => builder.IndexPath;
        public void Dispose() => builder.Dispose();

        public static Fixture Create((uint Type, uint Id)[] references)
        {
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var fixture = new Fixture(builder);
            var rows = references.ToList();
            foreach (var reference in rows)
                if (reference.Type is 29 or 2229 or 2230) builder.AddResource(reference.Type, reference.Id, [1, 2, 3]);
            builder.AddResource(3, NextId(), ComContainer(rows.ToArray()));
            builder.WriteIndex();
            return fixture;
        }

        public static Fixture CreateMany(params (uint Type, uint Id)[]?[] catalogs)
        {
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var fixture = new Fixture(builder);
            foreach (var catalog in catalogs)
            {
                foreach (var reference in catalog ?? [])
                    if (reference.Type is 29 or 2229 or 2230) builder.AddResource(reference.Type, reference.Id, [1, 2, 3]);
                builder.AddResource(3, NextId(), catalog is null ? new byte[20] : ComContainer(catalog));
            }
            builder.WriteIndex();
            return fixture;
        }

        public static Fixture CreateWithExtraRows((uint Type, uint Id)[] references,
            (uint Type, uint Id)[] extraRows)
        {
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var fixture = new Fixture(builder);
            foreach (var row in extraRows)
                builder.AddResource(row.Type, row.Id, [1, 2, 3]);
            builder.AddResource(3, NextId(), ComContainer(references));
            builder.WriteIndex();
            return fixture;
        }

        private static byte[] ComContainer((uint Type, uint Id)[] references)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("COM_CONT"u8);
            writer.Write(6U);
            writer.Write(checked((uint)(4 + references.Length * 9)));
            writer.Write((uint)references.Length);
            foreach (var reference in references)
            {
                writer.Write(reference.Type);
                writer.Write(reference.Id);
                writer.Write((byte)0);
            }
            return stream.ToArray();
        }
    }
}
