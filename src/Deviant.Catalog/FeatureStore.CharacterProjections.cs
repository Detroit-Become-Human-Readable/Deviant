using System.Text.Json;
using System.Text;
using Deviant.Assets;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed record CharacterGroupProjectionPage(
    IReadOnlyList<AssetDescriptor> Groups,
    long Total,
    long Offset,
    long CompleteGroupCount,
    long PartialGroupCount,
    long UnknownGroupCount,
    long KnownClipCount);

public sealed record CharacterModelProjectionPage(IReadOnlyList<AssetDescriptor> Models, long Total, long Offset);

public sealed partial class FeatureStore
{
    public const int CharacterProjectionPageLimit = 512;
    private const string CharacterProjectionVersion = "character-models-v5";
    private const int CharacterProjectionBatchSize = 256;
    private static readonly HashSet<string> CharacterSearchMetadataKeys = new(StringComparer.Ordinal)
    {
        "character.key", "character.name", "character.candidate-key", "character.candidate-name",
        "character.candidate-variant", "model.variant", "model.source-label", "NativeId", "NativeType"
    };
    private readonly object characterReadGate = new();
    private readonly SemaphoreSlim characterPreparationGate = new(1, 1);
    private SqliteConnection? characterReadConnection;

    private static string CharacterProjectionActorKey(AssetDescriptor asset)
    {
        var kind = asset.Metadata.GetValueOrDefault("model.kind") ?? "unclassified";
        if (kind == "character") return asset.Metadata.GetValueOrDefault("character.key") ?? "";
        if (kind == "unclassified" && !string.IsNullOrWhiteSpace(asset.Metadata.GetValueOrDefault("character.candidate-name")))
            return asset.Metadata.GetValueOrDefault("character.candidate-key") ?? "";
        return "";
    }

