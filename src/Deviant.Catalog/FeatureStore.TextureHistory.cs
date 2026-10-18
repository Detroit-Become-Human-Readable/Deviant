using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private bool TextureHistoryIsClean(string profile, SqliteTransaction transaction)
    {
        using var pending = TextureCommand("SELECT 1 FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile LIMIT 1", profile, transaction);
        return pending.ExecuteScalar() is null;
    }

    private void RefreshTextureHistoryAfterAppend(string profile, long previousGeneration, long generation,
        bool previouslyClean, SqliteTransaction transaction)
    {
        if (!previouslyClean || previousGeneration < 1 || generation != previousGeneration + 1 ||
            !TextureHistoryTailsMatch(profile, generation, transaction))
        {
            RefreshTextureHistory(profile, transaction);
            return;
        }
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.HistoryIndex", TextureDiagnosticKey());
        using var append = TextureCommand("""
            UPDATE texture_projection_history_index AS h SET next_generation=$generation
                WHERE h.revision=$revision AND h.profile=$profile AND h.next_generation IS NULL
                AND (h.identity,h.generation) IN(SELECT d.identity,(SELECT MAX(t.generation) FROM texture_projection_history_index t
                    WHERE t.revision=d.revision AND t.profile=d.profile AND t.identity=d.identity)
                    FROM texture_projection_history_dirty d WHERE d.revision=$revision AND d.profile=$profile);
            INSERT INTO texture_projection_history_index(revision,profile,identity,generation,next_generation,ordinal,name_kind,kind,label,sort_key,search,
                width,height,format,context_key,context_label,has_owner,has_conflict,has_metadata,alias,name_rank,effective_sort,search_fold,data_generation)
            SELECT p.revision,p.profile,p.identity,$generation,NULL,p.ordinal,p.name_kind,p.kind,p.label,p.sort_key,p.search,
                p.width,p.height,p.format,p.context_key,p.context_label,p.has_owner,p.has_conflict,p.has_metadata,a.alias,
                CASE WHEN p.name_kind IN(0,1) THEN 0 WHEN p.name_kind=2 THEN 1 WHEN p.name_kind=3 THEN 2 WHEN a.alias IS NOT NULL THEN 3 ELSE 4 END,
                CASE WHEN a.alias IS NULL THEN p.sort_key ELSE deviant_natural(a.alias) END,
                deviant_texture_fold(p.search||char(10)||COALESCE(a.alias,'')),p.generation
            FROM texture_projection_history_dirty d JOIN texture_projection_rows p
                ON p.revision=d.revision AND p.profile=d.profile AND p.identity=d.identity
            LEFT JOIN texture_projection_alias_versions a ON a.revision=p.revision AND a.profile=p.profile AND a.identity=p.identity
                AND a.generation=(SELECT MAX(v.generation) FROM texture_projection_alias_versions v WHERE v.revision=p.revision
                    AND v.profile=p.profile AND v.identity=p.identity AND v.generation<=$generation)
            WHERE d.revision=$revision AND d.profile=$profile AND p.generation=(SELECT MAX(r.generation) FROM texture_projection_rows r
                WHERE r.revision=p.revision AND r.profile=p.profile AND r.identity=p.identity AND r.generation<=$generation);
            DELETE FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile;
            """, profile, transaction);
        append.Parameters.AddWithValue("$generation", generation);
        append.ExecuteNonQuery();
        RefreshTextureSummaryDirty(profile, transaction);
    }

    private bool TextureHistoryTailsMatch(string profile, long generation, SqliteTransaction transaction)
    {
        using var check = TextureCommand("""
            SELECT
                (SELECT MAX(r.generation) FROM texture_projection_rows r WHERE r.revision=d.revision AND r.profile=d.profile
                    AND r.identity=d.identity AND r.generation<$generation),
                (SELECT MAX(a.generation) FROM texture_projection_alias_versions a WHERE a.revision=d.revision AND a.profile=d.profile
                    AND a.identity=d.identity AND a.generation<$generation),
                (SELECT MAX(h.generation) FROM texture_projection_history_index h WHERE h.revision=d.revision AND h.profile=d.profile AND h.identity=d.identity),
                (SELECT CASE WHEN h.next_generation IS NULL THEN 1 ELSE 0 END FROM texture_projection_history_index h
                    WHERE h.revision=d.revision AND h.profile=d.profile AND h.identity=d.identity ORDER BY h.generation DESC LIMIT 1),
                (SELECT COALESCE(h.data_generation,h.generation) FROM texture_projection_history_index h WHERE h.revision=d.revision AND h.profile=d.profile
                    AND h.identity=d.identity ORDER BY h.generation DESC LIMIT 1),
                (SELECT h.next_generation FROM texture_projection_history_index h WHERE h.revision=d.revision AND h.profile=d.profile
                    AND h.identity=d.identity ORDER BY h.generation DESC LIMIT 1),
                (SELECT MAX(r.generation) FROM texture_projection_rows r WHERE r.revision=d.revision AND r.profile=d.profile AND r.identity=d.identity),
                (SELECT MAX(a.generation) FROM texture_projection_alias_versions a WHERE a.revision=d.revision AND a.profile=d.profile AND a.identity=d.identity),
                EXISTS(SELECT 1 FROM texture_projection_rows r WHERE r.revision=d.revision AND r.profile=d.profile
                    AND r.identity=d.identity AND r.generation=$generation),
                EXISTS(SELECT 1 FROM texture_projection_alias_versions a WHERE a.revision=d.revision AND a.profile=d.profile
                    AND a.identity=d.identity AND a.generation=$generation),
                (SELECT a.alias FROM texture_projection_alias_versions a WHERE a.revision=d.revision AND a.profile=d.profile
                    AND a.identity=d.identity AND a.generation<$generation ORDER BY a.generation DESC LIMIT 1),
                (SELECT h.alias FROM texture_projection_history_index h WHERE h.revision=d.revision AND h.profile=d.profile
                    AND h.identity=d.identity ORDER BY h.generation DESC LIMIT 1)
            FROM texture_projection_history_dirty d WHERE d.revision=$revision AND d.profile=$profile
            """, profile, transaction);
        check.Parameters.AddWithValue("$generation", generation);
        using var rows = check.ExecuteReader();
        while (rows.Read())
        {
            long? Value(int ordinal) => rows.IsDBNull(ordinal) ? null : rows.GetInt64(ordinal);
            var priorData = Value(0);
            var priorAlias = Value(1);
            var history = Value(2);
            if (Value(6) > generation || Value(7) > generation || (rows.GetInt64(8) == 0 && rows.GetInt64(9) == 0)) return false;
            if (priorData is null)
            {
                if (history is not null) return false;
                continue;
            }
            if (history != Math.Max(priorData.Value, priorAlias ?? 0) || rows.GetInt64(3) != 1 ||
                Value(4) != priorData || !rows.IsDBNull(5) ||
                !Equals(rows.IsDBNull(10) ? null : rows.GetString(10), rows.IsDBNull(11) ? null : rows.GetString(11))) return false;
        }
        return true;
    }
}
