using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private const string LocalizationValidationContract = "localization-validation-v1";
    private readonly Dictionary<string, (string Generation, long External, long Local)> localizationReadValidations = [];
    private sealed class LocalizationValidationChangedException : Exception;

    private void InitializeLocalizationValidation()
    {
        using var transaction = connection.BeginTransaction();
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS localization_generation_validation(
              revision TEXT NOT NULL, generation TEXT NOT NULL, record_version TEXT NOT NULL,
              search_version TEXT NOT NULL, contract TEXT NOT NULL, token TEXT NOT NULL,
              PRIMARY KEY(revision,generation,record_version,search_version,contract));
            """;
        schema.ExecuteNonQuery();
        using (var replacement = connection.CreateCommand())
        {
            replacement.Transaction = transaction;
            replacement.CommandText = """
                CREATE TRIGGER IF NOT EXISTS localization_rows_validation_replace
                BEFORE INSERT ON localization_generation_rows BEGIN
                  DELETE FROM localization_generation_validation
                  WHERE (revision,generation) IN
                    (SELECT revision,generation FROM localization_generation_rows WHERE row_id=NEW.row_id);
                END;
                CREATE TRIGGER IF NOT EXISTS localization_rows_validation_update_replace
                BEFORE UPDATE OF row_id ON localization_generation_rows BEGIN
                  DELETE FROM localization_generation_validation
                  WHERE (revision,generation) IN
                    (SELECT revision,generation FROM localization_generation_rows WHERE row_id=NEW.row_id);
                END;
                """;
            replacement.ExecuteNonQuery();
        }
        // A receipt belongs to a validated SQLite snapshot. Any subsequent write to
        // that generation revokes it in the same transaction, including external writers.
        foreach (var table in new[] { "localization_generation_rows", "localization_generation_batches", "localization_generations" })
        foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var trigger = connection.CreateCommand();
            trigger.Transaction = transaction;
            var old = action == "INSERT" ? string.Empty :
                "DELETE FROM localization_generation_validation WHERE revision=OLD.revision AND generation=OLD.generation;";
            var next = action == "DELETE" ? string.Empty :
                "DELETE FROM localization_generation_validation WHERE revision=NEW.revision AND generation=NEW.generation;";
            trigger.CommandText = $"CREATE TRIGGER IF NOT EXISTS {table}_validation_{action.ToLowerInvariant()} " +
                $"AFTER {action} ON {table} BEGIN {old}{next} END;";
            trigger.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private string? ReadLocalizationValidation(SqliteTransaction transaction, string generation,
        string recordVersion, string searchVersion)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT token FROM localization_generation_validation
            WHERE revision=$r AND generation=$g AND record_version=$v AND search_version=$s AND contract=$c
            """;
        BindGroup(command, generation);
        command.Parameters.AddWithValue("$v", recordVersion);
        command.Parameters.AddWithValue("$s", searchVersion);
        command.Parameters.AddWithValue("$c", LocalizationValidationContract);
        return command.ExecuteScalar() as string;
    }

    private void SaveLocalizationValidation(SqliteTransaction transaction, string generation,
        string recordVersion, string searchVersion, string validationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO localization_generation_validation VALUES($r,$g,$v,$s,$c,$t)";
        BindGroup(command, generation);
        command.Parameters.AddWithValue("$v", recordVersion);
        command.Parameters.AddWithValue("$s", searchVersion);
        command.Parameters.AddWithValue("$c", LocalizationValidationContract);
        command.Parameters.AddWithValue("$t", validationToken);
        command.ExecuteNonQuery();
    }

    private void RequireLocalizationValidation(SqliteTransaction transaction, string generation, string validationToken)
    {
        if (localizationReadValidations.TryGetValue(validationToken, out var local))
        {
            var stamp = ReadLocalizationChangeStamp(transaction);
            if (local.Generation != generation || local.External != stamp.External || local.Local != stamp.Local)
                throw new InvalidDataException("The localization generation changed after validation.");
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM localization_generation_validation
            WHERE revision=$r AND generation=$g AND contract=$c AND token=$t LIMIT 1
            """;
        BindGroup(command, generation);
        command.Parameters.AddWithValue("$c", LocalizationValidationContract);
        command.Parameters.AddWithValue("$t", validationToken);
        if (command.ExecuteScalar() is null)
            throw new InvalidDataException("The localization generation changed after validation.");
    }

    private (long External, long Local) ReadLocalizationChangeStamp(SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA data_version";
        long external = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT total_changes()";
        return (external, Convert.ToInt64(command.ExecuteScalar()));
    }
}