    /// <summary>Builds the character lookup tables in bounded commits. Call during worker preparation before browsing.</summary>
    public async Task PrepareCharacterProjectionsAsync(CancellationToken token = default)
    {
        await characterPreparationGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => PrepareCharacterProjections(token), token).ConfigureAwait(false); }
        finally { characterPreparationGate.Release(); }
    }

    private void PrepareCharacterProjections(CancellationToken token)
    {
        lock (gate)
        {
            if (characterProjectionReady) return;
            bool ready = CatalogService.ExecuteInterruptible(connection, token, () =>
            {
                EnsureCharacterProjectionSchema();
                using var state = connection.CreateCommand();
                state.CommandText = "SELECT version FROM character_projection_state WHERE revision=$revision";
                state.Parameters.AddWithValue("$revision", revision);
                if (string.Equals(state.ExecuteScalar() as string, CharacterProjectionVersion, StringComparison.Ordinal))
                {
                    characterProjectionWritesEnabled = true;
                    characterProjectionReady = true;
                    return true;
                }
                characterProjectionWritesEnabled = true;
                characterProjectionReady = false;
                using var transaction = connection.BeginTransaction();
                foreach (var table in new[] { "character_projection_models", "character_projection_clips", "character_projection_groups", "character_projection_group_history" })
                {
                    using var clear = connection.CreateCommand(); clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM " + table + " WHERE revision=$revision";
                    clear.Parameters.AddWithValue("$revision", revision); clear.ExecuteNonQuery();
                }
                using (var mark = connection.CreateCommand())
                {
                    mark.Transaction = transaction;
                    mark.CommandText = "INSERT INTO character_projection_state(revision,version) VALUES($revision,'building') ON CONFLICT(revision) DO UPDATE SET version='building'";
                    mark.Parameters.AddWithValue("$revision", revision); mark.ExecuteNonQuery();
                }
                transaction.Commit(); return false;
            });
            if (ready) return;
        }

        long cursor = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            List<(long RowId, string Payload)> batch;
            lock (gate)
            {
                batch = CatalogService.ExecuteInterruptible(connection, token, () =>
                {
                    using var read = connection.CreateCommand();
                    read.CommandText = "SELECT row_id,payload FROM assets_v2 WHERE revision=$revision AND family='character-model' AND row_id>$cursor ORDER BY row_id LIMIT $limit";
                    read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$cursor", cursor);
                    read.Parameters.AddWithValue("$limit", CharacterProjectionBatchSize);
                    using var rows = read.ExecuteReader(); var found = new List<(long, string)>();
                    while (rows.Read()) found.Add((rows.GetInt64(0), rows.GetString(1)));
                    return found;
                });
            }

            if (batch.Count == 0)
            {
                bool finished;
                lock (gate) finished = CatalogService.ExecuteInterruptible(connection, token, () =>
                {
                    using var pending = connection.CreateCommand();
                    pending.CommandText = "SELECT EXISTS(SELECT 1 FROM assets_v2 WHERE revision=$revision AND family='character-model' AND row_id>$cursor)";
                    pending.Parameters.AddWithValue("$revision", revision); pending.Parameters.AddWithValue("$cursor", cursor);
                    if ((long)pending.ExecuteScalar()! != 0) return false;
                    using var transaction = connection.BeginTransaction();
                    using var ready = connection.CreateCommand(); ready.Transaction = transaction;
                    ready.CommandText = "UPDATE character_projection_state SET version=$version WHERE revision=$revision";
                    ready.Parameters.AddWithValue("$version", CharacterProjectionVersion); ready.Parameters.AddWithValue("$revision", revision);
                    ready.ExecuteNonQuery(); transaction.Commit(); characterProjectionReady = true; return true;
                });
                if (finished) return;
                continue;
            }

            // Parse outside the writer lock. A malformed legacy model is skipped without blocking or
            // preventing valid rows in this batch from being projected.
            var parsed = new List<(long RowId, AssetDescriptor Asset)>(batch.Count);
            foreach (var item in batch)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (JsonSerializer.Deserialize<AssetDescriptor>(item.Payload) is { Identity: not null, Metadata: not null, Names: not null, Extents: not null, Label: not null } asset)
                        parsed.Add((item.RowId, asset));
                }
                catch (JsonException) { }
                catch (NotSupportedException) { }
            }

            lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
            {
                using var transaction = connection.BeginTransaction();
                foreach (var item in parsed)
                {
                    token.ThrowIfCancellationRequested();
                    RefreshCharacterProjection(item.Asset, item.RowId, transaction, rebuildGroups: false);
                }
                foreach (var key in parsed
                    .Select(item => (Actor: CharacterProjectionActorKey(item.Asset),
                        Profile: item.Asset.Metadata.GetValueOrDefault("model.name-profile") ?? "",
                        Classification: item.Asset.Metadata.GetValueOrDefault("model.classification-profile") ?? ""))
                    .Where(key => key.Actor.Length > 0).Distinct())
                    RebuildCharacterProjectionGroup(key.Actor, key.Profile, key.Classification, transaction);
                transaction.Commit(); return true;
            });
            cursor = batch[^1].RowId;
        }
    }

    /// <summary>Returns a bounded page using a dedicated read-only connection on a worker thread.</summary>
    public Task<CharacterGroupProjectionPage> QueryCharacterGroupsAsync(AssetQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > CharacterProjectionPageLimit)
            throw new ArgumentOutOfRangeException(nameof(query));
        return Task.Run(() => QueryCharacterGroupsReadOnly(query, token), token);
    }

    /// <summary>Returns a bounded model page from the indexed character projection on a worker thread.</summary>
    public Task<CharacterModelProjectionPage> QueryCharacterModelsAsync(AssetQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > CharacterProjectionPageLimit)
            throw new ArgumentOutOfRangeException(nameof(query));
        return Task.Run(() => QueryCharacterModelsReadOnly(query, token), token);
    }

    private CharacterModelProjectionPage QueryCharacterModelsReadOnly(AssetQuery query, CancellationToken token)
    {
        if (!characterProjectionReady) throw new InvalidOperationException("Character projections are not ready. Call PrepareCharacterProjectionsAsync first.");
        lock (characterReadGate)
        {
            var read = CharacterReadConnection();
            return CatalogService.ExecuteInterruptible(read, token, () =>
            {
                using var transaction = read.BeginTransaction();
                using var count = CharacterModelFilter(read, query, transaction, "COUNT(*)");
                var total = (long)count.ExecuteScalar()!;
                using var page = CharacterModelFilter(read, query, transaction, "row_id,identity,payload,animation_state,animation_count");
                var order = query.Sort == AssetSort.Animations
                    ? "CASE WHEN animation_state IN ('complete','partial') THEN animation_count END " + (query.Descending ? "DESC" : "ASC") + " NULLS LAST,name_sort,identity"
                    : "name_sort,identity";
                page.CommandText += " ORDER BY " + order + " LIMIT $limit OFFSET $offset";
                page.Parameters.AddWithValue("$limit", query.Limit); page.Parameters.AddWithValue("$offset", query.Offset);
                var models = new List<AssetDescriptor>(); using (var rows = page.ExecuteReader())
                {
                    while (rows.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var asset = JsonSerializer.Deserialize<AssetDescriptor>(rows.GetString(2));
                        if (asset is null) continue;
                        var metadata = asset.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        metadata["model.animation-state"] = rows.GetString(3);
                        if (rows.GetString(3) == "unknown") metadata.Remove("model.animation-count");
                        else metadata["model.animation-count"] = rows.GetInt64(4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        models.Add(asset with { Metadata = metadata });
                    }
                }
                transaction.Commit();
                return new CharacterModelProjectionPage(models, total, query.Offset);
            });
        }
    }

    private SqliteCommand CharacterModelFilter(SqliteConnection read, AssetQuery query, SqliteTransaction transaction, string select)
    {
        var command = read.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + select + " FROM character_projection_models WHERE revision=$revision";
        command.Parameters.AddWithValue("$revision", revision);
        if (query.ExactKey is { } identity) { command.CommandText += " AND identity=$identity"; command.Parameters.AddWithValue("$identity", identity); }
        if (query.ModelKind is { } kind) { command.CommandText += " AND model_kind=$kind"; command.Parameters.AddWithValue("$kind", kind); }
        if (query.ModelNameProfile is { } profile) { command.CommandText += " AND name_profile=$profile"; command.Parameters.AddWithValue("$profile", profile); }
        if (query.CharacterGroup is { } actor) { command.CommandText += " AND actor=$actor"; command.Parameters.AddWithValue("$actor", actor); }
        if (query.MaximumCatalogRow is { } boundary) { command.CommandText += " AND row_id<=$boundary"; command.Parameters.AddWithValue("$boundary", boundary); }
        foreach (var term in (query.Search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = "$search" + command.Parameters.Count;
            command.CommandText += " AND deviant_contains(search," + name + ")";
            command.Parameters.AddWithValue(name, term);
            if (term.EnumerateRunes().Count() >= 3)
            {
                command.CommandText += " AND row_id IN(SELECT rowid FROM character_projection_models_fts WHERE character_projection_models_fts MATCH " + name + "Fts)";
                command.Parameters.AddWithValue(name + "Fts", "\"" + term.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
        }
        return command;
    }

    private CharacterGroupProjectionPage QueryCharacterGroupsReadOnly(AssetQuery query, CancellationToken token)
    {
        if (!characterProjectionReady) throw new InvalidOperationException("Character projections are not ready. Call PrepareCharacterProjectionsAsync first.");
        lock (characterReadGate)
        {
            var read = CharacterReadConnection();
            return CatalogService.ExecuteInterruptible(read, token, () =>
            {
            using var transaction = read.BeginTransaction();
            var filter = CharacterGroupFilter(read, query, transaction);
            long total, complete, partial, unknown, knownClips;
            using (var count = filter)
            {
                count.CommandText = "SELECT COUNT(*),COALESCE(SUM(state='complete'),0),COALESCE(SUM(state='partial'),0),COALESCE(SUM(state='unknown'),0),COALESCE(SUM(clip_count),0) FROM (" + count.CommandText + ")";
                using var reader = count.ExecuteReader(); reader.Read();
                total = reader.GetInt64(0); complete = reader.GetInt64(1); partial = reader.GetInt64(2);
                unknown = reader.GetInt64(3); knownClips = reader.GetInt64(4);
            }

            using var page = CharacterGroupFilter(read, query, transaction);
            var order = query.Sort == AssetSort.Animations
                ? "CASE WHEN state IN ('complete','partial') THEN clip_count END " + (query.Descending ? "DESC" : "ASC") + " NULLS LAST,name_sort,actor"
                : "name_sort,actor";
            page.CommandText = "SELECT representative_payload,actor,actor_name,clip_count,state,model_kind,name_profile,classification_profile FROM (" + page.CommandText + ") ORDER BY " + order + " LIMIT $limit OFFSET $offset";
            page.Parameters.AddWithValue("$limit", query.Limit); page.Parameters.AddWithValue("$offset", query.Offset);
            var groups = new List<AssetDescriptor>();
            var aliasScopes = new List<(string Actor, string Profile, string Classification)>();
            using (var rows = page.ExecuteReader())
            {
                while (rows.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var asset = JsonSerializer.Deserialize<AssetDescriptor>(rows.GetString(0));
                    if (asset is null) continue;
                    var metadata = asset.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    var modelKind = rows.GetString(5);
                    if (modelKind == "character")
                    {
                        metadata["model.kind"] = "character";
                        metadata["character.key"] = rows.GetString(1);
                        metadata["character.name"] = rows.GetString(2);
                        metadata.Remove("character.candidate-key");
                        metadata.Remove("character.candidate-name");
                        metadata.Remove("character.candidate-variant");
                        metadata.Remove("character.candidate-needs-detail-check");
                    }
                    else
                    {
                        metadata["model.kind"] = "unclassified";
                        metadata.Remove("character.key");
                        metadata.Remove("character.name");
                        metadata["character.candidate-key"] = rows.GetString(1);
                        metadata["character.candidate-name"] = rows.GetString(2);
                        metadata["character.candidate-needs-detail-check"] = "true";
                    }
                    metadata["model.animation-state"] = rows.GetString(4);
                    if (!string.Equals(rows.GetString(4), "unknown", StringComparison.Ordinal))
                        metadata["model.animation-count"] = rows.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else metadata.Remove("model.animation-count");
                    groups.Add(asset with
                    {
                        Label = rows.GetString(2), SortKey = rows.GetString(2),
                        Metadata = metadata
                    });
                    aliasScopes.Add((rows.GetString(1), rows.GetString(6), rows.GetString(7)));
                }
            }
            var boundary = query.MaximumCatalogRow ?? long.MaxValue;
            for (var index = 0; index < groups.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var scope = aliasScopes[index];
                var aliases = ReadCharacterGroupAliases(read, transaction, scope.Actor, scope.Profile,
                    scope.Classification, boundary, token);
                groups[index] = groups[index] with { Names = groups[index].Names.Concat(aliases).Distinct().ToArray() };
            }
            token.ThrowIfCancellationRequested(); transaction.Commit();
            return new CharacterGroupProjectionPage(groups, total, query.Offset, complete, partial, unknown, knownClips);
            });
        }
    }

    private NameEvidence[] ReadCharacterGroupAliases(SqliteConnection read, SqliteTransaction transaction,
        string actor, string profile, string classification, long boundary, CancellationToken token)
    {
        const int maximumAliasPayloadBytes = 1024 * 1024;
        const int maximumAliases = 512;
        const int maximumAliasBytes = 1024 * 1024;
        var aliases = new List<NameEvidence>(maximumAliases);
        var seen = new HashSet<NameEvidence>();
        var bytesUsed = 0;
        using var command = read.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT aliases_payload FROM character_projection_models WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification AND row_id<=$boundary AND aliases_payload<>'[]' AND length(CAST(aliases_payload AS BLOB))<=$maxBytes ORDER BY row_id";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$classification", classification);
        command.Parameters.AddWithValue("$boundary", boundary);
        command.Parameters.AddWithValue("$maxBytes", maximumAliasPayloadBytes);
        using var rows = command.ExecuteReader();
        while (rows.Read() && aliases.Count < maximumAliases && bytesUsed < maximumAliasBytes)
        {
            token.ThrowIfCancellationRequested();
            NameEvidence[] parsed;
            try { parsed = JsonSerializer.Deserialize<NameEvidence[]>(rows.GetString(0)) ?? []; }
            catch (JsonException) { continue; }
            foreach (var alias in parsed)
            {
                if (alias is null || !seen.Add(alias)) continue;
                var aliasBytes = JsonSerializer.SerializeToUtf8Bytes(alias).Length;
                if (aliasBytes > maximumAliasBytes - bytesUsed) { seen.Remove(alias); continue; }
                aliases.Add(alias); bytesUsed += aliasBytes;
                if (aliases.Count == maximumAliases || bytesUsed >= maximumAliasBytes) break;
            }
        }
        return aliases.ToArray();
    }

    private SqliteCommand CharacterGroupFilter(SqliteConnection read, AssetQuery query, SqliteTransaction transaction)
    {
        var command = read.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
            SELECT h.actor AS actor,h.actor_name AS actor_name,h.state AS state,h.clip_count AS clip_count,
                   h.model_kind AS model_kind,
                   m.payload AS representative_payload,h.name_profile AS name_profile,
                   h.classification_profile AS classification_profile,h.name_sort AS name_sort,h.first_row AS first_row
            FROM character_projection_group_history h
            JOIN character_projection_models m ON m.revision=h.revision AND m.identity=h.representative_identity
            WHERE h.revision=$revision AND h.is_present=1
              AND h.watermark=(SELECT MAX(previous.watermark) FROM character_projection_group_history previous
                  WHERE previous.revision=h.revision AND previous.actor=h.actor AND previous.name_profile=h.name_profile
                    AND previous.classification_profile=h.classification_profile AND previous.watermark<=$boundary)
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$boundary", query.MaximumCatalogRow ?? long.MaxValue);
        if (query.ModelNameProfile is { } profile) { command.CommandText += " AND h.name_profile=$profile"; command.Parameters.AddWithValue("$profile", profile); }
        foreach (var term in (query.Search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = "$search" + command.Parameters.Count;
            command.CommandText += " AND EXISTS(SELECT 1 FROM character_projection_models searched WHERE searched.revision=h.revision AND searched.actor=h.actor AND searched.name_profile=h.name_profile AND searched.classification_profile=h.classification_profile AND searched.row_id<=$boundary AND deviant_contains(searched.search," + name + ")";
            command.Parameters.AddWithValue(name, term);
            if (term.EnumerateRunes().Count() >= 3)
            {
                command.CommandText += " AND searched.row_id IN(SELECT rowid FROM character_projection_models_fts WHERE character_projection_models_fts MATCH " + name + "Fts)";
                command.Parameters.AddWithValue(name + "Fts", "\"" + term.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
            command.CommandText += ")";
        }
        if (query.ModelKind is { } selectedKind)
        { command.CommandText += " AND h.model_kind=$kind"; command.Parameters.AddWithValue("$kind", selectedKind); }
        return command;
    }

    private bool characterReadClosed;

    private SqliteConnection CharacterReadConnection()
    {
        ObjectDisposedException.ThrowIf(characterReadClosed, this);
        if (characterReadConnection is not null) return characterReadConnection;
        var read = CreateReaderConnection();
        read.Open();
        read.CreateFunction<string, string, bool>("deviant_contains", static (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        characterReadConnection = read; return read;
    }

    private void EnsureCharacterProjectionSchema()
    {
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS character_projection_state(revision TEXT PRIMARY KEY,version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS character_projection_models(
                revision TEXT NOT NULL,identity TEXT NOT NULL,row_id INTEGER NOT NULL,actor TEXT NOT NULL,name_profile TEXT NOT NULL,
                classification_profile TEXT NOT NULL,model_kind TEXT NOT NULL,actor_name TEXT NOT NULL,name_sort TEXT NOT NULL,
                search TEXT NOT NULL,payload TEXT NOT NULL,aliases_payload TEXT NOT NULL,animation_state TEXT NOT NULL,animation_count INTEGER NOT NULL,PRIMARY KEY(revision,identity));
            CREATE INDEX IF NOT EXISTS character_projection_models_group ON character_projection_models(revision,actor,name_profile,classification_profile,row_id);
            CREATE INDEX IF NOT EXISTS character_projection_models_page ON character_projection_models(revision,name_profile,name_sort,identity);
            CREATE INDEX IF NOT EXISTS character_projection_models_actor_page ON character_projection_models(revision,actor,name_profile,name_sort,identity,row_id);
            CREATE INDEX IF NOT EXISTS character_projection_models_kind_page ON character_projection_models(revision,model_kind,name_profile,name_sort,identity);
            CREATE INDEX IF NOT EXISTS character_projection_models_animation ON character_projection_models(revision,name_profile,animation_state,animation_count,name_sort,identity);
            CREATE VIRTUAL TABLE IF NOT EXISTS character_projection_models_fts USING fts5(search,content='character_projection_models',content_rowid='row_id',tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS character_projection_models_ai AFTER INSERT ON character_projection_models BEGIN
                INSERT INTO character_projection_models_fts(rowid,search) VALUES(new.row_id,new.search); END;
            CREATE TRIGGER IF NOT EXISTS character_projection_models_ad AFTER DELETE ON character_projection_models BEGIN
                INSERT INTO character_projection_models_fts(character_projection_models_fts,rowid,search) VALUES('delete',old.row_id,old.search); END;
            CREATE TRIGGER IF NOT EXISTS character_projection_models_au AFTER UPDATE OF search ON character_projection_models WHEN old.search!=new.search BEGIN
                INSERT INTO character_projection_models_fts(character_projection_models_fts,rowid,search) VALUES('delete',old.row_id,old.search);
                INSERT INTO character_projection_models_fts(rowid,search) VALUES(new.row_id,new.search); END;
            CREATE TABLE IF NOT EXISTS character_projection_clips(
                revision TEXT NOT NULL,actor TEXT NOT NULL,name_profile TEXT NOT NULL,classification_profile TEXT NOT NULL,
                clip_identity TEXT NOT NULL,ref_count INTEGER NOT NULL,PRIMARY KEY(revision,actor,name_profile,classification_profile,clip_identity));
            CREATE TABLE IF NOT EXISTS character_projection_groups(
                row_id INTEGER PRIMARY KEY,
                revision TEXT NOT NULL,actor TEXT NOT NULL,name_profile TEXT NOT NULL,classification_profile TEXT NOT NULL,
                model_kind TEXT NOT NULL,actor_name TEXT NOT NULL,name_sort TEXT NOT NULL,search TEXT NOT NULL,
                representative_payload TEXT NOT NULL,aliases_json TEXT NOT NULL,first_row INTEGER NOT NULL,
                model_count INTEGER NOT NULL,clip_count INTEGER NOT NULL,state TEXT NOT NULL,
                UNIQUE(revision,actor,name_profile,classification_profile));
            CREATE INDEX IF NOT EXISTS character_projection_groups_name ON character_projection_groups(revision,name_profile,name_sort,actor);
            CREATE INDEX IF NOT EXISTS character_projection_groups_animation ON character_projection_groups(revision,name_profile,state,clip_count,name_sort,actor);
            CREATE VIRTUAL TABLE IF NOT EXISTS character_projection_groups_fts USING fts5(search,content='character_projection_groups',content_rowid='row_id',tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS character_projection_groups_ai AFTER INSERT ON character_projection_groups BEGIN
                INSERT INTO character_projection_groups_fts(rowid,search) VALUES(new.row_id,new.search); END;
            CREATE TRIGGER IF NOT EXISTS character_projection_groups_ad AFTER DELETE ON character_projection_groups BEGIN
                INSERT INTO character_projection_groups_fts(character_projection_groups_fts,rowid,search) VALUES('delete',old.row_id,old.search); END;
            CREATE TRIGGER IF NOT EXISTS character_projection_groups_au AFTER UPDATE OF search ON character_projection_groups WHEN old.search!=new.search BEGIN
                INSERT INTO character_projection_groups_fts(character_projection_groups_fts,rowid,search) VALUES('delete',old.row_id,old.search);
                INSERT INTO character_projection_groups_fts(rowid,search) VALUES(new.row_id,new.search); END;
            CREATE TABLE IF NOT EXISTS character_projection_group_history(
                history_id INTEGER PRIMARY KEY,revision TEXT NOT NULL,actor TEXT NOT NULL,name_profile TEXT NOT NULL,
                classification_profile TEXT NOT NULL,watermark INTEGER NOT NULL,is_present INTEGER NOT NULL,
                model_kind TEXT NOT NULL,actor_name TEXT NOT NULL,name_sort TEXT NOT NULL,representative_identity TEXT NOT NULL,
                first_row INTEGER NOT NULL,model_count INTEGER NOT NULL,clip_count INTEGER NOT NULL,state TEXT NOT NULL,
                UNIQUE(revision,actor,name_profile,classification_profile,watermark));
            CREATE INDEX IF NOT EXISTS character_projection_group_history_page
                ON character_projection_group_history(revision,name_profile,actor,classification_profile,watermark);
            """;
        schema.ExecuteNonQuery();
    }

    private void InitializeCharacterProjectionForAssetWrites()
    {
        if (characterProjectionWritesEnabled) return;
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM assets_v2 WHERE revision=$revision";
        count.Parameters.AddWithValue("$revision", revision); var existingRows = (long)count.ExecuteScalar()!;
        EnsureCharacterProjectionSchema();
        string? stateValue;
        using (var state = connection.CreateCommand())
        {
            state.CommandText = "SELECT version FROM character_projection_state WHERE revision=$revision";
            state.Parameters.AddWithValue("$revision", revision); stateValue = state.ExecuteScalar() as string;
        }
        characterProjectionWritesEnabled = true;
        if (existingRows == 0)
        {
            using var state = connection.CreateCommand();
            state.CommandText = "INSERT INTO character_projection_state(revision,version) VALUES($revision,$version) ON CONFLICT(revision) DO UPDATE SET version=excluded.version";
            state.Parameters.AddWithValue("$revision", revision); state.Parameters.AddWithValue("$version", CharacterProjectionVersion); state.ExecuteNonQuery();
            characterProjectionReady = true;
        }
        else characterProjectionReady = string.Equals(stateValue, CharacterProjectionVersion, StringComparison.Ordinal);
    }

    private void RefreshCharacterProjection(AssetDescriptor asset, long rowId, SqliteTransaction transaction, bool rebuildGroups = true)
    {
        var identity = asset.Identity.Key;
        (string Actor, string Profile, string Classification)? oldKey = null;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction; existing.CommandText = "SELECT actor,name_profile,classification_profile FROM character_projection_models WHERE revision=$revision AND identity=$identity";
            existing.Parameters.AddWithValue("$revision", revision); existing.Parameters.AddWithValue("$identity", identity);
            using var row = existing.ExecuteReader();
            if (row.Read()) oldKey = (row.GetString(0), row.GetString(1), row.GetString(2));
        }
        if (oldKey is { } previous) RemoveCharacterProjectionModel(identity, previous, transaction);
        if (asset.Family != "character-model")
        {
            if (rebuildGroups && oldKey is { } removed && removed.Actor.Length > 0)
                RebuildCharacterProjectionGroup(removed.Actor, removed.Profile, removed.Classification, transaction);
            return;
        }
        var modelKind = asset.Metadata.GetValueOrDefault("model.kind") ?? "unclassified";
        var actor = CharacterProjectionActorKey(asset);
        var profile = asset.Metadata.GetValueOrDefault("model.name-profile") ?? "";
        var classification = asset.Metadata.GetValueOrDefault("model.classification-profile") ?? "";
        var actorName = modelKind == "character"
            ? asset.Metadata.GetValueOrDefault("character.name") ?? asset.Label
            : actor.Length > 0 ? asset.Metadata.GetValueOrDefault("character.candidate-name") ?? asset.Label : asset.Label;
        var search = string.Join('\n', new[] { asset.SearchTerms, asset.Label, actorName, actor }
            .Concat(asset.Names.Select(name => name.Text))
            .Concat(asset.Metadata.Where(pair => CharacterSearchMetadataKeys.Contains(pair.Key)).Select(pair => pair.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var candidate = modelKind == "unclassified" && actor.Length > 0;
        var state = candidate ? "unknown" : asset.Metadata.GetValueOrDefault("model.animation-state") ?? "unknown";
        var animationsText = candidate ? null : asset.Metadata.GetValueOrDefault("model.animation-identities");
        string[] clips = [];
        if (animationsText is { Length: <= 4 * 1024 * 1024 })
        {
            try { clips = (JsonSerializer.Deserialize<string[]>(animationsText) ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray(); }
            catch (JsonException) { clips = []; }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO character_projection_models(revision,identity,row_id,actor,name_profile,classification_profile,model_kind,actor_name,name_sort,search,payload,aliases_payload,animation_state,animation_count) VALUES($revision,$identity,$row,$actor,$profile,$classification,$modelKind,$name,$sort,$search,$payload,$aliases,$state,$clips)";
            insert.Parameters.AddWithValue("$revision", revision); insert.Parameters.AddWithValue("$identity", identity); insert.Parameters.AddWithValue("$row", rowId);
            insert.Parameters.AddWithValue("$actor", actor); insert.Parameters.AddWithValue("$profile", profile); insert.Parameters.AddWithValue("$classification", classification);
            insert.Parameters.AddWithValue("$modelKind", modelKind);
            insert.Parameters.AddWithValue("$name", actorName); insert.Parameters.AddWithValue("$sort", Deviant.Assets.NaturalOrder.Key(asset.Label));
            insert.Parameters.AddWithValue("$search", search); insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(asset)); insert.Parameters.AddWithValue("$state", state);
            insert.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(asset.Names.Where(name => name.Origin == NameOrigin.UserAlias).Distinct().Take(512).ToArray()));
            insert.Parameters.AddWithValue("$clips", clips.Length); insert.ExecuteNonQuery();
        }
        if (modelKind == "character" && actor.Length > 0)
        {
            foreach (var clip in clips)
            {
                using var link = connection.CreateCommand(); link.Transaction = transaction;
                link.CommandText = "INSERT INTO character_projection_clips(revision,actor,name_profile,classification_profile,clip_identity,ref_count) VALUES($revision,$actor,$profile,$classification,$clip,1) ON CONFLICT(revision,actor,name_profile,classification_profile,clip_identity) DO UPDATE SET ref_count=ref_count+1";
                link.Parameters.AddWithValue("$revision", revision); link.Parameters.AddWithValue("$actor", actor); link.Parameters.AddWithValue("$profile", profile);
                link.Parameters.AddWithValue("$classification", classification); link.Parameters.AddWithValue("$clip", clip); link.ExecuteNonQuery();
            }
        }
        if (rebuildGroups && oldKey is { } old && old.Actor.Length > 0 && old != (actor, profile, classification))
            RebuildCharacterProjectionGroup(old.Actor, old.Profile, old.Classification, transaction);
        if (rebuildGroups && actor.Length > 0)
            RebuildCharacterProjectionGroup(actor, profile, classification, transaction);
    }

    private void RemoveCharacterProjectionModel(string identity, (string Actor, string Profile, string Classification) key, SqliteTransaction transaction)
    {
        var clips = new List<string>();
        AssetDescriptor? prior = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT payload FROM character_projection_models WHERE revision=$revision AND identity=$identity";
            read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$identity", identity);
            if (read.ExecuteScalar() is string payload) prior = JsonSerializer.Deserialize<AssetDescriptor>(payload);
        }
        if (prior is not null && prior.Metadata.GetValueOrDefault("model.kind") == "character")
        {
            var priorClips = prior.Metadata.GetValueOrDefault("model.animation-identities");
            if (priorClips is { Length: <= 4 * 1024 * 1024 })
            {
                try { clips = JsonSerializer.Deserialize<string[]>(priorClips)?.Distinct(StringComparer.Ordinal).ToList() ?? []; }
                catch (JsonException) { clips.Clear(); }
            }
        }
        foreach (var clip in clips)
        {
            using var decrement = connection.CreateCommand(); decrement.Transaction = transaction;
            decrement.CommandText = "UPDATE character_projection_clips SET ref_count=ref_count-1 WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification AND clip_identity=$clip; DELETE FROM character_projection_clips WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification AND clip_identity=$clip AND ref_count<=0";
            decrement.Parameters.AddWithValue("$revision", revision); decrement.Parameters.AddWithValue("$actor", key.Actor);
            decrement.Parameters.AddWithValue("$profile", key.Profile); decrement.Parameters.AddWithValue("$classification", key.Classification); decrement.Parameters.AddWithValue("$clip", clip); decrement.ExecuteNonQuery();
        }
        using var delete = connection.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM character_projection_models WHERE revision=$revision AND identity=$identity";
        delete.Parameters.AddWithValue("$revision", revision); delete.Parameters.AddWithValue("$identity", identity); delete.ExecuteNonQuery();
    }

    private void RebuildCharacterProjectionGroup(string actor, string profile, string classification, SqliteTransaction transaction)
    {
        long modelCount, firstRow, lastRow, completeModelCount;
        long? validatedRepresentativeRow;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT COUNT(*),MIN(row_id),MAX(row_id)," +
                "SUM(CASE WHEN model_kind='character' AND animation_state='complete' THEN 1 ELSE 0 END)," +
                "MIN(CASE WHEN model_kind='character' THEN row_id END) " +
                "FROM character_projection_models WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification";
            read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$actor", actor);
            read.Parameters.AddWithValue("$profile", profile); read.Parameters.AddWithValue("$classification", classification);
            using var reader = read.ExecuteReader(); reader.Read();
            modelCount = reader.GetInt64(0);
            firstRow = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            lastRow = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            completeModelCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            validatedRepresentativeRow = reader.IsDBNull(4) ? null : reader.GetInt64(4);
        }
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction; clear.CommandText = "DELETE FROM character_projection_groups WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification";
            clear.Parameters.AddWithValue("$revision", revision); clear.Parameters.AddWithValue("$actor", actor); clear.Parameters.AddWithValue("$profile", profile); clear.Parameters.AddWithValue("$classification", classification); clear.ExecuteNonQuery();
        }
        if (modelCount == 0)
        {
            using var boundary = connection.CreateCommand(); boundary.Transaction = transaction;
            boundary.CommandText = "SELECT COALESCE(MAX(row_id),0) FROM assets_v2 WHERE revision=$revision";
            boundary.Parameters.AddWithValue("$revision", revision); var watermark = (long)boundary.ExecuteScalar()!;
            using var tombstone = connection.CreateCommand(); tombstone.Transaction = transaction;
            tombstone.CommandText = "INSERT INTO character_projection_group_history(revision,actor,name_profile,classification_profile,watermark,is_present,model_kind,actor_name,name_sort,representative_identity,first_row,model_count,clip_count,state) VALUES($revision,$actor,$profile,$classification,$watermark,0,'character','', '', '',0,0,0,'unknown') ON CONFLICT(revision,actor,name_profile,classification_profile,watermark) DO UPDATE SET is_present=0";
            tombstone.Parameters.AddWithValue("$revision", revision); tombstone.Parameters.AddWithValue("$actor", actor);
            tombstone.Parameters.AddWithValue("$profile", profile); tombstone.Parameters.AddWithValue("$classification", classification);
            tombstone.Parameters.AddWithValue("$watermark", watermark); tombstone.ExecuteNonQuery();
            return;
        }
        var clipCount = 0L;
        using (var clips = connection.CreateCommand())
        {
            clips.Transaction = transaction; clips.CommandText = "SELECT COUNT(*) FROM character_projection_clips WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification";
            clips.Parameters.AddWithValue("$revision", revision); clips.Parameters.AddWithValue("$actor", actor); clips.Parameters.AddWithValue("$profile", profile); clips.Parameters.AddWithValue("$classification", classification);
            clipCount = (long)clips.ExecuteScalar()!;
        }
        var representativeRow = validatedRepresentativeRow ?? firstRow;
        string representativeIdentity, representativeName, representativeSearch, representativePayload;
        using (var representative = connection.CreateCommand())
        {
            representative.Transaction = transaction;
            representative.CommandText = "SELECT identity,actor_name,search,payload FROM character_projection_models WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification AND row_id=$row";
            representative.Parameters.AddWithValue("$revision", revision); representative.Parameters.AddWithValue("$actor", actor);
            representative.Parameters.AddWithValue("$profile", profile); representative.Parameters.AddWithValue("$classification", classification);
            representative.Parameters.AddWithValue("$row", representativeRow);
            using var row = representative.ExecuteReader();
            if (!row.Read()) throw new InvalidDataException("The selected character group representative disappeared during publication.");
            representativeIdentity = row.GetString(0); representativeName = row.GetString(1);
            representativeSearch = row.GetString(2); representativePayload = row.GetString(3);
        }
        var hasValidatedRepresentative = validatedRepresentativeRow.HasValue;
        var modelKind = hasValidatedRepresentative ? "character" : "unclassified";
        var state = completeModelCount == modelCount
            ? "complete" : clipCount > 0 ? "partial" : "unknown";
        // Aliases are collected for the requested catalog boundary when a group page is read.
        // Avoid rescanning and deserializing every member's alias list during incremental rebuilds.
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO character_projection_groups(revision,actor,name_profile,classification_profile,model_kind,actor_name,name_sort,search,representative_payload,aliases_json,first_row,model_count,clip_count,state) VALUES($revision,$actor,$profile,$classification,$kind,$name,$sort,$search,$payload,$aliases,$first,$models,$clips,$state)";
        insert.Parameters.AddWithValue("$revision", revision); insert.Parameters.AddWithValue("$actor", actor); insert.Parameters.AddWithValue("$profile", profile); insert.Parameters.AddWithValue("$classification", classification);
        insert.Parameters.AddWithValue("$kind", modelKind); insert.Parameters.AddWithValue("$name", representativeName); insert.Parameters.AddWithValue("$sort", Deviant.Assets.NaturalOrder.Key(representativeName));
        // Group search is evaluated against the indexed model rows in CharacterGroupFilter.
        insert.Parameters.AddWithValue("$search", representativeSearch);
        insert.Parameters.AddWithValue("$payload", representativePayload); insert.Parameters.AddWithValue("$aliases", "[]");
        insert.Parameters.AddWithValue("$first", firstRow); insert.Parameters.AddWithValue("$models", modelCount); insert.Parameters.AddWithValue("$clips", clipCount); insert.Parameters.AddWithValue("$state", state);
        insert.ExecuteNonQuery();

        var history = connection.CreateCommand(); history.Transaction = transaction;
        history.CommandText = "INSERT INTO character_projection_group_history(revision,actor,name_profile,classification_profile,watermark,is_present,model_kind,actor_name,name_sort,representative_identity,first_row,model_count,clip_count,state) VALUES($revision,$actor,$profile,$classification,$watermark,1,$kind,$name,$sort,$representative,$first,$models,$clips,$state) ON CONFLICT(revision,actor,name_profile,classification_profile,watermark) DO UPDATE SET is_present=1,model_kind=excluded.model_kind,actor_name=excluded.actor_name,name_sort=excluded.name_sort,representative_identity=excluded.representative_identity,first_row=excluded.first_row,model_count=excluded.model_count,clip_count=excluded.clip_count,state=excluded.state";
        history.Parameters.AddWithValue("$revision", revision); history.Parameters.AddWithValue("$actor", actor);
        history.Parameters.AddWithValue("$profile", profile); history.Parameters.AddWithValue("$classification", classification);
        history.Parameters.AddWithValue("$watermark", lastRow); history.Parameters.AddWithValue("$kind", modelKind); history.Parameters.AddWithValue("$name", representativeName);
        history.Parameters.AddWithValue("$sort", Deviant.Assets.NaturalOrder.Key(representativeName));
        history.Parameters.AddWithValue("$representative", representativeIdentity); history.Parameters.AddWithValue("$first", firstRow);
        history.Parameters.AddWithValue("$models", modelCount); history.Parameters.AddWithValue("$clips", clipCount); history.Parameters.AddWithValue("$state", state);
        history.ExecuteNonQuery();
    }

    private void RemoveMissingCharacterProjections(SqliteTransaction transaction)
    {
        using var list = connection.CreateCommand(); list.Transaction = transaction;
        list.CommandText = "SELECT identity,actor,name_profile,classification_profile FROM character_projection_models WHERE revision=$revision AND identity NOT IN(SELECT identity FROM assets_v2 WHERE revision=$revision)";
        list.Parameters.AddWithValue("$revision", revision); var old = new List<(string, string, string, string)>();
        using (var rows = list.ExecuteReader()) while (rows.Read()) old.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));
        foreach (var item in old)
        {
            var key = (item.Item2, item.Item3, item.Item4);
            RemoveCharacterProjectionModel(item.Item1, key, transaction);
            if (key.Item1.Length > 0) RebuildCharacterProjectionGroup(key.Item1, key.Item2, key.Item3, transaction);
        }
    }

    private void DisposeCharacterReadConnection()
    {
        lock (characterReadGate) { characterReadClosed = true; characterReadConnection?.Dispose(); characterReadConnection = null; }
    }
}
