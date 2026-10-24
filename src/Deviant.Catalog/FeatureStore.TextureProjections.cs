using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed record TextureProjectionOwnerCheckpoint(SourceRevision Revision, ResourceIdentity Identity,
    long StoredSize, uint? AlternativeSize, SourceExtent MetadataExtent, string MembershipKey,
    bool DirectoryParsed, bool HasCatalogMembership, string? Reason, bool MembershipComplete);
public sealed record TextureProjectionStatistics(long Total, long MetadataTextures, long Original, long OwnerRoles,
    long Inferred, long Fallback, long Aliased, long Conflicts, long Images, long Lighting, long LodBuffers, long MetadataRecords);
public sealed record TextureProjectionQuarantine(IReadOnlyList<ResourceIdentity> InvalidRows,
    IReadOnlyList<ResourceIdentity> SupportingOwners);

/// <summary>Versioned texture list rows with selection evidence stored separately.</summary>
public sealed partial class FeatureStore
{
    public const string TextureProjectionSchema = "texture-projection-v1";
    public const int MaximumTextureEvidenceBytes = 16 * 1024 * 1024;
    public const int MaximumTextureNameProofBytes = 64 * 1024;
    private sealed record TextureNameProof(TextureDiscoveryEntry Thin, IReadOnlyList<TextureEvidence> Sources);
    private bool textureSchemaReady;
    private static long textureDiagnosticSequence;
    private readonly Dictionary<TextureCountKey, long> textureCounts = [];
    private long textureCountDataVersion = -1, textureCountChanges = -1;
    private sealed record TextureCountKey(string Profile, long Generation, TextureDiscoveryQuery Query, string? Owner);

    private void EnsureTextureSchema()
    {
        if (textureSchemaReady) return;
        connection.CreateFunction<string, string>("deviant_texture_fold", static value => value.ToUpperInvariant(), isDeterministic: true);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_projection_heads(revision TEXT NOT NULL,profile TEXT NOT NULL,generation INTEGER NOT NULL,
                baseline_count INTEGER NOT NULL,PRIMARY KEY(revision,profile));
            CREATE TABLE IF NOT EXISTS texture_projection_current_roles(revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,generation INTEGER NOT NULL,role TEXT NOT NULL,PRIMARY KEY(revision,profile,identity,role));
            CREATE INDEX IF NOT EXISTS texture_current_role_values ON texture_projection_current_roles(revision,profile,role,identity,generation);
            CREATE TABLE IF NOT EXISTS texture_projection_current_role_rows(revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,generation INTEGER NOT NULL,PRIMARY KEY(revision,profile,identity));
            CREATE TABLE IF NOT EXISTS texture_projection_current_role_state(revision TEXT NOT NULL,profile TEXT NOT NULL,
                cursor_ordinal INTEGER NOT NULL,cursor_identity TEXT NOT NULL,complete INTEGER NOT NULL,PRIMARY KEY(revision,profile));
            CREATE TABLE IF NOT EXISTS texture_projection_rows(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,generation INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,name_kind INTEGER NOT NULL,kind INTEGER NOT NULL,label TEXT NOT NULL,sort_key TEXT NOT NULL,search TEXT NOT NULL,
                width INTEGER,height INTEGER,format TEXT,context_key TEXT,context_label TEXT,has_owner INTEGER NOT NULL,has_conflict INTEGER NOT NULL,
                has_metadata INTEGER NOT NULL,thin TEXT NOT NULL,PRIMARY KEY(revision,profile,identity,generation));
            CREATE INDEX IF NOT EXISTS texture_projection_sort ON texture_projection_rows(revision,profile,sort_key,ordinal,generation);
            CREATE TABLE IF NOT EXISTS texture_projection_links(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,generation INTEGER NOT NULL,
                relation INTEGER NOT NULL,link_key TEXT NOT NULL,role TEXT NOT NULL,label TEXT NOT NULL,verified INTEGER NOT NULL,
                PRIMARY KEY(revision,profile,identity,generation,relation,link_key,role));
            CREATE INDEX IF NOT EXISTS texture_projection_owner ON texture_projection_links(revision,profile,relation,link_key,identity,generation);
            CREATE INDEX IF NOT EXISTS texture_projection_role ON texture_projection_links(revision,profile,relation,role,verified,identity,generation);
            CREATE TABLE IF NOT EXISTS texture_projection_evidence(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,generation INTEGER NOT NULL,
                payload BLOB NOT NULL,sha256 TEXT NOT NULL,PRIMARY KEY(revision,profile,identity,generation));
            CREATE TABLE IF NOT EXISTS texture_projection_name_proofs(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,generation INTEGER NOT NULL,
                payload BLOB NOT NULL,sha256 TEXT NOT NULL,evidence_sha256 TEXT NOT NULL,PRIMARY KEY(revision,profile,identity,generation));
            CREATE TABLE IF NOT EXISTS texture_projection_owners(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,
                generation INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(revision,profile,identity));
            CREATE TABLE IF NOT EXISTS texture_projection_aliases(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,alias TEXT NOT NULL,
                PRIMARY KEY(revision,profile,identity));
            CREATE TABLE IF NOT EXISTS texture_projection_catalogs(revision TEXT NOT NULL,profile TEXT NOT NULL,catalog TEXT NOT NULL,generation INTEGER NOT NULL,
                table_sha256 TEXT NOT NULL,members_sha256 TEXT NOT NULL,member_count INTEGER NOT NULL,PRIMARY KEY(revision,profile,catalog,generation));
            CREATE TABLE IF NOT EXISTS texture_projection_catalog_members(revision TEXT NOT NULL,profile TEXT NOT NULL,catalog TEXT NOT NULL,generation INTEGER NOT NULL,
                identity TEXT NOT NULL,PRIMARY KEY(revision,profile,catalog,generation,identity));
            CREATE INDEX IF NOT EXISTS texture_projection_catalog_member ON texture_projection_catalog_members(revision,profile,identity,catalog,generation);
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_projection_current_migrations(version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS texture_projection_current_dirty(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,
                PRIMARY KEY(revision,profile,identity));
            CREATE TABLE IF NOT EXISTS texture_projection_current(row_id INTEGER PRIMARY KEY,revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,generation INTEGER NOT NULL,ordinal INTEGER NOT NULL,name_kind INTEGER NOT NULL,kind INTEGER NOT NULL,
                label TEXT NOT NULL,sort_key TEXT NOT NULL,search TEXT NOT NULL,width INTEGER,height INTEGER,format TEXT,context_key TEXT,
                context_label TEXT,has_owner INTEGER NOT NULL,has_conflict INTEGER NOT NULL,has_metadata INTEGER NOT NULL,thin TEXT NOT NULL,
                alias TEXT,name_rank INTEGER NOT NULL,effective_sort TEXT NOT NULL,search_fold TEXT NOT NULL,UNIQUE(revision,profile,identity));
            CREATE INDEX IF NOT EXISTS texture_current_named ON texture_projection_current(revision,profile,name_rank,effective_sort,ordinal,identity);
            CREATE INDEX IF NOT EXISTS texture_current_visible_named ON texture_projection_current(revision,profile,name_rank,effective_sort,ordinal,identity) WHERE kind!=2;
            CREATE INDEX IF NOT EXISTS texture_current_kind_named ON texture_projection_current(revision,profile,kind,name_rank,effective_sort,ordinal,identity);
            CREATE INDEX IF NOT EXISTS texture_current_name ON texture_projection_current(revision,profile,effective_sort,ordinal,identity);
            CREATE INDEX IF NOT EXISTS texture_current_ordinal ON texture_projection_current(revision,profile,ordinal,identity);
            CREATE INDEX IF NOT EXISTS texture_current_kind ON texture_projection_current(revision,profile,kind);
            CREATE VIRTUAL TABLE IF NOT EXISTS texture_projection_current_fts USING fts5(search_fold,content='texture_projection_current',content_rowid='row_id',tokenize='trigram case_sensitive 1');
            CREATE TRIGGER IF NOT EXISTS texture_current_ai AFTER INSERT ON texture_projection_current BEGIN
                INSERT INTO texture_projection_current_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS texture_current_ad AFTER DELETE ON texture_projection_current BEGIN
                INSERT INTO texture_projection_current_fts(texture_projection_current_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS texture_current_au AFTER UPDATE OF search_fold ON texture_projection_current WHEN old.search_fold IS NOT new.search_fold BEGIN
                INSERT INTO texture_projection_current_fts(texture_projection_current_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold);
                INSERT INTO texture_projection_current_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS texture_rows_dirty_ai AFTER INSERT ON texture_projection_rows BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(new.revision,new.profile,new.identity); END;
            CREATE TRIGGER IF NOT EXISTS texture_rows_dirty_au AFTER UPDATE ON texture_projection_rows BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(old.revision,old.profile,old.identity);
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(new.revision,new.profile,new.identity); END;
            CREATE TRIGGER IF NOT EXISTS texture_rows_dirty_ad AFTER DELETE ON texture_projection_rows BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(old.revision,old.profile,old.identity); END;
            CREATE TRIGGER IF NOT EXISTS texture_alias_dirty_ai AFTER INSERT ON texture_projection_aliases BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(new.revision,new.profile,new.identity); END;
            CREATE TRIGGER IF NOT EXISTS texture_alias_dirty_au AFTER UPDATE ON texture_projection_aliases BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(old.revision,old.profile,old.identity);
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(new.revision,new.profile,new.identity); END;
            CREATE TRIGGER IF NOT EXISTS texture_alias_dirty_ad AFTER DELETE ON texture_projection_aliases BEGIN
                INSERT OR IGNORE INTO texture_projection_current_dirty VALUES(old.revision,old.profile,old.identity); END;
            INSERT OR IGNORE INTO texture_projection_current_dirty
                SELECT DISTINCT revision,profile,identity FROM texture_projection_rows
                WHERE NOT EXISTS(SELECT 1 FROM texture_projection_current_migrations WHERE version=1);
            INSERT OR IGNORE INTO texture_projection_current_migrations VALUES(1);
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_projection_history_migrations(version INTEGER PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS texture_projection_history_dirty(revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,PRIMARY KEY(revision,profile,identity));
            CREATE TABLE IF NOT EXISTS texture_projection_history_index(row_id INTEGER PRIMARY KEY,revision TEXT NOT NULL,profile TEXT NOT NULL,
                identity TEXT NOT NULL,generation INTEGER NOT NULL,next_generation INTEGER,ordinal INTEGER NOT NULL,name_kind INTEGER NOT NULL,kind INTEGER NOT NULL,
                label TEXT NOT NULL,sort_key TEXT NOT NULL,search TEXT NOT NULL,width INTEGER,height INTEGER,format TEXT,context_key TEXT,context_label TEXT,
                has_owner INTEGER NOT NULL,has_conflict INTEGER NOT NULL,has_metadata INTEGER NOT NULL,alias TEXT,name_rank INTEGER NOT NULL,effective_sort TEXT NOT NULL,
                search_fold TEXT NOT NULL,UNIQUE(revision,profile,identity,generation));
            CREATE INDEX IF NOT EXISTS texture_history_named ON texture_projection_history_index(revision,profile,name_rank,effective_sort,ordinal,identity,generation,next_generation);
            CREATE INDEX IF NOT EXISTS texture_history_visible_named ON texture_projection_history_index(revision,profile,name_rank,effective_sort,ordinal,identity,generation,next_generation) WHERE kind!=2;
            CREATE INDEX IF NOT EXISTS texture_history_kind_named ON texture_projection_history_index(revision,profile,kind,name_rank,effective_sort,ordinal,identity,generation,next_generation);
            CREATE INDEX IF NOT EXISTS texture_history_name ON texture_projection_history_index(revision,profile,effective_sort,ordinal,identity,generation,next_generation);
            CREATE INDEX IF NOT EXISTS texture_history_ordinal ON texture_projection_history_index(revision,profile,ordinal,identity,generation,next_generation);
            CREATE INDEX IF NOT EXISTS texture_history_kind ON texture_projection_history_index(revision,profile,kind,generation,next_generation);
            CREATE VIRTUAL TABLE IF NOT EXISTS texture_projection_history_fts USING fts5(search_fold,content='texture_projection_history_index',content_rowid='row_id',tokenize='trigram case_sensitive 1');
            CREATE TRIGGER IF NOT EXISTS texture_history_ai AFTER INSERT ON texture_projection_history_index BEGIN
                INSERT INTO texture_projection_history_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS texture_history_ad AFTER DELETE ON texture_projection_history_index BEGIN
                INSERT INTO texture_projection_history_fts(texture_projection_history_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS texture_history_au AFTER UPDATE OF search_fold ON texture_projection_history_index WHEN old.search_fold IS NOT new.search_fold BEGIN
                INSERT INTO texture_projection_history_fts(texture_projection_history_fts,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold);
                INSERT INTO texture_projection_history_fts(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            INSERT OR IGNORE INTO texture_projection_history_dirty SELECT DISTINCT revision,profile,identity FROM texture_projection_rows
                WHERE NOT EXISTS(SELECT 1 FROM texture_projection_history_migrations WHERE version=1);
            INSERT OR IGNORE INTO texture_projection_history_migrations VALUES(1);
            """;
        command.ExecuteNonQuery();
        foreach (string source in new[] { "rows", "aliases" })
        foreach (string action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string prior = action != "INSERT" ? "INSERT OR IGNORE INTO texture_projection_history_dirty VALUES(old.revision,old.profile,old.identity);" : "";
            string next = action != "DELETE" ? "INSERT OR IGNORE INTO texture_projection_history_dirty VALUES(new.revision,new.profile,new.identity);" : "";
            command.CommandText = $"CREATE TRIGGER IF NOT EXISTS texture_history_{source}_{action.ToLowerInvariant()} AFTER {action} ON texture_projection_{source} BEGIN {prior}{next} END;";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS texture_current_options ON texture_projection_current
                (revision,profile,width,height,format,context_key,context_label,has_owner,identity,generation);
            """;
        using (TextureDiagnostic("TextureBrowser.Store.CurrentOptionsIndexBuild", TextureDiagnosticKey())) command.ExecuteNonQuery();
        EnsureTextureAliasSchema(transaction);
        using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('texture_projection_heads') WHERE name='minimum_generation'";
            if (Convert.ToInt64(columns.ExecuteScalar()) == 0)
            {
                columns.CommandText = "ALTER TABLE texture_projection_heads ADD COLUMN minimum_generation INTEGER NOT NULL DEFAULT 1";
                columns.ExecuteNonQuery();
            }
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('texture_projection_catalogs') WHERE name='label'";
            if (Convert.ToInt64(columns.ExecuteScalar()) == 0)
            {
                columns.CommandText = "ALTER TABLE texture_projection_catalogs ADD COLUMN label TEXT";
                columns.ExecuteNonQuery();
            }
        }
        command.CommandText = "CREATE INDEX IF NOT EXISTS texture_history_options ON texture_projection_history_index " +
            "(revision,profile,width,height,format,context_key,context_label,has_owner,identity,generation,next_generation,data_generation)";
        using (TextureDiagnostic("TextureBrowser.Store.HistoricalOptionsIndexBuild", TextureDiagnosticKey())) command.ExecuteNonQuery();
        EnsureTextureSummarySchema(transaction);
        EnsureTextureRebuildSchema(transaction);
        transaction.Commit(); textureSchemaReady = true;
    }

    public long InitializeTextureProjections(string profile, IReadOnlyList<TextureDiscoveryEntry> baseline, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile); ArgumentNullException.ThrowIfNull(baseline);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema();
            using var read = TextureCommand("SELECT generation,baseline_count FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile);
            long? existing = null;
            long previousGeneration = 0;
            using (var rows = read.ExecuteReader())
                if (rows.Read())
                {
                    previousGeneration = rows.GetInt64(0);
                    if (rows.GetInt32(1) == baseline.Count) existing = previousGeneration;
                }
            if (existing is { } published)
            {
                using var prepare = connection.BeginTransaction();
                RefreshTextureHistory(profile, prepare);
                token.ThrowIfCancellationRequested();
                prepare.Commit();
                return published;
            }
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "texture_projection_rows", "texture_projection_links", "texture_projection_evidence", "texture_projection_name_proofs", "texture_projection_owners", "texture_projection_catalog_members", "texture_projection_catalogs", "texture_projection_current_roles", "texture_projection_current_role_rows", "texture_projection_current_role_state", "texture_browse_summary_state", "texture_browse_summary_members", "texture_browse_summary_values", "texture_browse_summary_dirty", "texture_projection_owner_rebuilds" })
            {
                using var clear = TextureCommand($"DELETE FROM {table} WHERE revision=$revision AND profile=$profile", profile, transaction); clear.ExecuteNonQuery();
            }
            long baselineGeneration = checked(previousGeneration + 1);
            foreach (var row in baseline) { token.ThrowIfCancellationRequested(); WriteTextureRow(profile, baselineGeneration, row, false, transaction); }
            RefreshTextureHistory(profile, transaction);
            using (var ready = TextureCommand("INSERT INTO texture_projection_current_role_state VALUES($revision,$profile,-1,'',1)", profile, transaction)) ready.ExecuteNonQuery();
            using var head = TextureCommand("INSERT OR REPLACE INTO texture_projection_heads(revision,profile,generation,baseline_count,minimum_generation) VALUES($revision,$profile,$generation,$count,$generation)", profile, transaction);
            head.Parameters.AddWithValue("$generation", baselineGeneration);
            head.Parameters.AddWithValue("$count", baseline.Count); head.ExecuteNonQuery();
            token.ThrowIfCancellationRequested(); transaction.Commit(); return baselineGeneration;
        });
    }

