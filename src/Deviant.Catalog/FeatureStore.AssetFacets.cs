using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

namespace Deviant.Catalog;

public enum AssetFacet { Language, Codec, Channels, Group }
public sealed record AssetFacetValue(string Value, long Count);
public sealed record AssetFacetPage(SourceRevision Revision, long FamilyEpoch, long MatchedCount,
    long DistinctCount, IReadOnlyList<AssetFacetValue> Values, string? Continuation);

public sealed partial class FeatureStore
{
    public const int MaximumAssetFacetPageValues = 128;
    private const int MaximumAssetFacetValueBytes = 4096;
    private bool assetFacetIndexesPrepared;

    public void PrepareAssetFacets(SourceRevision source, string family, CancellationToken token = default)
    {
        ValidateFacetSource(source, family); token.ThrowIfCancellationRequested();
        using var timing = AssetLoadDiagnostics.Measure("AssetFacets.Prepare", family);
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            ObjectDisposedException.ThrowIf(disposed || linkedReadDisposed, this);
            using var transaction = connection.BeginTransaction();
            if (!assetFacetIndexesPrepared)
            {
                using var indexes = connection.CreateCommand(); indexes.Transaction = transaction;
                indexes.CommandText = """
                    CREATE INDEX IF NOT EXISTS assets_v2_facet_channels_v1 ON assets_v2(revision,family,channels,identity);
                    CREATE INDEX IF NOT EXISTS assets_v2_facet_group_v1 ON assets_v2(revision,family,bank,identity);
                    """;
                indexes.ExecuteNonQuery();
            }
            using var epoch = connection.CreateCommand(); epoch.Transaction = transaction;
            epoch.CommandText = "INSERT OR IGNORE INTO linked_feature_dependencies(revision,kind,dependency_key) VALUES($revision,1,$family)";
            epoch.Parameters.AddWithValue("$revision", revision); epoch.Parameters.AddWithValue("$family", family);
            epoch.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); transaction.Commit();
            assetFacetIndexesPrepared = true; return true;
        });
    }

    public long? ReadAssetFacetEpoch(SourceRevision source, string family, CancellationToken token = default)
    {
        ValidateFacetSource(source, family);
        return WithLinkedRead(token, (read, transaction, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            return ReadFacetEpoch(read, transaction, family);
        });
    }

    public AssetFacetPage? ReadAssetFacetPage(SourceRevision source, AssetQuery query, AssetFacet facet,
        string? afterValue = null, int limit = MaximumAssetFacetPageValues, long? expectedFamilyEpoch = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateFacetSource(source, query.Family!);
        if (!Enum.IsDefined(facet) || limit is < 1 or > MaximumAssetFacetPageValues || expectedFamilyEpoch is < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (query.ModelKind is not null || query.ModelNameProfile is not null || query.CharacterGroup is not null || query.CachedPayloadFilter is not null)
            throw new ArgumentException("Asset facets accept scalar filters.", nameof(query));
        if (afterValue is not null && (expectedFamilyEpoch is null || afterValue.Length == 0 ||
            afterValue.Length > MaximumAssetFacetValueBytes || Encoding.UTF8.GetByteCount(afterValue) > MaximumAssetFacetValueBytes))
            throw new ArgumentException("A continuation requires its family epoch and a bounded value.", nameof(afterValue));
        object? after = afterValue;
        if (facet == AssetFacet.Channels && afterValue is not null)
        {
            if (!int.TryParse(afterValue, NumberStyles.None, CultureInfo.InvariantCulture, out int channels))
                throw new ArgumentException("The channel continuation is invalid.", nameof(afterValue));
            after = channels;
        }
        string column = facet switch
        {
            AssetFacet.Language => "language", AssetFacet.Codec => "codec", AssetFacet.Channels => "channels",
            AssetFacet.Group => "bank", _ => throw new ArgumentOutOfRangeException(nameof(facet))
        };
        string valid = facet == AssetFacet.Channels ? "typeof(channels)='integer' AND channels>0"
            : $"typeof({column})='text' AND length(trim({column}))>0";
        var filter = query with { Offset = 0, Limit = 1, Sort = AssetSort.Default, Descending = false };
        return WithLinkedRead<AssetFacetPage?>(token, (read, transaction, cancellation) =>
        {
            long? epoch = ReadFacetEpoch(read, transaction, query.Family!);
            if (epoch is null || expectedFamilyEpoch is { } expected && epoch != expected) return null;
            long matched, distinct;
            using (var counts = FacetCommand(filter, $"COUNT(*),COUNT(DISTINCT CASE WHEN {valid} THEN {column} END)", read, transaction))
            using (var row = counts.ExecuteReader())
            { if (!row.Read()) throw new InvalidDataException("AssetFacetCountUnavailable"); matched = row.GetInt64(0); distinct = row.GetInt64(1); }
            AssetLoadDiagnostics.Counters("AssetFacets.ReadSnapshot", query.Family, new Dictionary<string, long>
                { ["FamilyEpoch"] = epoch.Value, ["MatchedCount"] = matched, ["DistinctCount"] = distinct });
            cancellation.ThrowIfCancellationRequested();
            var output = new List<AssetFacetValue>(limit); bool more = false;
            using (var page = FacetCommand(filter,
                $"CASE WHEN length(CAST({column} AS BLOB))<=$facetMaximum THEN CAST({column} AS TEXT) END,length(CAST({column} AS BLOB)),COUNT(*)", read, transaction))
            {
                page.CommandText += $" AND {valid}";
                if (after is not null)
                { page.CommandText += $" AND {column}>$facetAfter"; page.Parameters.AddWithValue("$facetAfter", after); }
                page.CommandText += $" GROUP BY {column} ORDER BY {column} ASC LIMIT $facetLimit";
                page.Parameters.AddWithValue("$facetMaximum", MaximumAssetFacetValueBytes);
                page.Parameters.AddWithValue("$facetLimit", limit + 1);
                using var rows = page.ExecuteReader();
                while (rows.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (output.Count == limit) { more = true; break; }
                    if (rows.IsDBNull(0) || rows.GetInt64(1) is < 1 or > MaximumAssetFacetValueBytes)
                        throw new InvalidDataException("AssetFacetValueLimit");
                    output.Add(new(rows.GetString(0), rows.GetInt64(2)));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            AssetLoadDiagnostics.Counters("AssetFacets.Page", query.Family, new Dictionary<string, long>
                { ["FamilyEpoch"] = epoch.Value, ["Values"] = output.Count, ["JsonBytes"] = 0 });
            return new(source, epoch.Value, matched, distinct, output.AsReadOnly(), more ? output[^1].Value : null);
        });
    }

    private SqliteCommand FacetCommand(AssetQuery query, string select, SqliteConnection read, SqliteTransaction transaction)
    {
        var command = AssetCommand(query, select, read, transaction, allowNameIndex: false);
        if (assetFacetIndexesPrepared)
            command.CommandText = command.CommandText.Replace(" INDEXED BY assets_v2_bank", " INDEXED BY assets_v2_facet_group_v1", StringComparison.Ordinal);
        return command;
    }

    private long? ReadFacetEpoch(SqliteConnection read, SqliteTransaction transaction, string family)
    {
        using var command = read.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT epoch FROM linked_feature_dependencies WHERE revision=$revision AND kind=1 AND dependency_key=$family";
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$family", family);
        return command.ExecuteScalar() is long value ? value : null;
    }

    private void ValidateFacetSource(SourceRevision source, string family)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentException.ThrowIfNullOrWhiteSpace(family);
        if (SourceIdentity.DerivedCacheKey(source.IndexContentId, source.PhysicalSourceStamp) != revision)
            throw new ArchiveException("SourceChanged", "The asset filters belong to another archive revision.");
    }
}
