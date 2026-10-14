using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionQuarantineTests
{
    [Fact]
    public async Task OneMissingCheckpointDoesNotExpandIntoTheSharedTextureComponent()
    {
        using var fixture = await Fixture.CreateAsync(16, 400);
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        fixture.Publish(store);
        var before = store.LoadTextureProjectionOwners("generated");
        var damaged = fixture.Rows[0]; var good = fixture.Rows[1];
        long generation = store.CaptureTextureProjectionGeneration("generated");
        store.SetTextureProjectionAlias("generated", damaged.Identity, "Saved panel alias");
        var watch = Stopwatch.StartNew();
        var result = store.QuarantineTextureProjectionEvidence("generated", fixture.Baseline, [damaged.Identity],
            evidence => evidence.Revision == fixture.Revision && evidence.Source.Ordinal != fixture.MissingOwner.Ordinal);
        var page = store.QueryTextureProjections("generated", store.CaptureTextureProjectionGeneration("generated"), new(Search: "Shared"));
        watch.Stop();
        Assert.Single(result.InvalidRows); Assert.Equal(damaged.Identity, result.InvalidRows[0]);
        Assert.Equal(399, page.Total); Assert.Equal(100, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, entry => entry.Identity == damaged.Identity);
        Assert.Equal(before, store.LoadTextureProjectionOwners("generated"));
        Assert.Equal(good.Label, store.LoadTextureProjectionDetails("generated", generation, good.Identity)!.Label);
        var baseline = Assert.Single(store.QueryTextureProjections("generated", store.CaptureTextureProjectionGeneration("generated"),
            new(Search: "Saved panel alias")).Entries);
        Assert.Equal(TextureNameKind.Fallback, baseline.NameKind); Assert.Null(baseline.Width);
        Assert.Empty(baseline.MetadataIdentities); Assert.Equal("Saved panel alias", baseline.Alias);
        Assert.Null(store.LoadTextureProjectionDetails("generated", store.CaptureTextureProjectionGeneration("generated"), damaged.Identity));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Bounded quarantine and page query took {watch.Elapsed}.");
    }

    [Fact]
    public async Task CorruptRequestedEvidenceIsResetWithoutRemovingValidRowHistory()
    {
        using var fixture = await Fixture.CreateAsync(2, 5);
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache); fixture.Publish(store);
        var bad = fixture.Rows[1]; var good = fixture.Rows[2];
        long before = store.CaptureTextureProjectionGeneration("generated");
        using (var connection = fixture.OpenDatabase())
        using (var write = connection.CreateCommand())
        {
            write.CommandText = "UPDATE texture_projection_name_proofs SET sha256='corrupt' WHERE identity=$identity";
            write.Parameters.AddWithValue("$identity", bad.Identity.Key); write.ExecuteNonQuery();
        }
        var result = store.QuarantineTextureProjectionEvidence("generated", fixture.Baseline, [bad.Identity, good.Identity]);
        Assert.Equal(bad.Identity, Assert.Single(result.InvalidRows));
        Assert.Equal(good.Label, store.LoadTextureProjectionDetails("generated", before, good.Identity)!.Label);
        Assert.NotNull(store.LoadTextureProjectionOwner("generated", fixture.Owners[0]));
        long current = store.CaptureTextureProjectionGeneration("generated");
        Assert.Null(store.LoadTextureProjectionDetails("generated", current, bad.Identity));
        Assert.Equal(0, store.QueryTextureProjections("generated", current, new(Search: bad.Label)).Total);
        Assert.Equal(1, store.QueryTextureProjections("generated", current, new(Search: fixture.Baseline[bad.Identity].Label)).Total);
        Assert.Empty(store.QuarantineTextureProjectionEvidence("generated", fixture.Baseline, [bad.Identity]).InvalidRows);
    }

    [Fact]
    public async Task CancellationRollsBackQuarantineAndPreservesCheckpointAndGeneration()
    {
        using var fixture = await Fixture.CreateAsync(2, 5);
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache); fixture.Publish(store);
        long before = store.CaptureTextureProjectionGeneration("generated");
        var checkpoints = store.LoadTextureProjectionOwners("generated");
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => store.QuarantineTextureProjectionEvidence("generated", fixture.Baseline,
            [fixture.Rows[0].Identity], evidence => { cancellation.Cancel(); return false; }, cancellation.Token));
        Assert.Equal(before, store.CaptureTextureProjectionGeneration("generated"));
        Assert.Equal(checkpoints, store.LoadTextureProjectionOwners("generated"));
        Assert.NotNull(store.LoadTextureProjectionDetails("generated", before, fixture.Rows[0].Identity));
    }

    [Fact]
    public async Task ForeignRevisionProofCannotPublishDerivedMetadataAfterQuarantine()
    {
        using var fixture = await Fixture.CreateAsync(1, 3);
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache); fixture.Publish(store);
        var identity = fixture.Rows[1].Identity;
        var result = store.QuarantineTextureProjectionEvidence("generated", fixture.Baseline, [identity],
            evidence => evidence.Revision == (fixture.Revision with { PhysicalSourceStamp = "replaced-generated-source" }));
        Assert.Equal(identity, Assert.Single(result.InvalidRows));
        long current = store.CaptureTextureProjectionGeneration("generated");
        Assert.Null(store.LoadTextureProjectionDetails("generated", current, identity));
        var selected = Assert.Single(store.QueryTextureProjections("generated", current, new(Search: fixture.Baseline[identity].Label)).Entries);
        Assert.Empty(selected.MetadataContexts); Assert.Empty(selected.MetadataIdentities); Assert.Equal(TextureNameKind.Fallback, selected.NameKind);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private Fixture(string root, ArchiveSnapshot snapshot, int ownerCount, int textureCount)
        {
            this.root = root; Snapshot = snapshot;
            Owners = Enumerable.Range(0, ownerCount).Select(ordinal => new ResourceIdentity(snapshot.IndexContentId, ordinal)).ToArray();
            MissingOwner = new(snapshot.IndexContentId, ownerCount);
            var evidence = Owners.Select(Evidence).ToArray();
            Baseline = Enumerable.Range(ownerCount + 1, textureCount).Select(ordinal => new TextureDiscoveryEntry(
                new(snapshot.IndexContentId, ordinal), (uint)ordinal, 2229, $"Texture baseline {ordinal}", TextureNameKind.Fallback,
                null, null, null, null, null, null, null, [], [], [], [], [], [], $"texture-{ordinal}", $"Texture baseline {ordinal}"))
                .ToDictionary(entry => entry.Identity);
            Rows = Baseline.Values.Select((entry, index) => entry with
            {
                Label = $"Shared panel {index}", SearchText = $"Shared panel {index}", NameKind = TextureNameKind.VerifiedOwnerRole,
                Width = 4, Height = 2, Format = "BGRA8",
                Owners = Owners.Select((owner, at) => new TextureDiscoveryLink(owner, $"Panel {at}", TextureRelationshipKind.Object,
                    true, true, [evidence[at]])).ToArray(),
                MetadataIdentities = Owners.Select(owner => owner with { MemberPath = "filetext/40" }).ToArray(),
                NameCandidates = [new($"Shared panel {index}", TextureNameKind.VerifiedOwnerRole,
                    index == 0 ? evidence.Append(Evidence(MissingOwner)).ToArray() : evidence)]
            }).ToArray();
        }
        public ArchiveSnapshot Snapshot { get; }
        public string Cache => Path.Combine(root, "cache");
        public SourceRevision Revision => new(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
        public ResourceIdentity[] Owners { get; }
        public ResourceIdentity MissingOwner { get; }
        public Dictionary<ResourceIdentity, TextureDiscoveryEntry> Baseline { get; }
        public TextureDiscoveryEntry[] Rows { get; }
        private TextureEvidence Evidence(ResourceIdentity owner)
        {
            var row = Snapshot.RequireRecord(owner.Ordinal);
            var extent = new SourceExtent(row.VolumeName, row.Offset, 1, Convert.ToHexString(SHA256.HashData(new byte[] { 0 })).ToLowerInvariant());
            return new(owner, Revision, "GeneratedReader", "1", extent.PayloadSha256!, "Stored field", extent, new(owner, 0, 1));
        }
        public void Publish(FeatureStore store)
        {
            store.InitializeTextureProjections("generated", Baseline.Values.ToArray());
            foreach (var owner in Owners)
                store.CommitTextureProjectionOwner("generated", new(Revision, owner, 1, 0, Evidence(owner).PhysicalExtent!,
                    "generated-membership", true, true, null, true), Rows);
            store.PrepareTextureProjectionIndexes("generated");
        }
        public SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Directory.GetFiles(Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public static async Task<Fixture> CreateAsync(int owners, int textures)
        {
            string root = Path.Combine(Path.GetTempPath(), "deviant-projection-quarantine-" + Guid.NewGuid().ToString("N"));
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot); string path = Path.Combine(sourceRoot, "generated.idx");
            int count = owners + 1 + textures;
            var index = new byte[IndexLayout.HeaderLength + count * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), (uint)count);
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                int offset = IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength;
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset), ordinal <= owners ? 29u : 2229u);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 8), (uint)ordinal);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 12), (uint)ordinal);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 16), 1);
            }
            File.WriteAllBytes(path, index); File.WriteAllBytes(Path.ChangeExtension(path, ".dat"), new byte[count]);
            return new(root, await new ArchiveReader().OpenAsync(path), owners, textures);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