    public long CaptureTextureProjectionGeneration(string profile, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var command = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile);
            return command.ExecuteScalar() is long value ? value : 0;
        });
    }

    /// <summary>Publishes verified direct RAWL/RAW catalog membership independently of owner texture metadata.</summary>
    public long CommitTextureProjectionCatalog(string profile, SourceRevision sourceRevision, ResourceIdentity catalog,
        string tableSha256, IReadOnlyList<ResourceIdentity> members, CancellationToken token = default)
        => CommitTextureProjectionCatalogCore(profile, sourceRevision, catalog, tableSha256, members, "Catalog " + catalog.Ordinal, token);

    private long CommitTextureProjectionCatalogCore(string profile, SourceRevision sourceRevision, ResourceIdentity catalog,
        string tableSha256, IReadOnlyList<ResourceIdentity> members, string label, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (revision != SourceIdentity.DerivedCacheKey(sourceRevision.IndexContentId, sourceRevision.PhysicalSourceStamp) ||
            catalog.IndexContentId != sourceRevision.IndexContentId || catalog.MemberPath is not null ||
            tableSha256.Length != 64 || !tableSha256.All(Uri.IsHexDigit) || members.Count > LocalizationReferenceLimit)
            throw new InvalidDataException("TextureProjectionCatalogSourceMismatch");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            if (member.IndexContentId != sourceRevision.IndexContentId || member.MemberPath is not null)
                throw new InvalidDataException("TextureProjectionCatalogSourceMismatch");
            keys.Add(member.Key);
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Profile = "direct-texture-members-v1", Catalog = catalog.Key, Table = tableSha256, Members = keys })))).ToLowerInvariant();
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile, transaction);
            long current = Convert.ToInt64(head.ExecuteScalar() ?? throw new InvalidOperationException("Texture projection baseline is unavailable."));
            using (var read = TextureCommand("SELECT table_sha256,members_sha256,member_count,label FROM texture_projection_catalogs WHERE revision=$revision AND profile=$profile AND catalog=$catalog ORDER BY generation DESC LIMIT 1", profile, transaction))
            {
                read.Parameters.AddWithValue("$catalog", catalog.Key); using var row = read.ExecuteReader();
                if (row.Read() && row.GetString(0) == tableSha256 && row.GetString(1) == fingerprint && row.GetInt32(2) == keys.Count &&
                    !row.IsDBNull(3) && row.GetString(3) == label) return current;
            }
            long generation = checked(current + 1);
            using (var write = TextureCommand("INSERT INTO texture_projection_catalogs(revision,profile,catalog,generation,table_sha256,members_sha256,member_count,label) VALUES($revision,$profile,$catalog,$generation,$table,$members,$count,$label)", profile, transaction))
            {
                write.Parameters.AddWithValue("$catalog", catalog.Key); write.Parameters.AddWithValue("$generation", generation);
                write.Parameters.AddWithValue("$table", tableSha256); write.Parameters.AddWithValue("$members", fingerprint);
                write.Parameters.AddWithValue("$label", label);
                write.Parameters.AddWithValue("$count", keys.Count); write.ExecuteNonQuery();
            }
            foreach (string key in keys)
            {
                token.ThrowIfCancellationRequested();
                using var write = TextureCommand("INSERT INTO texture_projection_catalog_members VALUES($revision,$profile,$catalog,$generation,$identity)", profile, transaction);
                write.Parameters.AddWithValue("$catalog", catalog.Key); write.Parameters.AddWithValue("$generation", generation);
                write.Parameters.AddWithValue("$identity", key); write.ExecuteNonQuery();
            }
            using (var update = TextureCommand("UPDATE texture_projection_heads SET generation=$generation WHERE revision=$revision AND profile=$profile", profile, transaction))
            { update.Parameters.AddWithValue("$generation", generation); update.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return generation;
        });
    }

    private const int LocalizationReferenceLimit = 4096;
    private const string TextureDirectMembership = "m.revision=p.revision AND m.profile=p.profile AND m.identity=p.identity AND m.generation=" +
        "(SELECT MAX(c.generation) FROM texture_projection_catalogs c WHERE c.revision=m.revision AND c.profile=m.profile AND c.catalog=m.catalog AND c.generation<=$generation)";
    private static string TexturePageProjection => "p.thin,a.alias,(SELECT json_group_array(m.catalog) FROM texture_projection_catalog_members m WHERE " + TextureDirectMembership + ")";

    public IReadOnlyList<TextureProjectionOwnerCheckpoint> LoadTextureProjectionOwners(string profile, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var command = TextureCommand("SELECT payload FROM texture_projection_owners WHERE revision=$revision AND profile=$profile ORDER BY identity", profile);
            var result = new List<TextureProjectionOwnerCheckpoint>(); using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var json = reader.GetString(0);
                if (json.Length > 16 * 1024) throw new InvalidDataException("TextureProjectionCheckpointLimit");
                result.Add(JsonSerializer.Deserialize<TextureProjectionOwnerCheckpoint>(json) ?? throw new InvalidDataException("TextureProjectionCheckpointInvalid"));
            }
            return (IReadOnlyList<TextureProjectionOwnerCheckpoint>)result;
        });
    }

    public void CompleteTextureProjectionOwner(string profile, ResourceIdentity identity, string membershipKey, CancellationToken token = default)
    {
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            using var read = TextureCommand("SELECT payload FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction);
            read.Parameters.AddWithValue("$identity", identity.Key);
            if (read.ExecuteScalar() is not string json) return false;
            var checkpoint = JsonSerializer.Deserialize<TextureProjectionOwnerCheckpoint>(json);
            if (checkpoint is null || checkpoint.MembershipKey != membershipKey) return false;
            if (!checkpoint.MembershipComplete)
            {
                using var write = TextureCommand("UPDATE texture_projection_owners SET payload=$payload WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction);
                write.Parameters.AddWithValue("$identity", identity.Key); write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(checkpoint with { MembershipComplete = true })); write.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
        });
    }

    public long CommitTextureProjectionOwner(string profile, TextureProjectionOwnerCheckpoint checkpoint,
        IReadOnlyList<TextureDiscoveryEntry> rows, CancellationToken token = default, long? completedRebuildRequest = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint); ArgumentNullException.ThrowIfNull(rows);
        if (completedRebuildRequest is < 1) throw new ArgumentOutOfRangeException(nameof(completedRebuildRequest));
        if (revision != SourceIdentity.DerivedCacheKey(checkpoint.Revision.IndexContentId, checkpoint.Revision.PhysicalSourceStamp) ||
            checkpoint.Identity.IndexContentId != checkpoint.Revision.IndexContentId || checkpoint.Identity.MemberPath is not null ||
            checkpoint.MetadataExtent.Length < 1 || checkpoint.MetadataExtent.PayloadSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
            throw new InvalidDataException("TextureProjectionSourceMismatch");
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.OwnerPublish", diagnosticKey);
        using var waitDiagnostic = TextureDiagnostic("TextureBrowser.Store.OwnerPublishWait", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            waitDiagnostic?.Dispose();
            using var holdDiagnostic = TextureDiagnostic("TextureBrowser.Store.OwnerPublishHold", diagnosticKey);
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.OwnerRows", diagnosticKey, new Dictionary<string, long> { ["Rows"] = rows.Count });
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            using var transactionDiagnostic = TextureDiagnostic("TextureBrowser.Store.OwnerTransaction", diagnosticKey);
            bool historyWasClean = TextureHistoryIsClean(profile, transaction);
            using var current = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile, transaction);
            long headGeneration = Convert.ToInt64(current.ExecuteScalar() ?? throw new InvalidOperationException("Texture projection baseline is unavailable."));
            if (completedRebuildRequest is null && TextureOwnerPublicationUnchanged(profile, checkpoint, rows, headGeneration, transaction, token))
            {
                token.ThrowIfCancellationRequested(); transaction.Commit();
                AssetLoadDiagnostics.Counters("TextureBrowser.Store.OwnerUnchanged", diagnosticKey,
                    new Dictionary<string, long> { ["Rows"] = rows.Count });
                return headGeneration;
            }
            long generation = checked(headGeneration + 1);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.Identity.IndexContentId != checkpoint.Revision.IndexContentId) throw new InvalidDataException("TextureProjectionSourceMismatch");
                WriteTextureRow(profile, generation, row, true, transaction);
                using var provenance = TextureCommand("INSERT OR IGNORE INTO texture_projection_links VALUES($revision,$profile,$identity,$generation,4,$owner,'','',1)", profile, transaction);
                provenance.Parameters.AddWithValue("$identity", row.Identity.Key); provenance.Parameters.AddWithValue("$generation", generation);
                provenance.Parameters.AddWithValue("$owner", checkpoint.Identity.Key); provenance.ExecuteNonQuery();
            }
            using var owner = TextureCommand("INSERT OR REPLACE INTO texture_projection_owners VALUES($revision,$profile,$identity,$generation,$payload)", profile, transaction);
            owner.Parameters.AddWithValue("$identity", checkpoint.Identity.Key); owner.Parameters.AddWithValue("$generation", generation);
            owner.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(checkpoint)); owner.ExecuteNonQuery();
            using var head = TextureCommand("UPDATE texture_projection_heads SET generation=$generation WHERE revision=$revision AND profile=$profile", profile, transaction);
            head.Parameters.AddWithValue("$generation", generation); head.ExecuteNonQuery();
            RefreshTextureHistoryAfterAppend(profile, headGeneration, generation, historyWasClean, transaction);
            token.ThrowIfCancellationRequested();
            CompleteTextureOwnerRebuild(profile, checkpoint.Identity, completedRebuildRequest, transaction);
            using (TextureDiagnostic("TextureBrowser.Store.OwnerCommit", diagnosticKey)) transaction.Commit();
            return generation;
        });
    }

    private void WriteTextureRow(string profile, long generation, TextureDiscoveryEntry entry, bool evidence, SqliteTransaction transaction)
    {
        var thin = ThinTextureProjection(entry);
        byte[]? encoded = evidence ? JsonSerializer.SerializeToUtf8Bytes(entry with { Alias = null }) : null;
        if (encoded?.Length > MaximumTextureEvidenceBytes) throw new InvalidDataException("TextureProjectionEvidenceLimit");
        using var command = TextureCommand("INSERT OR REPLACE INTO texture_projection_rows VALUES($revision,$profile,$identity,$generation,$ordinal,$nameKind,$kind,$label,$sort,$search,$width,$height,$format,$context,$contextLabel,$owner,$conflict,$metadata,$thin)", profile, transaction);
        Add("$identity", entry.Identity.Key); Add("$generation", generation);
        foreach (var value in TextureProjectionScalars(entry)) Add(value.Parameter, value.Value);
        Add("$thin", JsonSerializer.Serialize(thin)); command.ExecuteNonQuery();
        WriteCurrentTextureRoles(profile, entry.Identity.Key, generation,
            entry.Owners.Where(owner => owner.Verified && !string.IsNullOrEmpty(owner.Role)).Select(owner => owner.Role!).Distinct(StringComparer.Ordinal), transaction);
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        foreach (var item in TextureProjectionLinks(entry)) Link(item.Relation, item.Key, item.Role, item.Label, item.Verified);
        void Link(int relation, string key, string role, string label, bool verified)
        {
            using var write = TextureCommand("INSERT OR IGNORE INTO texture_projection_links VALUES($revision,$profile,$identity,$generation,$relation,$key,$role,$label,$verified)", profile, transaction);
            write.Parameters.AddWithValue("$identity", entry.Identity.Key); write.Parameters.AddWithValue("$generation", generation);
            write.Parameters.AddWithValue("$relation", relation); write.Parameters.AddWithValue("$key", key); write.Parameters.AddWithValue("$role", role);
            write.Parameters.AddWithValue("$label", label); write.Parameters.AddWithValue("$verified", verified ? 1 : 0); write.ExecuteNonQuery();
        }
        if (encoded is not null)
        {
            using var write = TextureCommand("INSERT OR REPLACE INTO texture_projection_evidence VALUES($revision,$profile,$identity,$generation,$payload,$sha)", profile, transaction);
            write.Parameters.AddWithValue("$identity", entry.Identity.Key); write.Parameters.AddWithValue("$generation", generation); write.Parameters.AddWithValue("$payload", encoded);
            string evidenceSha = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
            write.Parameters.AddWithValue("$sha", evidenceSha); write.ExecuteNonQuery();
            byte[] proof = EncodeTextureProjectionNameProof(entry, thin);
            using var name = TextureCommand("INSERT OR REPLACE INTO texture_projection_name_proofs VALUES($revision,$profile,$identity,$generation,$payload,$sha,$evidence)", profile, transaction);
            name.Parameters.AddWithValue("$identity", entry.Identity.Key); name.Parameters.AddWithValue("$generation", generation); name.Parameters.AddWithValue("$payload", proof);
            name.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant()); name.Parameters.AddWithValue("$evidence", evidenceSha); name.ExecuteNonQuery();
        }
    }

    private void WriteCurrentTextureRoles(string profile, string identity, long generation, IEnumerable<string> roles, SqliteTransaction transaction)
    {
        using (var clear = TextureCommand("DELETE FROM texture_projection_current_roles WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction))
        { clear.Parameters.AddWithValue("$identity", identity); clear.ExecuteNonQuery(); }
        foreach (string role in roles.Distinct(StringComparer.Ordinal))
        {
            using var write = TextureCommand("INSERT INTO texture_projection_current_roles VALUES($revision,$profile,$identity,$generation,$role)", profile, transaction);
            write.Parameters.AddWithValue("$identity", identity); write.Parameters.AddWithValue("$generation", generation);
            write.Parameters.AddWithValue("$role", role); write.ExecuteNonQuery();
        }
        using var receipt = TextureCommand("INSERT OR REPLACE INTO texture_projection_current_role_rows VALUES($revision,$profile,$identity,$generation)", profile, transaction);
        receipt.Parameters.AddWithValue("$identity", identity); receipt.Parameters.AddWithValue("$generation", generation); receipt.ExecuteNonQuery();
    }

    public const int CurrentTextureRoleBatchSize = 128;

    /// <summary>Prepares current role values in bounded transactions without retaining the writer gate between batches.</summary>
    public Task PrepareCurrentTextureRolesAsync(string profile, long generation, CancellationToken token = default)
        => Task.Run(async () =>
        {
            while (!PrepareCurrentTextureRoleBatch(profile, generation, token))
            { token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }, token);

    private bool PrepareCurrentTextureRoleBatch(string profile, long generation, CancellationToken token)
    {
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.RoleBackfillBatch", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile, transaction);
            if (head.ExecuteScalar() is not long current || current != generation)
                throw new ArchiveException("TextureProjectionGenerationChanged", "Texture indexing advanced. Refresh the filter options.");
            _ = UseCurrentTextureProjection(profile, generation, transaction);
            long cursorOrdinal = -1; string cursorIdentity = "";
            using (var state = TextureCommand("SELECT cursor_ordinal,cursor_identity,complete FROM texture_projection_current_role_state WHERE revision=$revision AND profile=$profile", profile, transaction))
            using (var reader = state.ExecuteReader())
                if (reader.Read())
                {
                    if (reader.GetInt32(2) != 0) { reader.Close(); token.ThrowIfCancellationRequested(); transaction.Commit(); return true; }
                    cursorOrdinal = reader.GetInt64(0); cursorIdentity = reader.GetString(1);
                }
            var batch = new List<(long Ordinal, string Identity, long Generation, string Thin)>();
            using (var read = CurrentTextureRoleBackfillCommand(profile, cursorOrdinal, cursorIdentity, transaction))
            {
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested(); string thin = reader.GetString(3);
                    if (thin.Length > MaximumTextureNameProofBytes) throw new InvalidDataException("TextureRoleProjectionInvalid");
                    batch.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), thin));
                }
            }
            foreach (var row in batch)
            {
                token.ThrowIfCancellationRequested();
                var entry = JsonSerializer.Deserialize<TextureDiscoveryEntry>(row.Thin) ?? throw new InvalidDataException("TextureRoleProjectionInvalid");
                if (entry.Identity.Key != row.Identity || entry.Owners is null || entry.Owners.Any(owner => owner is null))
                    throw new InvalidDataException("TextureRoleProjectionInvalid");
                using var matches = TextureCommand("SELECT 1 FROM texture_projection_current WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation=$generation", profile, transaction);
                matches.Parameters.AddWithValue("$identity", row.Identity); matches.Parameters.AddWithValue("$generation", row.Generation);
                if (matches.ExecuteScalar() is null) throw new ArchiveException("TextureProjectionGenerationChanged", "Texture indexing advanced. Refresh the filter options.");
                WriteCurrentTextureRoles(profile, row.Identity, row.Generation,
                    entry.Owners.Where(owner => owner.Verified && !string.IsNullOrEmpty(owner.Role)).Select(owner => owner.Role!), transaction);
                cursorOrdinal = row.Ordinal; cursorIdentity = row.Identity;
            }
            using (var state = TextureCommand("INSERT OR REPLACE INTO texture_projection_current_role_state VALUES($revision,$profile,$cursor,$identity,$complete)", profile, transaction))
            {
                state.Parameters.AddWithValue("$cursor", cursorOrdinal); state.Parameters.AddWithValue("$identity", cursorIdentity);
                state.Parameters.AddWithValue("$complete", batch.Count == 0 ? 1 : 0); state.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested(); transaction.Commit();
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.RoleBackfillRows", diagnosticKey,
                new Dictionary<string, long> { ["Rows"] = batch.Count, ["Cursor"] = cursorOrdinal, ["Complete"] = batch.Count == 0 ? 1 : 0 });
            return batch.Count == 0;
        });
    }

    private SqliteCommand CurrentTextureRoleBackfillCommand(string profile, long cursorOrdinal, string cursorIdentity, SqliteTransaction? transaction = null)
    {
        var read = TextureCommand("SELECT p.ordinal,p.identity,p.generation,p.thin FROM texture_projection_current p INDEXED BY texture_current_ordinal " +
            "LEFT JOIN texture_projection_current_role_rows r ON r.revision=p.revision AND r.profile=p.profile AND r.identity=p.identity AND r.generation=p.generation " +
            "WHERE p.revision=$revision AND p.profile=$profile AND (p.ordinal,p.identity)>($cursor,$identity) " +
            "AND r.identity IS NULL ORDER BY p.ordinal,p.identity LIMIT $limit", profile, transaction);
        read.Parameters.AddWithValue("$cursor", cursorOrdinal); read.Parameters.AddWithValue("$identity", cursorIdentity);
        read.Parameters.AddWithValue("$limit", CurrentTextureRoleBatchSize); return read;
    }

    private const string TextureLatest = "WITH latest AS (SELECT p.* FROM texture_projection_rows p WHERE p.revision=$revision AND p.profile=$profile AND p.generation=(SELECT MAX(v.generation) FROM texture_projection_rows v WHERE v.revision=p.revision AND v.profile=p.profile AND v.identity=p.identity AND v.generation<=$generation)) ";
    private const string TextureCurrentLatest = "WITH latest AS (SELECT p.* FROM texture_projection_current p WHERE p.revision=$revision AND p.profile=$profile) ";
    private const string TextureHistoricalLatest = "WITH latest AS (SELECT p.* FROM texture_projection_history_index p WHERE p.revision=$revision AND p.profile=$profile AND p.generation<=$generation AND (p.next_generation IS NULL OR p.next_generation>$generation)) ";

    /// <summary>Resets only invalid requested rows. Valid supporting rows, checkpoints and aliases are retained.</summary>
    public TextureProjectionQuarantine QuarantineTextureProjectionEvidence(string profile,
        IReadOnlyDictionary<ResourceIdentity, TextureDiscoveryEntry> baseline,
        IReadOnlyList<ResourceIdentity> identities, Func<TextureEvidence, bool>? validEvidence = null,
        CancellationToken token = default, bool damagedDetailEvidence = false, long? validationGeneration = null,
        Func<ResourceIdentity, bool>? canRebuildOwner = null)
    {
        if (identities.Count > 1000) throw new ArgumentOutOfRangeException(nameof(identities));
        if (identities.Count == 0) return new([], []);
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Quarantine", diagnosticKey);
        using var waitDiagnostic = TextureDiagnostic("TextureBrowser.Store.QuarantineWait", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            waitDiagnostic?.Dispose();
            using var holdDiagnostic = TextureDiagnostic("TextureBrowser.Store.QuarantineHold", diagnosticKey);
            EnsureTextureSchema(); long head = CaptureTextureProjectionGeneration(profile, token);
            if (validationGeneration is < 1 || validationGeneration > head) throw new ArgumentOutOfRangeException(nameof(validationGeneration));
            var invalid = InvalidTextureProjectionRows(profile, validationGeneration ?? head, identities, validEvidence, damagedDetailEvidence, token);
            if (invalid.Count == 0) return new TextureProjectionQuarantine([], []);
            var targets = identities.Distinct().Where(identity => invalid.Contains(identity.Key)).ToArray();
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.QuarantineRows", diagnosticKey, new Dictionary<string, long>
            { ["Requested"] = identities.Count, ["Invalid"] = targets.Length });
            if (targets.Any(identity => !baseline.ContainsKey(identity))) throw new InvalidDataException("TextureProjectionBaselineMissing");
            foreach (var identity in targets)
            {
                var entry = baseline[identity];
                if (entry.Identity != identity || entry.NameKind != TextureNameKind.Fallback || entry.MetadataIdentities.Count != 0 ||
                    entry.MetadataContexts.Count != 0 || entry.NameCandidates.Count != 0 || entry.Owners.Count != 0 || entry.Related.Count != 0 ||
                    entry.Width is not null || entry.Height is not null || entry.Format is not null || entry.ResourceName is not null || entry.ResourcePath is not null)
                    throw new InvalidDataException("TextureProjectionBaselineInvalid");
            }
            var owners = new HashSet<ResourceIdentity>();
            using var transaction = connection.BeginTransaction();
            foreach (var identity in targets)
            {
                token.ThrowIfCancellationRequested();
                using var version = TextureCommand("SELECT MAX(generation) FROM texture_projection_rows WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation<=$generation", profile, transaction);
                version.Parameters.AddWithValue("$identity", identity.Key); version.Parameters.AddWithValue("$generation", validationGeneration ?? head);
                long damagedGeneration = Convert.ToInt64(version.ExecuteScalar());
                // Read publisher provenance independently; related resources must not hide the source owner.
                foreach (string relations in new[] { "relation=4", "relation IN(0,1,3)" })
                using (var links = TextureCommand("SELECT DISTINCT link_key FROM texture_projection_links WHERE revision=$revision AND profile=$profile " +
                    "AND identity=$identity AND " + relations + " AND generation=$generation", profile, transaction))
                {
                    links.Parameters.AddWithValue("$identity", identity.Key); links.Parameters.AddWithValue("$generation", damagedGeneration);
                    using var reader = links.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested(); var parts = reader.GetString(0).Split(':');
                        if (parts.Length != 3 || parts[0] != identity.IndexContentId || !long.TryParse(parts[1],
                            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ordinal)) continue;
                        var owner = new ResourceIdentity(identity.IndexContentId, ordinal);
                        if (!owners.Contains(owner) && CanQueueTextureOwnerRebuild(profile, owner, canRebuildOwner, transaction)) owners.Add(owner);
                    }
                }
                // This identity alone is damaged; do not traverse its connected owner/texture graph.
                foreach (string table in new[] { "texture_projection_rows", "texture_projection_links", "texture_projection_evidence", "texture_projection_name_proofs" })
                {
                    using var remove = TextureCommand($"DELETE FROM {table} WHERE revision=$revision AND profile=$profile AND identity=$identity AND generation=$generation", profile, transaction);
                    remove.Parameters.AddWithValue("$identity", identity.Key); remove.Parameters.AddWithValue("$generation", damagedGeneration);
                    using (TextureDiagnostic("TextureBrowser.Store.QuarantineDelete", diagnosticKey)) remove.ExecuteNonQuery();
                }
                WriteTextureRow(profile, checked(head + 1), baseline[identity] with { Alias = null }, false, transaction);
            }
            QueueTextureOwnerRebuilds(profile, owners, transaction, token);
            RefreshTextureHistory(profile, transaction);
            using (var update = TextureCommand("UPDATE texture_projection_heads SET generation=$generation,minimum_generation=$generation WHERE revision=$revision AND profile=$profile", profile, transaction))
            { update.Parameters.AddWithValue("$generation", checked(head + 1)); update.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested();
            using (TextureDiagnostic("TextureBrowser.Store.QuarantineCommit", diagnosticKey)) transaction.Commit();
            return new TextureProjectionQuarantine(targets, owners.OrderBy(identity => identity.Ordinal).ToArray());
        });
    }

    private HashSet<string> InvalidTextureProjectionRows(string profile, long generation,
        IReadOnlyList<ResourceIdentity> identities, Func<TextureEvidence, bool>? validEvidence,
        bool damagedDetailEvidence, CancellationToken token, SqliteConnection? readConnection = null, SqliteTransaction? transaction = null)
    {
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Proof", diagnosticKey);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        string sql = TextureLatest + "SELECT p.identity,length(n.payload)," +
            "CASE WHEN length(n.payload)<=$maximum THEN n.payload ELSE NULL END,n.sha256,n.evidence_sha256,e.sha256,p.thin,p.label,p.name_kind,p.search FROM latest p " +
            "LEFT JOIN texture_projection_name_proofs n ON n.revision=p.revision AND n.profile=p.profile AND n.identity=p.identity AND n.generation=p.generation " +
            "LEFT JOIN texture_projection_evidence e ON e.revision=p.revision AND e.profile=p.profile AND e.identity=p.identity AND e.generation=p.generation WHERE p.identity IN(" +
            string.Join(',', identities.Select((_, index) => "$identity" + index)) + ") AND (e.identity IS NOT NULL OR p.has_metadata=1 OR p.has_owner=1 OR p.name_kind!=4)";
        using var read = readConnection is null ? TextureCommand(sql, profile, transaction) :
            ReadTextureCommand(readConnection, transaction!, sql, revision, profile);
        read.Parameters.AddWithValue("$generation", generation); read.Parameters.AddWithValue("$maximum", MaximumTextureNameProofBytes);
        for (int i = 0; i < identities.Count; i++) read.Parameters.AddWithValue("$identity" + i, identities[i].Key);
        SqliteDataReader reader;
        using (TextureDiagnostic("TextureBrowser.Store.ProofSql", diagnosticKey)) reader = read.ExecuteReader();
        using var readerLease = reader;
        using var validationDiagnostic = TextureDiagnostic("TextureBrowser.Store.ProofValidation", diagnosticKey);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); string identity = reader.GetString(0);
            if (damagedDetailEvidence || reader.IsDBNull(1) || reader.GetInt64(1) > MaximumTextureNameProofBytes || reader.IsDBNull(2) ||
                reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) || reader.GetString(4) != reader.GetString(5))
            { invalid.Add(identity); continue; }
            var payload = (byte[])reader.GetValue(2);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(payload)), reader.GetString(3)) ||
                !ValidTextureNameProof(payload, reader.GetString(6), identity, reader.GetString(7), reader.GetInt32(8), reader.GetString(9), validEvidence)) invalid.Add(identity);
        }
        return invalid;
    }

    /// <summary>Repairs the complete supporting component. Use outside foreground browsing.</summary>
    public IReadOnlyList<ResourceIdentity> RepairTextureProjectionEvidence(string profile,
        IReadOnlyDictionary<ResourceIdentity, TextureDiscoveryEntry> baseline,
        Func<TextureEvidence, bool>? validEvidence = null, CancellationToken token = default,
        IReadOnlyList<ResourceIdentity>? identities = null, bool damagedDetailEvidence = false)
    {
        identities ??= baseline.Keys.Take(1001).ToArray();
        if (identities.Count > 1000) throw new ArgumentOutOfRangeException(nameof(identities));
        if (identities.Count == 0) return [];
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); long generation = CaptureTextureProjectionGeneration(profile, token);
            var invalid = new HashSet<string>(StringComparer.Ordinal);
            using (var read = TextureCommand(TextureLatest + "SELECT p.identity,length(n.payload)," +
                "CASE WHEN length(n.payload)<=$maximum THEN n.payload ELSE NULL END,n.sha256,n.evidence_sha256,e.sha256,p.thin,p.label,p.name_kind,p.search FROM latest p " +
                "LEFT JOIN texture_projection_name_proofs n ON n.revision=p.revision AND n.profile=p.profile AND n.identity=p.identity AND n.generation=p.generation " +
                "LEFT JOIN texture_projection_evidence e ON e.revision=p.revision AND e.profile=p.profile AND e.identity=p.identity AND e.generation=p.generation WHERE p.identity IN(" +
                string.Join(',', identities.Select((_, index) => "$identity" + index)) + ") AND (e.identity IS NOT NULL OR p.has_metadata=1 OR p.has_owner=1 OR p.name_kind!=4)", profile))
            {
                read.Parameters.AddWithValue("$generation", generation); read.Parameters.AddWithValue("$maximum", MaximumTextureNameProofBytes);
                for (int i = 0; i < identities.Count; i++) read.Parameters.AddWithValue("$identity" + i, identities[i].Key);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested(); string identity = reader.GetString(0);
                    if (damagedDetailEvidence || reader.IsDBNull(1) || reader.GetInt64(1) > MaximumTextureNameProofBytes || reader.IsDBNull(2) ||
                        reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) || reader.GetString(4) != reader.GetString(5))
                    { invalid.Add(identity); continue; }
                    var payload = (byte[])reader.GetValue(2);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(payload)), reader.GetString(3)) ||
                        !ValidTextureNameProof(payload, reader.GetString(6), identity, reader.GetString(7), reader.GetInt32(8), reader.GetString(9), validEvidence)) invalid.Add(identity);
                }
            }
            if (invalid.Count == 0) return (IReadOnlyList<ResourceIdentity>)Array.Empty<ResourceIdentity>();
            using var transaction = connection.BeginTransaction();
            using (var tables = connection.CreateCommand())
            {
                tables.Transaction = transaction;
                tables.CommandText = "CREATE TEMP TABLE IF NOT EXISTS texture_invalid_rows(identity TEXT PRIMARY KEY);" +
                    "CREATE TEMP TABLE IF NOT EXISTS texture_invalid_owners(identity TEXT PRIMARY KEY,ordinal INTEGER NOT NULL);" +
                    "DELETE FROM texture_invalid_rows;DELETE FROM texture_invalid_owners;"; tables.ExecuteNonQuery();
            }
            foreach (string identity in invalid)
            {
                using var add = connection.CreateCommand(); add.Transaction = transaction;
                add.CommandText = "INSERT INTO texture_invalid_rows VALUES($identity)"; add.Parameters.AddWithValue("$identity", identity); add.ExecuteNonQuery();
            }
            // A shared texture can carry names from multiple source owners. Rebuild the complete connected support set.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var owners = TextureCommand(TextureLatest + "INSERT OR IGNORE INTO texture_invalid_owners " +
                    "SELECT DISTINCT o.identity,deviant_ordinal(o.identity) FROM latest p JOIN texture_invalid_rows b ON b.identity=p.identity " +
                    "JOIN texture_projection_links l ON l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=p.generation " +
                    "JOIN texture_projection_owners o ON o.revision=p.revision AND o.profile=p.profile AND deviant_ordinal(o.identity)=deviant_ordinal(l.link_key) WHERE l.relation IN(0,1,3,4)", profile, transaction);
                owners.Parameters.AddWithValue("$generation", generation); int ownerChanges = owners.ExecuteNonQuery();
                using var rows = TextureCommand(TextureLatest + "INSERT OR IGNORE INTO texture_invalid_rows SELECT DISTINCT p.identity FROM latest p " +
                    "JOIN texture_projection_links l ON l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=p.generation " +
                    "JOIN texture_invalid_owners o ON o.ordinal=deviant_ordinal(l.link_key) WHERE l.relation IN(0,1,3,4)", profile, transaction);
                rows.Parameters.AddWithValue("$generation", generation); int rowChanges = rows.ExecuteNonQuery();
                if (ownerChanges == 0 && rowChanges == 0) break;
            }
            var rebuild = new List<ResourceIdentity>();
            using (var owners = TextureCommand("SELECT payload FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity IN(SELECT identity FROM texture_invalid_owners)", profile, transaction))
            using (var reader = owners.ExecuteReader())
                while (reader.Read()) { token.ThrowIfCancellationRequested(); rebuild.Add(JsonSerializer.Deserialize<TextureProjectionOwnerCheckpoint>(reader.GetString(0))!.Identity); }
            var affected = new HashSet<string>(StringComparer.Ordinal);
            using (var rows = connection.CreateCommand())
            {
                rows.Transaction = transaction; rows.CommandText = "SELECT identity FROM texture_invalid_rows";
                using var reader = rows.ExecuteReader(); while (reader.Read()) affected.Add(reader.GetString(0));
            }
            foreach (string table in new[] { "texture_projection_rows", "texture_projection_links", "texture_projection_evidence", "texture_projection_name_proofs" })
            {
                using var remove = TextureCommand($"DELETE FROM {table} WHERE revision=$revision AND profile=$profile AND identity IN(SELECT identity FROM texture_invalid_rows)", profile, transaction);
                remove.ExecuteNonQuery();
            }
            using (var owners = TextureCommand("DELETE FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity IN(SELECT identity FROM texture_invalid_owners)", profile, transaction)) owners.ExecuteNonQuery();
            long repairedGeneration = checked(generation + 1);
            foreach (var entry in baseline.Values.Where(entry => affected.Contains(entry.Identity.Key)))
            { token.ThrowIfCancellationRequested(); WriteTextureRow(profile, repairedGeneration, entry, false, transaction); }
            RefreshTextureHistory(profile, transaction);
            using (var head = TextureCommand("UPDATE texture_projection_heads SET generation=$generation,minimum_generation=$generation WHERE revision=$revision AND profile=$profile", profile, transaction))
            { head.Parameters.AddWithValue("$generation", repairedGeneration); head.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return (IReadOnlyList<ResourceIdentity>)rebuild;
        });
    }

    private static bool ValidTextureNameProof(byte[] payload, string thinJson, string identity, string label, int nameKind,
        string search, Func<TextureEvidence, bool>? validEvidence)
    {
        try
        {
            if (thinJson.Length > MaximumTextureNameProofBytes) return false;
            var thin = JsonSerializer.Deserialize<TextureDiscoveryEntry>(thinJson);
            var proof = JsonSerializer.Deserialize<TextureNameProof>(payload);
            if (thin is null || proof is null || proof.Sources.Count > 128 || thin.Identity.Key != identity || thin.Label != label ||
                (int)thin.NameKind != nameKind || thin.SearchText != search) return false;
            var expected = proof.Thin;
            if (expected.Identity != thin.Identity || expected.Label != thin.Label || expected.NameKind != thin.NameKind ||
                expected.ResourceName != thin.ResourceName || expected.ResourcePath != thin.ResourcePath || expected.SearchText != thin.SearchText ||
                expected.ExportStem != thin.ExportStem || expected.Width != thin.Width || expected.Height != thin.Height || expected.Format != thin.Format ||
                expected.Kind != thin.Kind || expected.ContextKey != thin.ContextKey || expected.ContextLabel != thin.ContextLabel ||
                !expected.MetadataIdentities.SequenceEqual(thin.MetadataIdentities) ||
                !expected.CatalogKeys.SequenceEqual(thin.CatalogKeys) ||
                JsonSerializer.Serialize(expected.MetadataContexts) != JsonSerializer.Serialize(thin.MetadataContexts)) return false;
            return validEvidence is null || proof.Sources.All(validEvidence);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or NullReferenceException) { return false; }
    }

    public TextureProjectionOwnerCheckpoint? LoadTextureProjectionOwner(string profile, ResourceIdentity identity, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var command = TextureCommand("SELECT payload FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity=$identity", profile);
            command.Parameters.AddWithValue("$identity", identity.Key);
            return command.ExecuteScalar() is string json && json.Length <= 16 * 1024 ? JsonSerializer.Deserialize<TextureProjectionOwnerCheckpoint>(json) : null;
        });
    }

    private static string TextureOrder(TextureDiscoveryQuery query, bool current = false) => !query.SortByName ? "p.ordinal,p.identity" :
        (query.NamedFirst ? "p.name_rank," : "") + "p.effective_sort,p.ordinal,p.identity";

    public void PrepareTextureProjectionIndexes(string profile, CancellationToken token = default)
    {
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema();
            using var transaction = connection.BeginTransaction();
            using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile, transaction);
            long generation = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
            if (generation != 0)
            {
                _ = UseCurrentTextureProjection(profile, generation, transaction);
                RefreshTextureHistory(profile, transaction);
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
        });
    }

    private bool UseCurrentTextureProjection(string profile, long generation, SqliteTransaction transaction)
    {
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.CurrentIndex", TextureDiagnosticKey());
        long current = ReadTextureProjectionHead(connection, transaction, profile, generation);
        if (current != generation)
        { RefreshTextureHistory(profile, transaction); return false; }
        using var pending = TextureCommand("SELECT 1 FROM texture_projection_current_dirty WHERE revision=$revision AND profile=$profile LIMIT 1", profile, transaction);
        if (pending.ExecuteScalar() is null) return true;
        using var refresh = TextureCommand("""
            DELETE FROM texture_projection_current AS c WHERE c.revision=$revision AND c.profile=$profile
                AND c.identity IN(SELECT identity FROM texture_projection_current_dirty WHERE revision=$revision AND profile=$profile)
                AND NOT EXISTS(SELECT 1 FROM texture_projection_rows p WHERE p.revision=c.revision AND p.profile=c.profile AND p.identity=c.identity AND p.generation<=$generation);
            INSERT INTO texture_projection_current(revision,profile,identity,generation,ordinal,name_kind,kind,label,sort_key,search,
                width,height,format,context_key,context_label,has_owner,has_conflict,has_metadata,thin,alias,name_rank,effective_sort,search_fold)
            SELECT p.revision,p.profile,p.identity,p.generation,p.ordinal,p.name_kind,p.kind,p.label,p.sort_key,p.search,
                p.width,p.height,p.format,p.context_key,p.context_label,p.has_owner,p.has_conflict,p.has_metadata,p.thin,a.alias,
                CASE WHEN p.name_kind IN(0,1) THEN 0 WHEN p.name_kind=2 THEN 1 WHEN p.name_kind=3 THEN 2 WHEN a.alias IS NOT NULL THEN 3 ELSE 4 END,
                CASE WHEN a.alias IS NULL THEN p.sort_key ELSE deviant_natural(a.alias) END,
                deviant_texture_fold(p.search||char(10)||COALESCE(a.alias,''))
            FROM texture_projection_rows p JOIN texture_projection_current_dirty d ON d.revision=p.revision AND d.profile=p.profile AND d.identity=p.identity
            LEFT JOIN texture_projection_aliases a ON a.revision=p.revision AND a.profile=p.profile AND a.identity=p.identity
            WHERE p.revision=$revision AND p.profile=$profile AND p.generation=(SELECT MAX(v.generation) FROM texture_projection_rows v
                WHERE v.revision=p.revision AND v.profile=p.profile AND v.identity=p.identity AND v.generation<=$generation)
            ON CONFLICT(revision,profile,identity) DO UPDATE SET generation=excluded.generation,ordinal=excluded.ordinal,
                name_kind=excluded.name_kind,kind=excluded.kind,label=excluded.label,sort_key=excluded.sort_key,search=excluded.search,
                width=excluded.width,height=excluded.height,format=excluded.format,context_key=excluded.context_key,context_label=excluded.context_label,
                has_owner=excluded.has_owner,has_conflict=excluded.has_conflict,has_metadata=excluded.has_metadata,thin=excluded.thin,alias=excluded.alias,
                name_rank=excluded.name_rank,effective_sort=excluded.effective_sort,search_fold=excluded.search_fold;
            DELETE FROM texture_projection_current_dirty WHERE revision=$revision AND profile=$profile;
            """, profile, transaction);
        refresh.Parameters.AddWithValue("$generation", generation); refresh.ExecuteNonQuery();
        return true;
    }

    private long TextureFilteredCount(string profile, long generation, TextureDiscoveryQuery query, bool current, SqliteTransaction transaction, string? owner = null)
    {
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Count", diagnosticKey);
        using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA data_version"; long external = Convert.ToInt64(version.ExecuteScalar());
        version.CommandText = "SELECT total_changes()"; long local = Convert.ToInt64(version.ExecuteScalar());
        if (external != textureCountDataVersion || local != textureCountChanges)
        { textureCounts.Clear(); textureCountDataVersion = external; textureCountChanges = local; }
        var key = new TextureCountKey(profile, generation, query with { Offset = 0, Limit = 1, SortByName = false, NamedFirst = false }, owner);
        if (textureCounts.TryGetValue(key, out long found)) return found;
        using var command = TextureFilter(profile, generation, query, "COUNT(*)", current, transaction);
        if (owner is not null) AddTextureOwnerFilter(command, owner, current);
        long total;
        using (TextureDiagnostic("TextureBrowser.Store.CountSql", diagnosticKey)) total = Convert.ToInt64(command.ExecuteScalar());
        if (textureCounts.Count >= 128) textureCounts.Clear();
        textureCounts[key] = total; return total;
    }

    private static string TexturePageColumns(bool current)
    {
        string projection = TexturePageProjection.Replace("a.alias", "p.alias", StringComparison.Ordinal);
        return current ? projection : projection.Replace("p.thin", "(SELECT r.thin FROM texture_projection_rows r WHERE r.revision=p.revision AND r.profile=p.profile AND r.identity=p.identity AND r.generation=" + HistoricalTextureDataGeneration + ")", StringComparison.Ordinal);
    }

    private const string TextureHistoryRowEvents = """
        SELECT r.revision,r.profile,r.identity,r.generation FROM texture_projection_history_dirty d
            CROSS JOIN texture_projection_rows r WHERE d.revision=$revision AND d.profile=$profile
            AND r.revision=d.revision AND r.profile=d.profile AND r.identity=d.identity
        """;
    private const string TextureHistoryAliasEvents = """
        SELECT a.revision,a.profile,a.identity,a.generation FROM texture_projection_history_dirty d
            CROSS JOIN texture_projection_alias_versions a WHERE d.revision=$revision AND d.profile=$profile
            AND a.revision=d.revision AND a.profile=d.profile AND a.identity=d.identity
        """;

    private void RefreshTextureHistory(string profile, SqliteTransaction transaction, bool prepareSummary = true)
    {
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.HistoryIndex", TextureDiagnosticKey());
        using var pending = TextureCommand("SELECT 1 FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile LIMIT 1", profile, transaction);
        if (pending.ExecuteScalar() is null)
        {
            if (prepareSummary) RefreshTextureSummaryDirty(profile, transaction);
            return;
        }
        using var refresh = TextureCommand($$"""
            DELETE FROM texture_projection_history_index AS h WHERE h.revision=$revision AND h.profile=$profile
                AND h.identity IN(SELECT identity FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile);
            WITH events AS (
                {{TextureHistoryRowEvents}}
                UNION
                {{TextureHistoryAliasEvents}}
            ), timeline AS (
                SELECT e.*,LEAD(e.generation) OVER(PARTITION BY e.revision,e.profile,e.identity ORDER BY e.generation) AS next_generation,
                    (SELECT MAX(r.generation) FROM texture_projection_rows r WHERE r.revision=e.revision AND r.profile=e.profile
                        AND r.identity=e.identity AND r.generation<=e.generation) AS data_generation FROM events e
            )
            INSERT INTO texture_projection_history_index(revision,profile,identity,generation,next_generation,ordinal,name_kind,kind,label,sort_key,search,
                width,height,format,context_key,context_label,has_owner,has_conflict,has_metadata,alias,name_rank,effective_sort,search_fold,data_generation)
            SELECT p.revision,p.profile,p.identity,t.generation,t.next_generation,p.ordinal,p.name_kind,p.kind,p.label,p.sort_key,p.search,
                p.width,p.height,p.format,p.context_key,p.context_label,p.has_owner,p.has_conflict,p.has_metadata,a.alias,
                CASE WHEN p.name_kind IN(0,1) THEN 0 WHEN p.name_kind=2 THEN 1 WHEN p.name_kind=3 THEN 2 WHEN a.alias IS NOT NULL THEN 3 ELSE 4 END,
                CASE WHEN a.alias IS NULL THEN p.sort_key ELSE deviant_natural(a.alias) END,deviant_texture_fold(p.search||char(10)||COALESCE(a.alias,'')),p.generation
            FROM timeline t JOIN texture_projection_rows p ON p.revision=t.revision AND p.profile=t.profile
                AND p.identity=t.identity AND p.generation=t.data_generation
            LEFT JOIN texture_projection_alias_versions a ON a.revision=p.revision AND a.profile=p.profile AND a.identity=p.identity
                AND a.generation=(SELECT MAX(v.generation) FROM texture_projection_alias_versions v WHERE v.revision=p.revision
                    AND v.profile=p.profile AND v.identity=p.identity AND v.generation<=t.generation)
            WHERE p.revision=$revision AND p.profile=$profile;
            DELETE FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile;
            """, profile, transaction);
        refresh.ExecuteNonQuery();
        if (prepareSummary) RefreshTextureSummaryDirty(profile, transaction);
    }

    private static void AddTextureOwnerFilter(SqliteCommand command, string owner, bool current)
    {
        command.CommandText += current ?
            " AND p.identity IN(SELECT l.identity FROM texture_projection_links l JOIN texture_projection_current c ON c.revision=l.revision AND c.profile=l.profile AND c.identity=l.identity AND c.generation=l.generation WHERE l.revision=$revision AND l.profile=$profile AND l.relation=0 AND l.verified=1 AND l.link_key=$ownerKey)" :
            " AND EXISTS(SELECT 1 FROM texture_projection_links l WHERE l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=" + HistoricalTextureDataGeneration + " AND l.relation=0 AND l.verified=1 AND l.link_key=$ownerKey)";
        command.Parameters.AddWithValue("$ownerKey", owner);
    }

    public TextureDiscoveryPage QueryTextureProjections(string profile, long generation, TextureDiscoveryQuery query, CancellationToken token = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Page", diagnosticKey);
        using var waitDiagnostic = TextureDiagnostic("TextureBrowser.Store.PageWait", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            waitDiagnostic?.Dispose();
            using var holdDiagnostic = TextureDiagnostic("TextureBrowser.Store.PageHold", diagnosticKey);
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            bool current = UseCurrentTextureProjection(profile, generation, transaction);
            long total = TextureFilteredCount(profile, generation, query, current, transaction);
            using var page = TextureFilter(profile, generation, query, TexturePageColumns(current), current, transaction);
            page.CommandText += " ORDER BY " + TextureOrder(query, current) + " LIMIT $limit OFFSET $offset";
            page.Parameters.AddWithValue("$limit", query.Limit); page.Parameters.AddWithValue("$offset", query.Offset);
            var result = new List<TextureDiscoveryEntry>(); SqliteDataReader reader;
            using (TextureDiagnostic("TextureBrowser.Store.PageSql", diagnosticKey)) reader = page.ExecuteReader();
            using var readerLease = reader;
            using var materializeDiagnostic = TextureDiagnostic("TextureBrowser.Store.PageMaterialize", diagnosticKey);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var value = JsonSerializer.Deserialize<TextureDiscoveryEntry>(reader.GetString(0))!;
                result.Add(value with { Alias = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CatalogKeys = value.CatalogKeys.Concat(JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
            }
            reader.Close(); materializeDiagnostic?.Dispose(); token.ThrowIfCancellationRequested();
            using (TextureDiagnostic("TextureBrowser.Store.PageCommit", diagnosticKey)) transaction.Commit();
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.PageRows", diagnosticKey, new Dictionary<string, long>
            { ["Generation"] = generation, ["Rows"] = result.Count, ["Total"] = total, ["Offset"] = query.Offset });
            return new TextureDiscoveryPage(result, total, query.Offset);
        });
    }

    public TextureDiscoveryPage QueryTextureProjectionsByOwner(string profile, long generation, ResourceIdentity owner,
        long offset, int limit, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            EnsureTextureSchema(); var query = new TextureDiscoveryQuery(Offset: offset, Limit: limit, IncludeLodBuffers: true);
            using var transaction = connection.BeginTransaction();
            bool current = UseCurrentTextureProjection(profile, generation, transaction);
            long total = TextureFilteredCount(profile, generation, query, current, transaction, owner.Key);
            using var page = TextureFilter(profile, generation, query, TexturePageColumns(current), current, transaction); AddTextureOwnerFilter(page, owner.Key, current);
            page.CommandText += " ORDER BY " + TextureOrder(query, current) + " LIMIT $limit OFFSET $offset";
            page.Parameters.AddWithValue("$limit", limit); page.Parameters.AddWithValue("$offset", offset);
            var result = new List<TextureDiscoveryEntry>(); using var reader = page.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); var entry = JsonSerializer.Deserialize<TextureDiscoveryEntry>(reader.GetString(0))!;
                result.Add(entry with { Alias = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CatalogKeys = entry.CatalogKeys.Concat(JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
            }
            reader.Close(); token.ThrowIfCancellationRequested(); transaction.Commit();
            return new TextureDiscoveryPage(result, total, offset);
        });
    }

    public TextureDiscoveryPage? LocateTextureProjection(string profile, long generation, ResourceIdentity identity,
        TextureDiscoveryQuery query, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            bool current = UseCurrentTextureProjection(profile, generation, transaction);
            using var command = TextureFilter(profile, generation, query,
                "p.identity,ROW_NUMBER() OVER(ORDER BY " + TextureOrder(query, current) + ")-1 AS position", current, transaction);
            command.CommandText = "SELECT position FROM (" + command.CommandText + ") WHERE identity=$identity";
            command.Parameters.AddWithValue("$identity", identity.Key); var position = command.ExecuteScalar();
            if (position is null or DBNull) { transaction.Commit(); return null; }
            long offset = Convert.ToInt64(position) / query.Limit * query.Limit;
            transaction.Commit();
            return QueryTextureProjections(profile, generation, query with { Offset = offset }, token);
        });
    }

    public TextureProjectionStatistics TextureProjectionCounts(string profile, long generation, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            bool current = UseCurrentTextureProjection(profile, generation, transaction);
            using var command = TextureCommand((current ? TextureCurrentLatest : TextureHistoricalLatest) + "SELECT COUNT(*),COALESCE(SUM(has_metadata),0)," +
                "COALESCE(SUM(name_kind IN (0,1)),0),COALESCE(SUM(name_kind=2),0),COALESCE(SUM(name_kind=3),0),COALESCE(SUM(name_kind=4),0)," +
                "COALESCE(SUM(alias IS NOT NULL),0),COALESCE(SUM(has_conflict),0)," +
                "COALESCE(SUM(kind=0),0),COALESCE(SUM(kind=1),0),COALESCE(SUM(kind=2),0)," +
                "(SELECT COUNT(DISTINCT l.link_key) FROM latest p JOIN texture_projection_links l ON l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=" +
                (current ? "p.generation" : HistoricalTextureDataGeneration) + " WHERE l.relation=3) FROM latest", profile, transaction);
            command.Parameters.AddWithValue("$generation", generation); using var reader = command.ExecuteReader(); reader.Read();
            var result = new TextureProjectionStatistics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11));
            reader.Close(); token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        });
    }

    public TextureDiscoveryEntry? LoadTextureProjectionDetails(string profile, long generation, ResourceIdentity identity, CancellationToken token = default)
    {
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Evidence", diagnosticKey);
        using var waitDiagnostic = TextureDiagnostic("TextureBrowser.Store.EvidenceWait", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            waitDiagnostic?.Dispose();
            using var holdDiagnostic = TextureDiagnostic("TextureBrowser.Store.EvidenceHold", diagnosticKey);
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction(deferred: true);
            _ = ReadTextureProjectionHead(connection, transaction, profile, generation);
            using var command = TextureCommand("SELECT length(e.payload),CASE WHEN length(e.payload)<=$maximum THEN e.payload ELSE NULL END,e.sha256 FROM texture_projection_evidence e WHERE e.revision=$revision AND e.profile=$profile AND e.identity=$identity AND e.generation=(SELECT MAX(p.generation) FROM texture_projection_rows p WHERE p.revision=e.revision AND p.profile=e.profile AND p.identity=e.identity AND p.generation<=$generation)", profile, transaction);
            command.Parameters.AddWithValue("$identity", identity.Key); command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$maximum", MaximumTextureEvidenceBytes);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                reader.Close(); token.ThrowIfCancellationRequested(); transaction.Commit(); return null;
            }
            if (reader.GetInt64(0) > MaximumTextureEvidenceBytes || reader.IsDBNull(1)) throw new InvalidDataException("TextureProjectionEvidenceLimit");
            var payload = (byte[])reader.GetValue(1);
            if (Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() != reader.GetString(2))
                throw new InvalidDataException("TextureProjectionEvidenceChecksum");
            var result = JsonSerializer.Deserialize<TextureDiscoveryEntry>(payload);
            reader.Close(); token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        });
    }

    public TextureDiscoveryFilterOptions TextureProjectionFilters(string profile, long generation, CancellationToken token = default)
    {
        if (generation == CaptureTextureProjectionGeneration(profile, token))
            while (!PrepareCurrentTextureRoleBatch(profile, generation, token)) { token.ThrowIfCancellationRequested(); Thread.Yield(); }
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Store.Options", diagnosticKey);
        using var waitDiagnostic = TextureDiagnostic("TextureBrowser.Store.OptionsWait", diagnosticKey);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            waitDiagnostic?.Dispose();
            using var holdDiagnostic = TextureDiagnostic("TextureBrowser.Store.OptionsHold", diagnosticKey);
            EnsureTextureSchema(); using var transaction = connection.BeginTransaction();
            bool current = UseCurrentTextureProjection(profile, generation, transaction);
            if (!current)
            {
                using var indexDiagnostic = TextureDiagnostic("TextureBrowser.Store.HistoricalOptionsIndexBuild", diagnosticKey);
                using var index = connection.CreateCommand(); index.Transaction = transaction;
                index.CommandText = "CREATE INDEX IF NOT EXISTS texture_history_options ON texture_projection_history_index " +
                "(revision,profile,width,height,format,context_key,context_label,has_owner,identity,generation,next_generation,data_generation)";
                index.ExecuteNonQuery();
            }
            var dimensions = new List<TextureDimensionOption>(); var formats = new List<string>(); var roles = new List<string>(); var contexts = new List<TextureContextOption>();
            using (TextureDiagnostic("TextureBrowser.Store.OptionsDimensionsSql", diagnosticKey))
            using (var command = TextureOptionsCommand(profile, generation, current, "dimensions", transaction))
            { using var reader = command.ExecuteReader(); while (reader.Read()) dimensions.Add(new(reader.GetInt32(0), reader.GetInt32(1))); }
            using (TextureDiagnostic("TextureBrowser.Store.OptionsFormatsSql", diagnosticKey))
            using (var command = TextureOptionsCommand(profile, generation, current, "formats", transaction))
            { using var reader = command.ExecuteReader(); while (reader.Read()) formats.Add(reader.GetString(0)); }
            using (TextureDiagnostic("TextureBrowser.Store.OptionsRolesSql", diagnosticKey))
            using (var command = TextureOptionsCommand(profile, generation, current, "roles", transaction))
            { using var reader = command.ExecuteReader(); while (reader.Read()) roles.Add(reader.GetString(0)); }
            using (TextureDiagnostic("TextureBrowser.Store.OptionsContextsSql", diagnosticKey))
            using (var command = TextureOptionsCommand(profile, generation, current, "contexts", transaction))
            { using var reader = command.ExecuteReader(); while (reader.Read()) contexts.Add(new(reader.GetString(0), reader.GetString(1))); }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return new TextureDiscoveryFilterOptions(dimensions, formats, roles, contexts);
        });
    }

    private SqliteCommand TextureOptionsCommand(string profile, long generation, bool current, string facet, SqliteTransaction? transaction = null,
        SqliteConnection? readConnection = null)
    {
        string table = current ? "texture_projection_current" : "texture_projection_history_index";
        string index = current ? "texture_current_options" : "texture_history_options";
        string scope = "p.revision=$revision AND p.profile=$profile" + (current ? "" :
            " AND p.generation<=$generation AND (p.next_generation IS NULL OR p.next_generation>$generation)");
        string from = " FROM " + table + " p INDEXED BY " + index;
        string sql = facet switch
        {
            "dimensions" => "SELECT DISTINCT p.width,p.height" + from + " WHERE " + scope +
                " AND p.width IS NOT NULL AND p.height IS NOT NULL ORDER BY p.width,p.height",
            "formats" => "SELECT DISTINCT p.format" + from + " WHERE " + scope + " AND p.format IS NOT NULL ORDER BY p.format",
            "contexts" => "SELECT DISTINCT p.context_key,p.context_label" + from + " WHERE " + scope +
                " AND p.context_key IS NOT NULL AND p.context_label IS NOT NULL ORDER BY p.context_label",
            // The current role cache has explicit per-row receipts; historical reads seek each role's active source version.
            "roles" when current => "SELECT DISTINCT l.role FROM texture_projection_current_roles l " +
                "JOIN texture_projection_current_role_rows p ON p.revision=l.revision AND p.profile=l.profile AND p.identity=l.identity AND p.generation=l.generation " +
                "WHERE l.revision=$revision AND l.profile=$profile ORDER BY l.role",
            "roles" => "SELECT DISTINCT l.role FROM texture_projection_links l INDEXED BY texture_projection_role " +
                "WHERE l.revision=$revision AND l.profile=$profile AND l.relation=0 AND l.verified=1 AND l.role!='' " +
                "AND l.generation=(SELECT MAX(r.generation) FROM texture_projection_rows r " +
                "WHERE r.revision=l.revision AND r.profile=l.profile AND r.identity=l.identity AND r.generation<=$generation) ORDER BY l.role",
            _ => throw new ArgumentOutOfRangeException(nameof(facet))
        };
        var command = readConnection is null ? TextureCommand(sql, profile, transaction) : readConnection.CreateCommand();
        if (readConnection is not null)
        {
            command.CommandText = sql; command.Transaction = transaction;
            command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$profile", profile);
        }
        if (!current) command.Parameters.AddWithValue("$generation", generation);
        return command;
    }

    private SqliteCommand TextureFilter(string profile, long generation, TextureDiscoveryQuery query, string projection,
        bool current = false, SqliteTransaction? transaction = null, SqliteConnection? readConnection = null)
    {
        var terms = (query.Search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool trigram = terms.Any(term => term.EnumerateRunes().Count() >= 3);
        string table = current ? "texture_projection_current" : "texture_projection_history_index";
        string indexPrefix = current ? "texture_current_" : "texture_history_";
        string fts = current ? "texture_projection_current_fts" : "texture_projection_history_fts";
        string index = "";
        if (trigram) index = " NOT INDEXED";
        else if (projection != "COUNT(*)") index = " INDEXED BY " + indexPrefix +
            (!query.SortByName ? "ordinal" : !query.NamedFirst ? "name" :
                query.Kind != TextureKindFilter.All ? "kind_named" :
                !query.IncludeLodBuffers ? "visible_named" : "named");
        string sql = "SELECT " + projection + " FROM " + table + " p" + index +
            " WHERE p.revision=$revision AND p.profile=$profile" + (current ? "" :
                " AND p.generation<=$generation AND (p.next_generation IS NULL OR p.next_generation>$generation)");
        var command = readConnection is null ? TextureCommand(sql, profile, transaction) :
            ReadTextureCommand(readConnection, transaction!, sql, revision, profile);
        command.Parameters.AddWithValue("$generation", generation);
        if (!query.IncludeLodBuffers) command.CommandText += " AND p.kind!=2";
        if (query.Kind != TextureKindFilter.All) { command.CommandText += " AND p.kind=$kind"; command.Parameters.AddWithValue("$kind", query.Kind == TextureKindFilter.Images ? 0 : 1); }
        foreach (var term in terms)
        {
            string key = "$term" + command.Parameters.Count;
            command.CommandText += " AND deviant_contains(p.search||char(10)||COALESCE(p.alias,'')," + key + ")";
            command.Parameters.AddWithValue(key, term);
            if (term.EnumerateRunes().Count() >= 3)
            {
                command.CommandText += " AND p.row_id IN(SELECT rowid FROM " + fts + " WHERE " + fts + " MATCH " + key + "Fts)";
                command.Parameters.AddWithValue(key + "Fts", "\"" + term.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
        }
        Value("width", query.Width); Value("height", query.Height); Value("format", query.Format); Value("context_key", query.ContextKey);
        if (query.HasVerifiedOwner is { } owner) { command.CommandText += " AND p.has_owner=$hasOwner"; command.Parameters.AddWithValue("$hasOwner", owner ? 1 : 0); }
        if (query.VerifiedRole is { } role) Relation(0, "role", role);
        if (query.CatalogKey is { } catalog)
        {
            command.CommandText += current ?
                " AND (p.identity IN(SELECT l.identity FROM texture_projection_links l JOIN texture_projection_current c ON c.revision=l.revision AND c.profile=l.profile AND c.identity=l.identity AND c.generation=l.generation WHERE l.revision=$revision AND l.profile=$profile AND l.relation=2 AND l.verified=1 AND l.link_key=$catalog) OR " +
                "p.identity IN(SELECT m.identity FROM texture_projection_catalog_members m WHERE m.revision=$revision AND m.profile=$profile AND m.catalog=$catalog AND m.generation=(SELECT MAX(c.generation) FROM texture_projection_catalogs c WHERE c.revision=$revision AND c.profile=$profile AND c.catalog=$catalog AND c.generation<=$generation)))" :
                " AND (EXISTS(SELECT 1 FROM texture_projection_links l WHERE l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=" + HistoricalTextureDataGeneration + " AND l.relation=2 AND l.verified=1 AND l.link_key=$catalog) OR EXISTS(" +
                "SELECT 1 FROM texture_projection_catalog_members m WHERE " + TextureDirectMembership + " AND m.catalog=$catalog))";
            command.Parameters.AddWithValue("$catalog", catalog);
        }
        void Value(string column, object? value) { if (value is null) return; command.CommandText += " AND p." + column + "=$" + column; command.Parameters.AddWithValue("$" + column, value); }
        void Relation(int relation, string column, string value)
        {
            string parameter = "$relation" + relation;
            command.CommandText += current ?
                " AND p.identity IN(SELECT l.identity FROM texture_projection_links l JOIN texture_projection_current c ON c.revision=l.revision AND c.profile=l.profile AND c.identity=l.identity AND c.generation=l.generation WHERE l.revision=$revision AND l.profile=$profile AND l.relation=" + relation + " AND l.verified=1 AND l." + column + "=" + parameter + ")" :
                " AND EXISTS(SELECT 1 FROM texture_projection_links l WHERE l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=" + HistoricalTextureDataGeneration + " AND l.relation=" + relation + " AND l.verified=1 AND l." + column + "=" + parameter + ")";
            command.Parameters.AddWithValue(parameter, value);
        }
        return command;
    }
    private static string TextureDiagnosticKey() => "texture-db/" + Interlocked.Increment(ref textureDiagnosticSequence);
    private static IDisposable? TextureDiagnostic(string stage, string key) =>
        AssetLoadDiagnostics.CountersEnabled || AssetLoadDiagnostics.Listener.IsEnabled(AssetLoadDiagnostics.MeasurementName)
            ? new TextureDiagnosticPhase(stage, key) : null;
    private sealed class TextureDiagnosticPhase : IDisposable
    {
        private readonly string stage, key;
        private readonly IDisposable? timing;
        private int disposed;
        public TextureDiagnosticPhase(string stage, string key)
        {
            this.stage = stage; this.key = key;
            AssetLoadDiagnostics.Counters(stage, key, new Dictionary<string, long> { ["Active"] = 1, ["ThreadId"] = Environment.CurrentManagedThreadId });
            timing = AssetLoadDiagnostics.Measure(stage, key);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            timing?.Dispose();
            AssetLoadDiagnostics.Counters(stage, key, new Dictionary<string, long> { ["Active"] = 0, ["ThreadId"] = Environment.CurrentManagedThreadId });
        }
    }

    private SqliteCommand TextureCommand(string sql, string profile, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$profile", profile); return command;
    }
}
