using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private long ReadTextureProjectionHead(SqliteConnection read, SqliteTransaction transaction,
        string profile, long? requestedGeneration)
    {
        using var head = ReadTextureCommand(read, transaction,
            "SELECT generation,minimum_generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile",
            revision, profile);
        using var rows = head.ExecuteReader();
        if (!rows.Read()) throw TextureGenerationUnavailable();
        long current = rows.GetInt64(0), minimum = rows.GetInt64(1);
        if (minimum < 1 || current < minimum ||
            requestedGeneration is { } requested && (requested < minimum || requested > current))
            throw TextureGenerationUnavailable();
        return current;
    }

    private static ArchiveException TextureGenerationUnavailable() =>
        new("TextureBrowseGenerationUnavailable", "The texture index has changed. Refresh the page.");
}
