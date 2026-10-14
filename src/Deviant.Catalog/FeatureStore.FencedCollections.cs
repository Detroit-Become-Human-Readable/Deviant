using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    public void ResetItemsWithFeature<THead>(string collection, string version, string feature, THead head,
        CancellationToken token = default)
    {
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM asset_features WHERE revision=$revision AND instr(feature,$prefix)=1";
            remove.Parameters.AddWithValue("$revision", revision);
            remove.Parameters.AddWithValue("$prefix", collection + "/item/");
            remove.ExecuteNonQuery();
            WriteFeature(transaction, feature, version, head);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    public bool TryUpdateItemWithFeatureFence<THead, TItem>(string collection, string key, string version,
        string feature, Func<THead?, bool> accepts, Func<TItem?, TItem> update, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            if (!accepts(ReadFeature<THead>(transaction, feature, version))) return false;
            var itemKey = collection + "/item/" + key;
            WriteFeature(transaction, itemKey, version, update(ReadFeature<TItem>(transaction, itemKey, version)));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    public bool TrySaveFeatureWithFence<THead>(string feature, string version, Func<THead?, bool> accepts,
        THead replacement, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            if (!accepts(ReadFeature<THead>(transaction, feature, version))) return false;
            WriteFeature(transaction, feature, version, replacement);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    public bool TryLoadItemWithFeatureFence<THead, TItem>(string collection, string key, string version,
        string feature, Func<THead?, bool> accepts, out TItem? item, CancellationToken token = default)
    {
        TItem? result = default;
        bool accepted;
        lock (gate) accepted = CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            if (!accepts(ReadFeature<THead>(transaction, feature, version))) return false;
            result = ReadFeature<TItem>(transaction, collection + "/item/" + key, version);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
        item = result;
        return accepted;
    }

    private T? ReadFeature<T>(SqliteTransaction transaction, string key, string version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", version);
        var value = command.ExecuteScalar() as string;
        return value is null ? default : JsonSerializer.Deserialize<T>(value);
    }

    private void WriteFeature<T>(SqliteTransaction transaction, string key, string version, T value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO asset_features VALUES($revision,$key,$version,$payload)";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
        command.ExecuteNonQuery();
    }
}
