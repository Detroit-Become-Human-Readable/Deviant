using System.Text.Json;
using Deviant.Assets;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    /// <summary>Loads one asset descriptor by its exact indexed identity key.</summary>
    public AssetDescriptor? LoadAssetByIdentity(string identityKey, int maxPayloadBytes,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        if (maxPayloadBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));

        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT lookup_key,length(CAST(payload AS BLOB)),payload FROM assets_v2 WHERE revision=$revision AND identity=$identity";
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$identity", identityKey);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var payloadBytes = reader.GetInt64(1);
            if (payloadBytes < 1 || payloadBytes > maxPayloadBytes)
                throw new InvalidDataException("AssetDescriptorPayloadLimit");
            var lookupKey = reader.IsDBNull(0) ? null : reader.GetString(0);
            var payload = reader.GetString(2);
            token.ThrowIfCancellationRequested();
            try
            {
                var descriptor = JsonSerializer.Deserialize<AssetDescriptor>(payload);
                if (descriptor is null) throw new JsonException("Asset descriptor payload is empty.");
                return descriptor with { LookupKey = lookupKey };
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("AssetDescriptorPayloadInvalid", error);
            }
        });
    }
}
