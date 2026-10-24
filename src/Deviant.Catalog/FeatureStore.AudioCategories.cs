using Deviant.Assets;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private bool audioCategoriesPrepared;

    private void InitializeAudioCategories()
    {
        bool exists = false;
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(assets_v2)";
            using var rows = columns.ExecuteReader();
            while (rows.Read()) if (rows.GetString(1) == "audio_category") exists = true;
        }
        using var schema = connection.CreateCommand();
        if (!exists) { schema.CommandText = "ALTER TABLE assets_v2 ADD COLUMN audio_category TEXT"; schema.ExecuteNonQuery(); }
        schema.CommandText = """
            CREATE INDEX IF NOT EXISTS assets_v2_audio_category ON assets_v2(revision,family,audio_category,sort_key,identity);
            CREATE INDEX IF NOT EXISTS assets_v2_audio_category_recovery ON assets_v2(revision,family,audio_category,row_id);
            CREATE TRIGGER IF NOT EXISTS assets_v2_audio_category_insert AFTER INSERT ON assets_v2 BEGIN
                UPDATE assets_v2 SET audio_category=CASE WHEN new.family='audio.media' AND json_valid(new.payload)
                    THEN CASE WHEN json_type(new.payload,'$.Metadata."audio.category"')='text'
                        THEN json_extract(new.payload,'$.Metadata."audio.category"') END END WHERE row_id=new.row_id;
            END;
            CREATE TRIGGER IF NOT EXISTS assets_v2_audio_category_update AFTER UPDATE OF payload,family ON assets_v2 BEGIN
                UPDATE assets_v2 SET audio_category=CASE WHEN new.family='audio.media' AND json_valid(new.payload)
                    THEN CASE WHEN json_type(new.payload,'$.Metadata."audio.category"')='text'
                        THEN json_extract(new.payload,'$.Metadata."audio.category"')
                        WHEN old.family=new.family AND old.revision=new.revision AND old.identity=new.identity
                            THEN old.audio_category END END WHERE row_id=new.row_id;
            END;
            CREATE TRIGGER IF NOT EXISTS assets_v2_audio_category_epoch AFTER UPDATE OF audio_category ON assets_v2
                WHEN old.audio_category IS NOT new.audio_category AND (old.family='audio.media' OR new.family='audio.media') BEGIN
                INSERT INTO linked_feature_dependencies(revision,kind,dependency_key,epoch)
                    VALUES(new.revision,1,'audio.media/categories',1)
                    ON CONFLICT(revision,kind,dependency_key) DO UPDATE SET epoch=epoch+1;
            END;
            """;
        schema.ExecuteNonQuery();
    }

    /// <summary>Restores the scalar audio type from matching typed checkpoints in short transactions.</summary>
    public void PrepareAudioCategories(SourceRevision source, LinkedFeatureScope scope,
        string recoveryPrefix, string recoveryVersion, CancellationToken token = default)
    {
        ValidateFacetSource(source, "audio.media");
        if (scope.Family != "audio.media" || string.IsNullOrEmpty(recoveryPrefix) || string.IsNullOrEmpty(recoveryVersion))
            throw new ArgumentException("Audio category recovery requires an audio checkpoint scope.");
        token.ThrowIfCancellationRequested();
        lock (gate) if (audioCategoriesPrepared) return;
        PrepareAssetFacets(source, "audio.media", token);
        using var timing = AssetLoadDiagnostics.Measure("AudioCategoryRecovery");
        long after = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long last = 0;
            lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                using var transaction = connection.BeginTransaction();
                using (var range = connection.CreateCommand())
                {
                    range.Transaction = transaction;
                    range.CommandText = "SELECT MAX(row_id) FROM (SELECT row_id FROM assets_v2 WHERE revision=$revision AND family='audio.media' AND audio_category IS NULL AND row_id>$after ORDER BY row_id LIMIT 128)";
                    range.Parameters.AddWithValue("$revision", revision); range.Parameters.AddWithValue("$after", after);
                    if (range.ExecuteScalar() is long value) last = value;
                }
                if (last == 0) { transaction.Commit(); return true; }
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = """
                    UPDATE assets_v2 AS a SET audio_category=COALESCE(
                        CASE WHEN json_valid(a.payload) THEN CASE WHEN json_type(a.payload,'$.Metadata."audio.category"')='text'
                            THEN json_extract(a.payload,'$.Metadata."audio.category"') END END,
                        (SELECT CASE WHEN length(CAST(f.payload AS BLOB))<=4194304 AND json_valid(f.payload) THEN
                            CASE WHEN json_type(f.payload,'$.Media.Category')='text'
                                AND json_extract(f.payload,'$.Revision.IndexContentId')=$index
                                AND json_extract(f.payload,'$.Revision.PhysicalSourceStamp')=$stamp
                                AND json_extract(f.payload,'$.Media.Identity.IndexContentId')=$index
                                AND json_type(f.payload,'$.Media.Identity.Ordinal')='integer'
                                AND a.identity=$index||':'||CAST(json_extract(f.payload,'$.Media.Identity.Ordinal') AS TEXT)||':'||COALESCE(json_extract(f.payload,'$.Media.Identity.MemberPath'),'')
                            THEN json_extract(f.payload,'$.Media.Category') END END
                            FROM asset_features f WHERE f.revision=a.revision AND f.feature=$prefix||a.identity AND f.version=$recoveryVersion),
                        (SELECT CASE WHEN length(CAST(f.payload AS BLOB))<=4194304 AND json_valid(f.payload) THEN
                            CASE WHEN json_type(f.payload,'$.Category')='text'
                                AND json_extract(f.payload,'$.Identity.IndexContentId')=$index
                                AND json_type(f.payload,'$.Identity.Ordinal')='integer'
                                AND a.identity=$index||':'||CAST(json_extract(f.payload,'$.Identity.Ordinal') AS TEXT)||':'||COALESCE(json_extract(f.payload,'$.Identity.MemberPath'),'')
                            THEN json_extract(f.payload,'$.Category') END END
                            FROM linked_feature_links l JOIN asset_features f ON f.revision=l.revision AND f.feature=l.feature_key
                            WHERE l.revision=a.revision AND l.projection_key=$scope AND l.projection_version=$scopeVersion
                                AND l.identity=a.identity AND f.version=$collectionVersion))
                    WHERE a.revision=$revision AND a.family='audio.media' AND a.row_id>$after AND a.row_id<=$last AND a.audio_category IS NULL
                    """;
                foreach (var pair in new[] { ("$revision", revision), ("$index", source.IndexContentId), ("$stamp", source.PhysicalSourceStamp),
                    ("$prefix", recoveryPrefix), ("$recoveryVersion", recoveryVersion), ("$scope", scope.Key),
                    ("$scopeVersion", scope.Version), ("$collectionVersion", scope.CollectionVersion) })
                    update.Parameters.AddWithValue(pair.Item1, pair.Item2);
                update.Parameters.AddWithValue("$after", after); update.Parameters.AddWithValue("$last", last);
                int changed = update.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); transaction.Commit();
                if (changed != 0) counts.Clear();
                return true;
            });
            if (last == 0) break;
            after = last;
        }
        lock (gate) audioCategoriesPrepared = true;
    }
}
