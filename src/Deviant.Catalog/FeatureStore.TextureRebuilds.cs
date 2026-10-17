using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed record TextureProjectionOwnerRebuild(ResourceIdentity Identity, long RequestId);

public sealed partial class FeatureStore
{
    private void EnsureTextureRebuildSchema(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS texture_projection_owner_rebuilds(request_id INTEGER PRIMARY KEY AUTOINCREMENT,
                revision TEXT NOT NULL,profile TEXT NOT NULL,identity TEXT NOT NULL,index_content_id TEXT NOT NULL,ordinal INTEGER NOT NULL,
                UNIQUE(revision,profile,identity));
            """;
        command.ExecuteNonQuery();
    }

    private bool CanQueueTextureOwnerRebuild(string profile, ResourceIdentity owner,
        Func<ResourceIdentity, bool>? canRebuild, SqliteTransaction transaction)
    {
        if (canRebuild is not null) return canRebuild(owner);
        using var command = TextureCommand("SELECT 1 FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity=$identity", profile, transaction);
        command.Parameters.AddWithValue("$identity", owner.Key); return command.ExecuteScalar() is not null;
    }

    private void QueueTextureOwnerRebuilds(string profile, IEnumerable<ResourceIdentity> owners,
        SqliteTransaction transaction, CancellationToken token)
    {
        foreach (var owner in owners)
        {
            token.ThrowIfCancellationRequested();
            using var command = TextureCommand("INSERT OR REPLACE INTO texture_projection_owner_rebuilds(revision,profile,identity,index_content_id,ordinal) " +
                "VALUES($revision,$profile,$identity,$index,$ordinal)", profile, transaction);
            command.Parameters.AddWithValue("$identity", owner.Key); command.Parameters.AddWithValue("$index", owner.IndexContentId);
            command.Parameters.AddWithValue("$ordinal", owner.Ordinal); command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<TextureProjectionOwnerRebuild> LoadTextureProjectionOwnerRebuilds(string profile, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var command = TextureCommand("SELECT index_content_id,ordinal,request_id FROM texture_projection_owner_rebuilds " +
                "WHERE revision=$revision AND profile=$profile ORDER BY ordinal", profile);
            var result = new List<TextureProjectionOwnerRebuild>(); using var rows = command.ExecuteReader();
            while (rows.Read()) { token.ThrowIfCancellationRequested(); result.Add(new(new(rows.GetString(0), rows.GetInt64(1)), rows.GetInt64(2))); }
            return (IReadOnlyList<TextureProjectionOwnerRebuild>)result;
        });
    }

    public long? TextureProjectionOwnerRebuildRequest(string profile, ResourceIdentity owner, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            EnsureTextureSchema(); using var command = TextureCommand("SELECT request_id FROM texture_projection_owner_rebuilds " +
                "WHERE revision=$revision AND profile=$profile AND identity=$identity", profile);
            command.Parameters.AddWithValue("$identity", owner.Key); return command.ExecuteScalar() is long request ? (long?)request : null;
        });
    }

    private void CompleteTextureOwnerRebuild(string profile, ResourceIdentity owner, long? completedRequest,
        SqliteTransaction transaction)
    {
        if (completedRequest is null) return;
        using var command = TextureCommand("DELETE FROM texture_projection_owner_rebuilds WHERE revision=$revision AND profile=$profile " +
            "AND identity=$identity AND request_id<=$request", profile, transaction);
        command.Parameters.AddWithValue("$identity", owner.Key); command.Parameters.AddWithValue("$request", completedRequest.Value);
        command.ExecuteNonQuery();
    }
}
