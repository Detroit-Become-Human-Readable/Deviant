using Deviant.Assets;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private const string HistoricalTextureDataGeneration = "COALESCE(p.data_generation,p.generation)";

    private void EnsureTextureAliasSchema(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(texture_projection_history_index)";
        bool hasDataGeneration = false;
        using (var columns = command.ExecuteReader())
            while (columns.Read()) hasDataGeneration |= columns.GetString(1) == "data_generation";
        if (!hasDataGeneration)
        {
            command.CommandText = "ALTER TABLE texture_projection_history_index ADD COLUMN data_generation INTEGER; " +
                "DROP INDEX IF EXISTS texture_history_options";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_projection_alias_versions(
                revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,generation INTEGER NOT NULL,alias TEXT,
                PRIMARY KEY(revision,profile,identity,generation));
            CREATE TABLE IF NOT EXISTS texture_projection_alias_migrations(version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO texture_projection_alias_versions
                SELECT revision,profile,identity,0,alias FROM texture_projection_aliases
                WHERE NOT EXISTS(SELECT 1 FROM texture_projection_alias_migrations WHERE version=1);
            INSERT OR IGNORE INTO texture_projection_history_dirty
                SELECT DISTINCT revision,profile,identity FROM texture_projection_rows
                WHERE NOT EXISTS(SELECT 1 FROM texture_projection_alias_migrations WHERE version=1);
            INSERT OR IGNORE INTO texture_projection_alias_migrations VALUES(1);
            """;
        command.ExecuteNonQuery();
    }

    public void SetTextureProjectionAlias(string profile, ResourceIdentity identity, string? alias, CancellationToken token = default)
    {
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema();
            using var transaction = connection.BeginTransaction();
            using var read = TextureCommand("SELECT alias FROM texture_projection_aliases WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction);
            read.Parameters.AddWithValue("$identity", identity.Key);
            var current = read.ExecuteScalar() as string;
            if (string.Equals(current, alias, StringComparison.Ordinal)) return false;
            PublishTextureAliasChanges(profile, new Dictionary<string, string?> { [identity.Key] = alias }, transaction, token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    public void ReplaceTextureProjectionAliases(string profile, IReadOnlyDictionary<ResourceIdentity, string> aliases,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema();
            using var transaction = connection.BeginTransaction();
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var read = TextureCommand("SELECT identity,alias FROM texture_projection_aliases WHERE revision=$revision AND profile=$profile", profile, transaction))
            using (var rows = read.ExecuteReader())
                while (rows.Read()) { token.ThrowIfCancellationRequested(); current.Add(rows.GetString(0), rows.GetString(1)); }
            var requested = aliases.ToDictionary(pair => pair.Key.Key, pair => pair.Value, StringComparer.Ordinal);
            var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in current.Keys.Concat(requested.Keys).Distinct(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                current.TryGetValue(key, out var oldValue); requested.TryGetValue(key, out var newValue);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add(key, newValue);
            }
            if (changes.Count == 0) return false;
            PublishTextureAliasChanges(profile, changes, transaction, token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    private void PublishTextureAliasChanges(string profile, IReadOnlyDictionary<string, string?> changes,
        SqliteTransaction transaction, CancellationToken token)
    {
        using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", profile, transaction);
        long previous = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
        long generation = previous == 0 ? 0 : checked(previous + 1);
        foreach (var pair in changes)
        {
            token.ThrowIfCancellationRequested();
            using var current = TextureCommand(pair.Value is null
                ? "DELETE FROM texture_projection_aliases WHERE revision=$revision AND profile=$profile AND identity=$identity"
                : "INSERT OR REPLACE INTO texture_projection_aliases VALUES($revision,$profile,$identity,$alias)", profile, transaction);
            current.Parameters.AddWithValue("$identity", pair.Key);
            if (pair.Value is not null) current.Parameters.AddWithValue("$alias", pair.Value);
            current.ExecuteNonQuery();
            using var version = TextureCommand("INSERT OR REPLACE INTO texture_projection_alias_versions VALUES($revision,$profile,$identity,$generation,$alias)", profile, transaction);
            version.Parameters.AddWithValue("$identity", pair.Key);
            version.Parameters.AddWithValue("$generation", generation);
            version.Parameters.AddWithValue("$alias", (object?)pair.Value ?? DBNull.Value);
            version.ExecuteNonQuery();
        }
        RefreshTextureHistory(profile, transaction);
        if (previous > 0)
        {
            using var publish = TextureCommand("UPDATE texture_projection_heads SET generation=$generation WHERE revision=$revision AND profile=$profile", profile, transaction);
            publish.Parameters.AddWithValue("$generation", generation); publish.ExecuteNonQuery();
        }
        AssetLoadDiagnostics.Counters("TextureBrowser.Store.AliasPublicationPrepared", profile,
            new Dictionary<string, long> { ["Generation"] = generation, ["Changed"] = changes.Count });
    }
}
