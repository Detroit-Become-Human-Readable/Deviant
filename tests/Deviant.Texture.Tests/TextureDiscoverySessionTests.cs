using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
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
    public async Task OwnerQueryStaysPendingUntilContainerEnrichmentCanRun()
    {
        using var fixture = new DiscoveryFixture();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(TextureCatalogSession).GetField("<EnrichmentTask>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalogs, release.Task);
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        try
        {
            var pending = await discovery.QueryByOwnerAsync(owner);
            Assert.Equal(TextureOwnerQueryState.Pending, pending.State);
            Assert.True(pending.DiscoveryIncomplete); Assert.Empty(pending.Page.Entries);
        }
        finally { release.TrySetResult(); }
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
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await using var discovery = new TextureDiscoverySession(catalogs, native, new AliasStore(fixture.Library));
        await discovery.EnrichmentTask;
        var owner = new ResourceIdentity(snapshot.IndexContentId, 1, "dc-object/1/2211/43");
        Assert.Single((await discovery.QueryByOwnerAsync(owner)).Page.Entries);
        var path = snapshot.Volumes.Single().FilePath;
        var bytes = await File.ReadAllBytesAsync(path); bytes[(int)snapshot.Records[1].Offset + 32] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => discovery.QueryByOwnerAsync(owner));
        Assert.Equal("SourceChanged", error.Code);
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
            using var read = connection.CreateCommand(); read.CommandText = "SELECT feature,payload FROM asset_features WHERE feature LIKE 'texture.context/%'";
            using var rows = read.ExecuteReader(); Assert.True(rows.Read());
            var key = rows.GetString(0); var payload = JsonNode.Parse(rows.GetString(1))!; rows.Close();
            JsonNode evidence = field is "NameEvidence" or "PathEvidence" ? payload["Owners"]![0]![field]!
                : field == "ArchiveEvidence" ? payload["Bindings"]![0]![field]![0]! : payload["Bindings"]![0]![field]!;
            if (corruption == "revision") evidence["Revision"]!["PhysicalSourceStamp"] = "foreign revision";
            if (corruption == "extent") evidence["PhysicalExtent"]!["Offset"] = -1;
            if (corruption == "source") evidence["Source"]!["MemberPath"] = "foreign/member";
            if (corruption == "hash") evidence["SourceSha256"] = new string('0', 64);
            payload["Owners"]![0]!["Path"] = "textures/forged.dds";
            using var write = connection.CreateCommand(); write.CommandText = "UPDATE asset_features SET payload=$payload WHERE feature=$key";
            write.Parameters.AddWithValue("$key", key); write.Parameters.AddWithValue("$payload", payload.ToJsonString()); write.ExecuteNonQuery();
        }
        await using var reopened = new TextureDiscoverySession(catalogs, native, aliases, cache);
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

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantDiscoveryTests", Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(root, "archive", "sample.idx");
        public string Cache => Path.Combine(root, "cache");
        public string Settings => Path.Combine(root, "settings");
        public string Library => Path.Combine(root, "library");
        public DiscoveryFixture(int additionalTextures = 0, bool duplicateContainer = false, bool duplicateOwner = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Index)!);
            var catalog = Catalog(); var container = Container(duplicateOwner); var emptyCatalog = new byte[20];
            "COM_CONT"u8.CopyTo(emptyCatalog); Little(emptyCatalog, 8, 6); Little(emptyCatalog, 12, 4);
            var resources = new[] { (Type: 3u, Id: 1u, Bytes: catalog), (Type: 29u, Id: 80u, Bytes: container),
                (Type: 2229u, Id: 70u, Bytes: new byte[] { 1 }), (Type: 3u, Id: 2u, Bytes: emptyCatalog) }
                .Concat(Enumerable.Range(1, additionalTextures).Select(index => (Type: 2229u, Id: checked((uint)(70 + index)), Bytes: new byte[] { 1 }))).ToArray();
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
        private static byte[] Container(bool duplicateOwner)
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
            var records = new[] { metadata.ToArray(), context.ToArray() };
            if (duplicateOwner) records = records.Append(context.ToArray()).ToArray();
            using var body = new MemoryStream(); using (var writer = new BinaryWriter(body, Encoding.ASCII, true))
                foreach (var bytes in records) { writer.Write((uint)bytes.Length); writer.Write((byte)0); writer.Write(1u); writer.Write(0u); writer.Write(bytes); }
            using var decoded = new MemoryStream(); using (var writer = new BinaryWriter(decoded, Encoding.ASCII, true))
            {
                writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write((uint)(8 + records.Length * 8)); writer.Write(0u); writer.Write((uint)records.Length);
                writer.Write(2137u); writer.Write(42u); writer.Write(2211u); writer.Write(43u);
                if (duplicateOwner) { writer.Write(2211u); writer.Write(43u); }
                writer.Write("DC_DATA "u8); writer.Write(23u); writer.Write((uint)body.Length); writer.Write(body.ToArray());
            }
            using var compressed = new MemoryStream(); using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded.ToArray());
            var block = compressed.ToArray(); var segs = new byte[32 + block.Length]; "segs"u8.CopyTo(segs);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
            Little(segs, 8, (uint)decoded.Length); Little(segs, 12, (uint)block.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length); Little(segs, 20, 1); block.CopyTo(segs, 32); return segs;
        }
        private static void Little(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        private static void Big(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
