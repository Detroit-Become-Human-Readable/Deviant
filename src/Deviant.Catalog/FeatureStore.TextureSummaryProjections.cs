using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    public const int TextureBrowseSummaryBatchSize = 128;
    private sealed record TextureSummaryValue(int Facet, string Key, string Qualifier = "");

    private void EnsureTextureSummarySchema(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_browse_summary_state(revision TEXT NOT NULL,profile TEXT NOT NULL,
                version INTEGER NOT NULL,cursor_ordinal INTEGER NOT NULL DEFAULT -1,cursor_identity TEXT NOT NULL DEFAULT '',seed_complete INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(revision,profile)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS texture_browse_summary_dirty(revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,PRIMARY KEY(revision,profile,identity)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS texture_browse_summary_members(revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,facet INTEGER NOT NULL,key_value TEXT NOT NULL,qualifier TEXT NOT NULL,
                PRIMARY KEY(revision,profile,identity,facet,key_value,qualifier)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS texture_browse_summary_values(revision TEXT NOT NULL,profile TEXT NOT NULL,
                facet INTEGER NOT NULL,key_value TEXT NOT NULL,qualifier TEXT NOT NULL,reference_count INTEGER NOT NULL CHECK(reference_count>=0),
                PRIMARY KEY(revision,profile,facet,key_value,qualifier)) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
        foreach (var column in new[] { (Name: "cursor_ordinal", Type: "INTEGER NOT NULL DEFAULT -1"),
            (Name: "cursor_identity", Type: "TEXT NOT NULL DEFAULT ''"), (Name: "seed_complete", Type: "INTEGER NOT NULL DEFAULT 0") })
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('texture_browse_summary_state') WHERE name=$name";
            command.Parameters.AddWithValue("$name", column.Name);
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            {
                command.CommandText = "ALTER TABLE texture_browse_summary_state ADD COLUMN " + column.Name + " " + column.Type;
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
        }
        foreach (string table in new[] { "texture_projection_rows", "texture_projection_links", "texture_projection_aliases" })
        foreach (string action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string changed = action == "DELETE" ? "old" : "new";
            command.CommandText = $"CREATE TRIGGER IF NOT EXISTS {table}_summary_{action.ToLowerInvariant()} AFTER {action} ON {table} BEGIN " +
                $"INSERT OR IGNORE INTO texture_browse_summary_dirty VALUES({changed}.revision,{changed}.profile,{changed}.identity); " +
                (action == "UPDATE" ? "INSERT OR IGNORE INTO texture_browse_summary_dirty VALUES(old.revision,old.profile,old.identity); " : "") + "END";
            command.ExecuteNonQuery();
        }
    }

    public Task PrepareTextureBrowseSummaryAsync(string profile, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        return Task.Run(async () =>
        {
            while (!PrepareTextureBrowseSummaryBatch(profile, token))
            { token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }, token);
    }

    private bool PrepareTextureBrowseSummaryBatch(string profile, CancellationToken token)
    {
        int processed = 0, seeded = 0; bool complete;
        lock (gate) complete = CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            using (var history = TextureCommand("SELECT 1 FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile LIMIT 1", profile, transaction))
                if (history.ExecuteScalar() is not null)
                    throw new ArchiveException("TextureProjectionHistoryPending", "Texture indexing is preparing a new generation.");
            using (var version = TextureCommand("SELECT version FROM texture_browse_summary_state WHERE revision=$revision AND profile=$profile", profile, transaction))
                if (version.ExecuteScalar() is long storedVersion && storedVersion != 1)
                    throw new ArchiveException("TextureSummaryVersionUnsupported", "The texture summary uses a newer cache version.");
            using (var state = TextureCommand("INSERT OR IGNORE INTO texture_browse_summary_state(revision,profile,version) VALUES($revision,$profile,1)", profile, transaction))
                state.ExecuteNonQuery();
            long cursorOrdinal; string cursorIdentity; bool seededAll;
            using (var state = TextureCommand("SELECT cursor_ordinal,cursor_identity,seed_complete FROM texture_browse_summary_state WHERE revision=$revision AND profile=$profile", profile, transaction))
            using (var row = state.ExecuteReader())
            { row.Read(); cursorOrdinal = row.GetInt64(0); cursorIdentity = row.GetString(1); seededAll = row.GetInt32(2) != 0; }
            if (!seededAll)
            {
                var identities = new List<(long Ordinal, string Identity)>();
                using (var batch = TextureCommand("SELECT ordinal,identity FROM texture_projection_history_index INDEXED BY texture_history_ordinal " +
                    "WHERE revision=$revision AND profile=$profile AND (ordinal,identity)>($cursor,$identity) AND next_generation IS NULL " +
                    "ORDER BY ordinal,identity LIMIT $limit", profile, transaction))
                {
                    batch.Parameters.AddWithValue("$cursor", cursorOrdinal); batch.Parameters.AddWithValue("$identity", cursorIdentity);
                    batch.Parameters.AddWithValue("$limit", TextureBrowseSummaryBatchSize);
                    using var rows = batch.ExecuteReader(); while (rows.Read()) { token.ThrowIfCancellationRequested(); identities.Add((rows.GetInt64(0), rows.GetString(1))); }
                }
                using (var seed = TextureCommand("INSERT OR IGNORE INTO texture_browse_summary_dirty VALUES($revision,$profile,$identity)", profile, transaction))
                {
                    var identity = seed.Parameters.Add("$identity", SqliteType.Text);
                    foreach (var item in identities) { token.ThrowIfCancellationRequested(); identity.Value = item.Identity; seed.ExecuteNonQuery(); }
                }
                seeded = identities.Count;
                if (identities.Count > 0) (cursorOrdinal, cursorIdentity) = identities[^1];
                seededAll = identities.Count < TextureBrowseSummaryBatchSize;
                using var checkpoint = TextureCommand("UPDATE texture_browse_summary_state SET cursor_ordinal=$cursor,cursor_identity=$identity,seed_complete=$complete WHERE revision=$revision AND profile=$profile", profile, transaction);
                checkpoint.Parameters.AddWithValue("$cursor", cursorOrdinal); checkpoint.Parameters.AddWithValue("$identity", cursorIdentity);
                checkpoint.Parameters.AddWithValue("$complete", seededAll ? 1 : 0); checkpoint.ExecuteNonQuery();
            }
            else processed = RefreshTextureSummaryDirty(profile, transaction, token);
            using var pending = TextureCommand("SELECT 1 FROM texture_browse_summary_dirty WHERE revision=$revision AND profile=$profile LIMIT 1", profile, transaction);
            bool done = seededAll && pending.ExecuteScalar() is null;
            token.ThrowIfCancellationRequested(); transaction.Commit(); return done;
        });
        AssetLoadDiagnostics.Counters("TextureBrowser.Store.SummaryBackfillBatch", profile,
            new Dictionary<string, long> { ["Rows"] = processed, ["Seeded"] = seeded, ["Complete"] = complete ? 1 : 0 });
        token.ThrowIfCancellationRequested(); return complete;
    }

    private int RefreshTextureSummaryDirty(string profile, SqliteTransaction transaction, CancellationToken token = default)
    {
        using (var state = TextureCommand("SELECT version FROM texture_browse_summary_state WHERE revision=$revision AND profile=$profile", profile, transaction))
            if (state.ExecuteScalar() is not long version || version != 1) return 0;
        var updates = new List<(string Identity, long? Generation, HashSet<TextureSummaryValue> Values)>();
        using (var batch = TextureCommand("""
            SELECT d.identity,p.generation,COALESCE(p.data_generation,p.generation),p.name_kind,p.kind,p.width,p.height,p.format,
                p.context_key,p.context_label,p.has_conflict,p.has_metadata,p.alias
            FROM texture_browse_summary_dirty d LEFT JOIN texture_projection_history_index p
                ON p.revision=d.revision AND p.profile=d.profile AND p.identity=d.identity AND p.next_generation IS NULL
            WHERE d.revision=$revision AND d.profile=$profile LIMIT $limit
            """, profile, transaction))
        {
            batch.Parameters.AddWithValue("$limit", TextureBrowseSummaryBatchSize);
            using var rows = batch.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var values = new HashSet<TextureSummaryValue>();
                string identity = rows.GetString(0);
                if (rows.IsDBNull(1)) { updates.Add((identity, null, values)); continue; }
                values.Add(new(0, "resources"));
                values.Add(new(0, rows.GetInt32(3) switch { 0 or 1 => "original", 2 => "owner", 3 => "inferred", _ => "fallback" }));
                if (rows.GetInt32(4) is >= 0 and <= 2) values.Add(new(0, rows.GetInt32(4) switch { 0 => "images", 1 => "lighting", _ => "lod" }));
                if (rows.GetInt32(10) != 0) values.Add(new(0, "conflicts"));
                if (rows.GetInt32(11) != 0) values.Add(new(0, "with-metadata"));
                if (!rows.IsDBNull(12)) values.Add(new(0, "aliases"));
                if (!rows.IsDBNull(5) && !rows.IsDBNull(6)) values.Add(new(1,
                    rows.GetInt32(5).ToString(CultureInfo.InvariantCulture), rows.GetInt32(6).ToString(CultureInfo.InvariantCulture)));
                if (!rows.IsDBNull(7)) values.Add(new(2, rows.GetString(7)));
                if (!rows.IsDBNull(8) && !rows.IsDBNull(9)) values.Add(new(4, rows.GetString(8), rows.GetString(9)));
                updates.Add((identity, rows.GetInt64(2), values));
            }
        }
        foreach (var update in updates)
        {
            token.ThrowIfCancellationRequested();
            if (update.Generation is { } generation)
            {
                using var links = TextureCommand("SELECT relation,link_key,role,verified FROM texture_projection_links " +
                    "WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation=$generation AND relation IN(0,3)", profile, transaction);
                links.Parameters.AddWithValue("$identity", update.Identity); links.Parameters.AddWithValue("$generation", generation);
                using var rows = links.ExecuteReader();
                while (rows.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (rows.GetInt32(0) == 3) update.Values.Add(new(5, rows.GetString(1)));
                    else if (rows.GetInt32(3) != 0 && rows.GetString(2).Length > 0) update.Values.Add(new(3, rows.GetString(2)));
                }
            }
            var previous = new HashSet<TextureSummaryValue>();
            using (var members = TextureCommand("SELECT facet,key_value,qualifier FROM texture_browse_summary_members " +
                "WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction))
            {
                members.Parameters.AddWithValue("$identity", update.Identity); using var rows = members.ExecuteReader();
                while (rows.Read()) previous.Add(new(rows.GetInt32(0), rows.GetString(1), rows.GetString(2)));
            }
            foreach (var value in previous.Except(update.Values)) Change(value, -1);
            foreach (var value in update.Values.Except(previous)) Change(value, 1);
            using var clean = TextureCommand("DELETE FROM texture_browse_summary_dirty WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction);
            clean.Parameters.AddWithValue("$identity", update.Identity); clean.ExecuteNonQuery();

            void Change(TextureSummaryValue value, int delta)
            {
                using var write = TextureCommand(delta > 0 ?
                    "INSERT INTO texture_browse_summary_members VALUES($revision,$profile,$identity,$facet,$key,$qualifier); " +
                    "INSERT INTO texture_browse_summary_values VALUES($revision,$profile,$facet,$key,$qualifier,1) " +
                    "ON CONFLICT(revision,profile,facet,key_value,qualifier) DO UPDATE SET reference_count=reference_count+1" :
                    "DELETE FROM texture_browse_summary_members WHERE revision=$revision AND profile=$profile AND identity=$identity AND facet=$facet AND key_value=$key AND qualifier=$qualifier; " +
                    "UPDATE texture_browse_summary_values SET reference_count=reference_count-1 WHERE revision=$revision AND profile=$profile AND facet=$facet AND key_value=$key AND qualifier=$qualifier; " +
                    "DELETE FROM texture_browse_summary_values WHERE revision=$revision AND profile=$profile AND facet=$facet AND key_value=$key AND qualifier=$qualifier AND reference_count=0", profile, transaction);
                write.Parameters.AddWithValue("$identity", update.Identity); write.Parameters.AddWithValue("$facet", value.Facet);
                write.Parameters.AddWithValue("$key", value.Key); write.Parameters.AddWithValue("$qualifier", value.Qualifier); write.ExecuteNonQuery();
            }
        }
        return updates.Count;
    }

    private TextureBrowseSummary? TryReadPreparedTextureBrowseSummary(TextureBrowseToken pin, SqliteConnection read,
        SqliteTransaction transaction, CancellationToken token)
    {
        using (var schema = read.CreateCommand())
        {
            schema.Transaction = transaction; schema.CommandText = "SELECT 1 FROM pragma_table_info('texture_browse_summary_state') WHERE name='seed_complete'";
            if (schema.ExecuteScalar() is null) return null;
        }
        using (var ready = ReadTextureCommand(read, transaction, """
            SELECT 1 FROM texture_browse_summary_state s JOIN texture_projection_heads h ON h.revision=s.revision AND h.profile=s.profile
            WHERE s.revision=$revision AND s.profile=$profile AND s.version=1 AND s.seed_complete=1 AND h.generation=$generation
                AND NOT EXISTS(SELECT 1 FROM texture_browse_summary_dirty d WHERE d.revision=s.revision AND d.profile=s.profile)
            """, revision, pin.Profile))
        {
            ready.Parameters.AddWithValue("$generation", pin.Generation);
            if (ready.ExecuteScalar() is null) return null;
        }
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var dimensions = new List<TextureDimensionOption>(); var formats = new List<string>();
        var roles = new List<string>(); var contexts = new List<TextureContextOption>(); long metadata;
        using (var values = ReadTextureCommand(read, transaction, "SELECT facet,key_value,qualifier,reference_count FROM texture_browse_summary_values " +
            "WHERE revision=$revision AND profile=$profile AND facet<5 ORDER BY facet,key_value,qualifier", revision, pin.Profile))
        using (var rows = values.ExecuteReader())
        {
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested(); string key = rows.GetString(1), qualifier = rows.GetString(2);
                switch (rows.GetInt32(0))
                {
                    case 0: counts.Add(key, rows.GetInt64(3)); break;
                    case 1: dimensions.Add(new(int.Parse(key, CultureInfo.InvariantCulture), int.Parse(qualifier, CultureInfo.InvariantCulture))); break;
                    case 2: formats.Add(key); break;
                    case 3: roles.Add(key); break;
                    case 4: contexts.Add(new(key, qualifier)); break;
                }
            }
        }
        using (var count = ReadTextureCommand(read, transaction, "SELECT COUNT(*) FROM texture_browse_summary_values " +
            "WHERE revision=$revision AND profile=$profile AND facet=5", revision, pin.Profile)) metadata = Convert.ToInt64(count.ExecuteScalar());
        token.ThrowIfCancellationRequested();
        AssetLoadDiagnostics.Counters("TextureBrowser.Store.PreparedSummaryRead", pin.Profile,
            new Dictionary<string, long> { ["Generation"] = pin.Generation });
        return new(new(Array.AsReadOnly(dimensions.OrderBy(value => value.Width).ThenBy(value => value.Height).ToArray()),
            Array.AsReadOnly(formats.ToArray()), Array.AsReadOnly(roles.ToArray()),
            Array.AsReadOnly(contexts.OrderBy(value => value.Label, StringComparer.Ordinal).ToArray())),
            new(counts.GetValueOrDefault("resources"), counts.GetValueOrDefault("with-metadata"), counts.GetValueOrDefault("original"),
                counts.GetValueOrDefault("owner"), counts.GetValueOrDefault("inferred"), counts.GetValueOrDefault("fallback"),
                counts.GetValueOrDefault("aliases"), counts.GetValueOrDefault("conflicts"), counts.GetValueOrDefault("images"),
                counts.GetValueOrDefault("lighting"), counts.GetValueOrDefault("lod"), metadata));
    }
}
