using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Catalog;

public sealed record AssetCatalogPage(IReadOnlyList<AssetDescriptor> Entries, long Total, long Offset);

public sealed partial class FeatureStore
{
    /// <summary>Reads a count and bounded page from one database snapshot without joining the writer queue.</summary>
    public AssetCatalogPage ReadAssetPage(AssetQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Family);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        const int maximumPayloadBytes = 4 * 1024 * 1024;
        return WithLinkedRead(token, (read, transaction, cancellation) =>
        {
            long total;
            using (AssetLoadDiagnostics.Measure("AssetBrowser.Count", query.Family))
            using (var count = AssetCommand(query, "COUNT(*)", read, transaction, allowNameIndex: false))
                total = Convert.ToInt64(count.ExecuteScalar());
            cancellation.ThrowIfCancellationRequested();
            var entries = new List<AssetDescriptor>();
            long bytes = 0;
            using var timing = AssetLoadDiagnostics.Measure("AssetBrowser.Page", query.Family);
            using var page = AssetPageCommand(query,
                "identity,length(CAST(a.payload AS BLOB)),CASE WHEN length(CAST(a.payload AS BLOB))<=$maximum THEN a.payload ELSE NULL END",
                read, transaction, prepareIndexes: false);
            page.Parameters.AddWithValue("$maximum", maximumPayloadBytes);
            using var rows = page.ExecuteReader();
            while (rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                long length = rows.GetInt64(1);
                if (length < 1 || length > maximumPayloadBytes || length > MaximumLinkedFeaturePageBytes - bytes || rows.IsDBNull(2))
                    throw new InvalidDataException("AssetPageLimit");
                var asset = LinkedJson<AssetDescriptor>(rows.GetString(2));
                if (asset.Identity is null || asset.Identity.Key != rows.GetString(0) || asset.Family != query.Family || asset.Revision is null ||
                    SourceIdentity.DerivedCacheKey(asset.Revision.IndexContentId, asset.Revision.PhysicalSourceStamp) != revision)
                    throw new InvalidDataException("AssetPageInvalid");
                bytes = checked(bytes + length);
                entries.Add(asset);
            }
            AssetLoadDiagnostics.Counters("AssetBrowser.Page", query.Family,
                new Dictionary<string, long> { ["Rows"] = entries.Count, ["Matched"] = total, ["PayloadBytes"] = bytes });
            return new AssetCatalogPage(entries.AsReadOnly(), total, query.Offset);
        });
    }
}
