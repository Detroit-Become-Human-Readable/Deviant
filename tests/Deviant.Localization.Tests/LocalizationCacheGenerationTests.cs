using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationCacheGenerationTests
{
    private static SqliteConnection OpenMutationConnection(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        connection.CreateFunction<string, string>("deviant_natural", static text => NaturalOrder.Key(text), isDeterministic: true);
        return connection;
    }

    [Fact]
    public async Task SearchProjectionUpgradeRebuildsFromCachedRecordsWithoutOpeningSourceVolumes()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("PROJECTION_KEY", "cached projection text")]))));
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
            IndexStamp = stamps[original.IndexPath], Volumes = volumes,
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(volumes)
        };
        var revisions = new FixedRevisionProvider(stamps);
        try
        {
            await using (var initial = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions))
            {
                await initial.EnrichmentTask;
                Assert.Equal("cached projection text", Assert.Single((await initial.QueryAsync(
                    new LocalizationQuery(ExactKey: "PROJECTION_KEY", Limit: 10))).Records).Text);
            }

            using (var store = FeatureStore.Open(snapshot, fixture.CachePath))
            using (var previous = store.OpenLocalizationGeneration<LocalizationGenerationHead>(
                LocalizationCacheContract.RecordStoreVersion, LocalizationCacheContract.SearchProjectionVersion,
                HeadIdentity, _ => true)!)
            {
                var writer = store.BeginLocalizationGeneration(LocalizationCacheContract.RecordStoreVersion, "generated-old-search-v0");
                Assert.Equal(previous.Generation, writer.BasePublishedGeneration);
                var page = store.LoadLocalizationRecordPage<LocalizationGenerationHead, LocalizationRecord>(previous, -1, 512);
                var rows = page.Items.Select(item => new LocalizationGenerationRow<LocalizationRecord>(
                    item.IdentityKey, item.Record, SearchAsset(snapshot, item.Record))).ToArray();
                store.AppendLocalizationGeneration(writer, rows);
                var oldHead = previous.Head with { SearchProjectionVersion = "generated-old-search-v0" };
                using var oldProjection = store.PublishLocalizationGeneration(writer, oldHead, HeadIdentity);
            }

            File.Delete(fixture.VolumePath);
            await using var upgraded = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions);
            await upgraded.EnrichmentTask;
            Assert.False(File.Exists(fixture.VolumePath));
            Assert.True(upgraded.IsEnrichmentComplete);
            var result = await upgraded.QueryAsync(new LocalizationQuery(ExactKey: "PROJECTION_KEY", Limit: 10));
            Assert.Equal("cached projection text", Assert.Single(result.Records).Text);
        }
        finally
        {
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    [Fact]
    public async Task DamagedPublishedRowsCauseSourceRebuildRatherThanPartialRead()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("RECOVERY_KEY", "recovered source text")]))));
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
            IndexStamp = stamps[original.IndexPath], Volumes = volumes,
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(volumes)
        };
        var revisions = new FixedRevisionProvider(stamps);
        try
        {
            await using (var initial = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions))
            {
                await initial.EnrichmentTask;
                Assert.Equal("recovered source text", Assert.Single((await initial.QueryAsync(
                    new LocalizationQuery(ExactKey: "RECOVERY_KEY", Limit: 10))).Records).Text);
            }

            var database = Directory.GetFiles(fixture.CachePath, "*.sqlite3").Single();
            using (var connection = OpenMutationConnection(database))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE localization_generation_rows SET payload='not-json' WHERE collection='records'";
                command.ExecuteNonQuery();
            }

            await using var recovered = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions);
            await recovered.EnrichmentTask;
            Assert.True(recovered.IsEnrichmentComplete);
            Assert.Equal("recovered source text", Assert.Single((await recovered.QueryAsync(
                new LocalizationQuery(ExactKey: "RECOVERY_KEY", Limit: 10))).Records).Text);
        }
        finally
        {
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    [Fact]
    public async Task NullNestedHeadStampIsRejectedAndRebuilt()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("HEAD_KEY", "head recovery text")]))));
        var snapshot = await fixture.BuildAsync();
        try
        {
            await using (var initial = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath))
                await initial.EnrichmentTask;

            var database = Directory.GetFiles(fixture.CachePath, "*.sqlite3").Single();
            using (var connection = OpenMutationConnection(database))
            {
                string generation;
                string payload;
                using (var read = connection.CreateCommand())
                {
                    read.CommandText = "SELECT g.generation,g.head_payload FROM localization_generation_active a JOIN localization_generations g ON g.revision=a.revision AND g.generation=a.generation WHERE a.record_store_version=$v";
                    read.Parameters.AddWithValue("$v", LocalizationCacheContract.RecordStoreVersion);
                    using var reader = read.ExecuteReader(); Assert.True(reader.Read());
                    generation = reader.GetString(0); payload = reader.GetString(1);
                }
                var json = JsonNode.Parse(payload)!;
                json["IndexStamp"] = null;
                var damaged = json.ToJsonString();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE localization_generations SET head_payload=$payload,head_sha256=$hash WHERE generation=$generation";
                update.Parameters.AddWithValue("$payload", damaged);
                update.Parameters.AddWithValue("$hash", HeadHash(damaged));
                update.Parameters.AddWithValue("$generation", generation); update.ExecuteNonQuery();
            }

            await using var recovered = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
            await recovered.EnrichmentTask;
            Assert.Equal("head recovery text", Assert.Single((await recovered.QueryAsync(
                new LocalizationQuery(ExactKey: "HEAD_KEY", Limit: 10))).Records).Text);
        }
        finally
        {
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    [Fact]
    public async Task FailedInitialEnrichmentAbandonsItsStagedGenerationAndQueriesAreUnavailable()
    {
        using var fixture = new LocalizationFixture();
        fixture.AddResource(LocalizationFixture.Wrap(LocalizationFixture.MakeLocalization(6,
            ("ENG", [LocalizationFixture.DirectText("FAIL_KEY", "generated source text")]))));
        var snapshot = await fixture.BuildAsync();
        File.Delete(fixture.VolumePath);
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath);
            await Assert.ThrowsAnyAsync<Exception>(() => session.EnrichmentTask);
            var error = await Assert.ThrowsAsync<ArchiveException>(() => session.QueryAsync(
                new LocalizationQuery(Limit: 10)));
            Assert.Equal("LocalizationUnavailable", error.Code);
            using var store = FeatureStore.Open(snapshot, fixture.CachePath);
            Assert.Null(store.OpenLocalizationGeneration<LocalizationGenerationHead>(LocalizationCacheContract.RecordStoreVersion,
                LocalizationCacheContract.SearchProjectionVersion, HeadIdentity, _ => true));
        }
        finally
        {
            if (Directory.Exists(fixture.CachePath)) Directory.Delete(fixture.CachePath, recursive: true);
        }
    }

    private static AssetDescriptor SearchAsset(ArchiveSnapshot snapshot, LocalizationRecord record)
    {
        var label = record.Key ?? "Generated localization record";
        var search = string.Join('\n', new[] { record.Key, record.Text, record.RawLanguageCode, record.Language, record.Diagnostic }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(record.Identity, new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            LocalizationSchema.AssetFamily, label, record.ResourceIdentity.Key, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.None, [], [], Language: record.Language,
            Codec: record.Kind.ToString(), SortKey: label)
        { SearchTerms = search, LookupKey = record.Key };
    }

    private static LocalizationGenerationIdentity HeadIdentity(LocalizationGenerationHead head) => new(
        head.InputSnapshotId, head.ParsedResultId, head.RecordStoreVersion, head.SearchProjectionVersion,
        head.JsonPolicyVersion, head.RecordCount);

    private static string HeadHash(string payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("deviant-localization-generation-head-v1"); Append(payload);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        void Append(string value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)bytes.Length));
            hash.AppendData(length); hash.AppendData(bytes);
        }
    }

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
