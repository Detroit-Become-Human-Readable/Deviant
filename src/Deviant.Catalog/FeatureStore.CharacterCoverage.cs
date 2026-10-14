using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private const string CharacterCoverageProjection = """
        json_object(
            'DescriptorIdentity', json_extract(payload, '$.Descriptor.Identity'),
            'DescriptorRevision', json_extract(payload, '$.Descriptor.Revision'),
            'DescriptorFamily', json_extract(payload, '$.Descriptor.Family'),
            'NameProfile', json_extract(payload, '$.Descriptor.Metadata."model.name-profile"'),
            'SourceSummaryIdentity', json_extract(payload, '$.SourceSummary.Identity'),
            'SourceSummaryRevision', json_extract(payload, '$.SourceSummary.Revision'),
            'StatisticsAvailability', json_extract(payload, '$.SourceSummary.StatisticsAvailability'),
            'SourceExtents', json(json_extract(payload, '$.SourceSummary.SourceExtents')),
            'CatalogLinks', json(coalesce((
                SELECT json_group_array(json_object(
                    'CatalogIdentity', json_extract(link.value, '$.CatalogIdentity'),
                    'ReferenceTableSha256', json_extract(link.value, '$.ReferenceTableSha256'),
                    'ResolutionCandidates', json(json_extract(link.value, '$.Relationship.Resolution.Candidates'))
                ))
                FROM json_each(
                    CASE WHEN json_type(payload, '$.CatalogLinks') = 'array'
                        THEN payload ELSE '{"CatalogLinks":[]}' END,
                    '$.CatalogLinks') AS link
            ), '[]'))
        )
        """;

    /// <summary>Loads only the identity, source extents, and catalog memberships needed to validate restored character-name coverage.</summary>
    public T? LoadCharacterCoverageProjection<T>(string key, string version, int maximumPayloadBytes,
        int maximumProjectionBytes, Action<int> reserveProjectionBytes, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumProjectionBytes);
        ArgumentNullException.ThrowIfNull(reserveProjectionBytes);

        var projectionSql = $"SELECT {CharacterCoverageProjection} FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            using (var size = connection.CreateCommand())
            {
                size.Transaction = transaction;
                size.CommandText = $"SELECT length(CAST(payload AS BLOB)), length(CAST(({CharacterCoverageProjection}) AS BLOB)) " +
                    "FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
                AddProjectionParameters(size, key, version);
                using var row = size.ExecuteReader();
                if (!row.Read())
                {
                    transaction.Commit();
                    return default;
                }

                if (row.IsDBNull(0) || row.IsDBNull(1))
                    throw new InvalidDataException("The cached character model entry is not valid JSON.");
                var payloadBytes = row.GetInt64(0);
                var projectionBytes = row.GetInt64(1);
                if (payloadBytes < 0 || payloadBytes > maximumPayloadBytes)
                    throw new InvalidDataException("The cached character model entry exceeds its configured size limit.");
                if (projectionBytes < 0 || projectionBytes > maximumProjectionBytes)
                    throw new InvalidDataException("The character coverage projection exceeds its configured size limit.");
                reserveProjectionBytes(checked((int)projectionBytes));
            }

            token.ThrowIfCancellationRequested();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = projectionSql;
            AddProjectionParameters(read, key, version);
            var projected = read.ExecuteScalar() as string
                ?? throw new InvalidDataException("The cached character coverage projection is invalid.");
            T? value;
            try { value = JsonSerializer.Deserialize<T>(projected); }
            catch (Exception error) when (error is JsonException or NotSupportedException or SqliteException)
            { throw new InvalidDataException("The cached character coverage projection is invalid.", error); }
            if (value is null) throw new InvalidDataException("The cached character coverage projection is null.");
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return value;
        });
    }

    private void AddProjectionParameters(SqliteCommand command, string key, string version)
    {
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", version);
    }
}
