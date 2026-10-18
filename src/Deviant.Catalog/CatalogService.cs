using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Core;
using Deviant.Assets;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

/// <summary>Persists parser-normalized index rows while rebuilding volume validity from each fresh snapshot.</summary>
public static class CatalogService
{
    /// <summary>Creates an indexed query view over the verified archive snapshot without opening or writing a cache.</summary>
    public static IResourceCatalog OpenInMemory(ArchiveSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var key = SourceIdentity.DerivedCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        return new InMemoryResourceCatalog(snapshot, key, cancellationToken);
    }

    public static IResourceCatalog Open(ArchiveSnapshot snapshot, string? cacheRoot = null,
        CancellationToken cancellationToken = default) => Open(snapshot, cacheRoot, null, cancellationToken);

    public static IResourceCatalog Open(ArchiveSnapshot snapshot, string? cacheRoot,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "cache");
        cacheRoot = Path.GetFullPath(cacheRoot);
        var sourceRoot = Path.GetFullPath(snapshot.SourceRoot);
        if (IsWithin(cacheRoot, sourceRoot))
            throw new ArchiveException("UnsafeCache", "The catalog cache must be outside the selected archive directory.");
        Directory.CreateDirectory(cacheRoot);
        var storage = CacheStorageBudget.ForRoot(cacheRoot);
        if (storage.Limit == 0) return OpenInMemory(snapshot, cancellationToken);
        var key = SourceIdentity.DerivedCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var indexKey = SourceIdentity.IndexCacheKey(snapshot.IndexContentId);
        var fileKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion + "\n" + indexKey))).ToLowerInvariant();
        var indexCacheRoot = Path.Combine(cacheRoot, "raw-index-v1");
        Directory.CreateDirectory(indexCacheRoot);
        var dbPath = Path.Combine(indexCacheRoot, "catalog-" + fileKey + ".sqlite3");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, Vfs = storage.Vfs }.ToString());
        try
        {
            connection.Open();
            ExecuteInterruptible(connection, cancellationToken, () =>
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
                return true;
            });
            ExecuteInterruptible(connection, cancellationToken, () =>
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS index_rows (
                        parser_version TEXT NOT NULL, index_content_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                        type INTEGER NOT NULL, flags INTEGER NOT NULL, id INTEGER NOT NULL, package INTEGER NOT NULL,
                        offset INTEGER NOT NULL, stored_size INTEGER NOT NULL, alternative_size INTEGER NOT NULL,
                        volume_name TEXT NOT NULL,
                        PRIMARY KEY(parser_version,index_content_id,ordinal));
                    CREATE INDEX IF NOT EXISTS index_rows_type ON index_rows(parser_version,index_content_id,type,ordinal);
                    CREATE INDEX IF NOT EXISTS index_rows_id ON index_rows(parser_version,index_content_id,id,ordinal);
                    CREATE INDEX IF NOT EXISTS index_rows_package ON index_rows(parser_version,index_content_id,package,ordinal);
                    CREATE TABLE IF NOT EXISTS cache_meta (
                        parser_version TEXT NOT NULL, index_content_id TEXT NOT NULL,
                        last_physical_source_stamp TEXT NOT NULL,
                        PRIMARY KEY(parser_version,index_content_id));
                    """;
                schema.ExecuteNonQuery();
                return true;
            });

            progress?.Report(new("Checking catalog cache", 0, snapshot.Records.Count));
            var reused = HasRows(connection, snapshot.IndexContentId, cancellationToken) &&
                         EnsureRowsMatch(connection, snapshot, cancellationToken, progress);
            if (!reused)
                StoreRows(connection, snapshot, cancellationToken, progress);
            StoreFreshPhysicalStamp(connection, snapshot, cancellationToken);
            progress?.Report(new(reused ? "Catalog cache ready" : "Catalog index ready", snapshot.Records.Count, snapshot.Records.Count));
            // Keep the reader's fresh snapshot authoritative for validity; the SQLite cache is used for indexed queries.
            return new SqliteResourceCatalog(connection, snapshot, key, reused, snapshot.Records);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            (error is SqliteException { SqliteErrorCode: 13 } || error is IOException { InnerException: SqliteException { SqliteErrorCode: 13 } }))
        {
            connection.Dispose();
            AssetLoadDiagnostics.Counters("CatalogMemoryFallback", snapshot.IndexContentId,
                new Dictionary<string, long> { ["StorageLimitBytes"] = storage.Limit, ["Rows"] = snapshot.Records.Count });
            progress?.Report(new("Catalog index ready", snapshot.Records.Count, snapshot.Records.Count));
            return OpenInMemory(snapshot, cancellationToken);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool HasRows(SqliteConnection connection, string contentId, CancellationToken token) =>
        ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM index_rows WHERE parser_version=$parser AND index_content_id=$index);";
            command.Parameters.AddWithValue("$parser", IndexLayout.ParserVersion);
            command.Parameters.AddWithValue("$index", contentId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        });

    private static void StoreRows(SqliteConnection connection, ArchiveSnapshot snapshot, CancellationToken token,
        IProgress<ArchiveProgress>? progress) => ExecuteInterruptible(connection, token, () =>
    {
        using var transaction = connection.BeginTransaction();
        using var buildTiming = AssetLoadDiagnostics.Measure("CatalogIndexBuild", snapshot.IndexContentId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO index_rows(parser_version,index_content_id,ordinal,type,flags,id,package,offset,stored_size,alternative_size,volume_name)
            VALUES($parser,$index,$ordinal,$type,$flags,$id,$package,$offset,$size,$alternative,$volume);
            """;
        var parser = command.Parameters.Add("$parser", SqliteType.Text);
        var index = command.Parameters.Add("$index", SqliteType.Text);
        var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var type = command.Parameters.Add("$type", SqliteType.Integer);
        var flags = command.Parameters.Add("$flags", SqliteType.Integer);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var package = command.Parameters.Add("$package", SqliteType.Integer);
        var offset = command.Parameters.Add("$offset", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var alternative = command.Parameters.Add("$alternative", SqliteType.Integer);
        var volume = command.Parameters.Add("$volume", SqliteType.Text);
        parser.Value = IndexLayout.ParserVersion;
        index.Value = snapshot.IndexContentId;
        progress?.Report(new("Building catalog index", 0, snapshot.Records.Count));
        var completed = 0L;
        using (AssetLoadDiagnostics.Measure("CatalogIndexRows", snapshot.IndexContentId))
        {
        foreach (var row in snapshot.Records)
        {
            token.ThrowIfCancellationRequested();
            ordinal.Value = row.Ordinal; type.Value = (long)row.Type; flags.Value = (long)row.Flags; id.Value = (long)row.Id;
            package.Value = (long)row.Package; offset.Value = row.Offset; size.Value = row.StoredSize;
            alternative.Value = (long)row.AlternativeSize; volume.Value = row.VolumeName;
            command.ExecuteNonQuery();
            completed++;
            if ((completed & 8191) == 0) progress?.Report(new("Building catalog index", completed, snapshot.Records.Count));
        }
        }
        using (AssetLoadDiagnostics.Measure("CatalogIndexCommit", snapshot.IndexContentId)) transaction.Commit();
        progress?.Report(new("Building catalog index", completed, snapshot.Records.Count));
        return true;
    });

    private static bool EnsureRowsMatch(SqliteConnection connection, ArchiveSnapshot snapshot, CancellationToken token,
        IProgress<ArchiveProgress>? progress)
    {
        var matches = true;
        matches = ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ordinal,type,flags,id,package,offset,stored_size,alternative_size,volume_name FROM index_rows WHERE parser_version=$parser AND index_content_id=$index ORDER BY ordinal;";
            command.Parameters.AddWithValue("$parser", IndexLayout.ParserVersion);
            command.Parameters.AddWithValue("$index", snapshot.IndexContentId);
            using var reader = command.ExecuteReader();
            var position = 0;
            progress?.Report(new("Validating cached catalog", 0, snapshot.Records.Count));
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (position >= snapshot.Records.Count) { matches = false; break; }
                var raw = snapshot.Records[position++];
                if (reader.GetInt64(0) != raw.Ordinal || reader.GetInt64(1) != raw.Type || reader.GetInt64(2) != raw.Flags ||
                    reader.GetInt64(3) != raw.Id || reader.GetInt64(4) != raw.Package || reader.GetInt64(5) != raw.Offset ||
                    reader.GetInt64(6) != raw.StoredSize || reader.GetInt64(7) != raw.AlternativeSize || reader.GetString(8) != raw.VolumeName)
                { matches = false; break; }
                if ((position & 8191) == 0) progress?.Report(new("Validating cached catalog", position, snapshot.Records.Count));
            }
            if (position != snapshot.Records.Count) matches = false;
            progress?.Report(new("Validating cached catalog", position, snapshot.Records.Count));
            return matches;
        });
        if (matches) return true;
        ExecuteInterruptible(connection, token, () =>
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM index_rows WHERE parser_version=$parser AND index_content_id=$index;";
            delete.Parameters.AddWithValue("$parser", IndexLayout.ParserVersion);
            delete.Parameters.AddWithValue("$index", snapshot.IndexContentId);
            delete.ExecuteNonQuery();
            return true;
        });
        return false;
    }

    private static void StoreFreshPhysicalStamp(SqliteConnection connection, ArchiveSnapshot snapshot, CancellationToken token) =>
        ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO cache_meta(parser_version,index_content_id,last_physical_source_stamp) VALUES($parser,$index,$stamp);";
            command.Parameters.AddWithValue("$parser", IndexLayout.ParserVersion);
            command.Parameters.AddWithValue("$index", snapshot.IndexContentId);
            command.Parameters.AddWithValue("$stamp", snapshot.PhysicalSourceStamp);
            command.ExecuteNonQuery();
            return true;
        });

    internal static T ExecuteInterruptible<T>(SqliteConnection connection, CancellationToken token, Func<T> operation)
    {
        CacheStorageBudget.ClearFailure();
        token.ThrowIfCancellationRequested();
        using var registration = token.CanBeCanceled
            ? token.Register(static state =>
            {
                var sqliteConnection = (SqliteConnection)state!;
                SQLitePCL.raw.sqlite3_interrupt(sqliteConnection.Handle);
            }, connection)
            : default;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = operation();
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException ex) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("SQLite operation was interrupted.", ex, token);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 13)
        {
            throw CacheStorageBudget.StorageFailure(ex);
        }
    }

    private static bool IsWithin(string path, string root)
    {
        return DirectoryBoundary.IsWithin(path, root);
    }
}

internal sealed class SqliteResourceCatalog(SqliteConnection connection, ArchiveSnapshot snapshot,
    string cacheKey, bool reusedIndexRows, IReadOnlyList<ResourceRecord> records) : IResourceCatalog
{
    private readonly object gate = new();
    private bool disposed;
    public ArchiveSnapshot Snapshot { get; } = snapshot;
    public string CacheKey { get; } = cacheKey;
    public bool ReusedIndexRows { get; } = reusedIndexRows;

    public long Count(ResourceQuery query) => Count(query, CancellationToken.None);

    public long Count(ResourceQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Validate(query);
            return CatalogService.ExecuteInterruptible(connection, cancellationToken, () =>
            {
                var (where, parameters) = BuildWhere(query);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM index_rows WHERE parser_version=$parser AND index_content_id=$index" + where + ";";
                AddBase(command, Snapshot.IndexContentId);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
        }
    }

    public IReadOnlyList<ResourceRecord> Query(ResourceQuery query, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            Validate(query);
            return CatalogService.ExecuteInterruptible(connection, cancellationToken, () =>
            {
                var (where, parameters) = BuildWhere(query);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ordinal FROM index_rows WHERE parser_version=$parser AND index_content_id=$index" + where + " ORDER BY ordinal LIMIT $limit OFFSET $offset;";
                AddBase(command, Snapshot.IndexContentId);
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.Parameters.AddWithValue("$limit", query.Limit);
                command.Parameters.AddWithValue("$offset", query.Offset);
                using var reader = command.ExecuteReader();
                var result = new List<ResourceRecord>();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ordinal = reader.GetInt64(0);
                    if (ordinal >= 0 && ordinal < records.Count) result.Add(records[(int)ordinal]);
                }
                return result;
            });
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            connection.Dispose();
        }
    }

    private static void AddBase(SqliteCommand command, string contentId)
    {
        command.Parameters.AddWithValue("$parser", IndexLayout.ParserVersion);
        command.Parameters.AddWithValue("$index", contentId);
    }

    private static void Validate(ResourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query), "Query offset must be zero or greater.");
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query), "Query limit must be between 1 and 10000.");
    }

    private static (string Where, List<(string Name, object Value)> Parameters) BuildWhere(ResourceQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        AddExact(query.Type, "$type", "type", clauses, parameters);
        AddExact(query.Id, "$id", "id", clauses, parameters);
        AddExact(query.Package, "$package", "package", clauses, parameters);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim();
            var searchable = text;
            var recognizedKind = "";
            var colon = text.IndexOf(':');
            if (colon > 0 && new[] { "id", "type", "package", "volume", "ordinal" }.Contains(text[..colon], StringComparer.OrdinalIgnoreCase))
            {
                recognizedKind = text[..colon].ToLowerInvariant();
                searchable = text[(colon + 1)..].Trim();
            }
            var parts = new List<string>();
            if ((recognizedKind is "id" or "type" or "package") && TryUInt(searchable, out var exact))
            {
                var col = recognizedKind;
                parts.Add(col + "=$searchNumeric");
                parameters.Add(("$searchNumeric", (long)exact));
            }
            else if (recognizedKind == "ordinal" && long.TryParse(searchable, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            {
                parts.Add("ordinal=$searchOrdinal");
                parameters.Add(("$searchOrdinal", ordinal));
            }
            else if (recognizedKind == "volume")
            {
                parts.Add("volume_name LIKE $searchText ESCAPE '\\'");
                parameters.Add(("$searchText", "%" + EscapeLike(searchable) + "%"));
            }
            else
            {
                if (TryUInt(searchable, out var number))
                {
                    parts.Add("(id=$searchNumeric OR type=$searchNumeric OR package=$searchNumeric)");
                    parameters.Add(("$searchNumeric", (long)number));
                }
                var like = "$searchText";
                parts.Add("(volume_name LIKE " + like + " ESCAPE '\\' OR CAST(id AS TEXT) LIKE " + like + " ESCAPE '\\' OR printf('%08X',id) LIKE " + like + " ESCAPE '\\' OR CAST(type AS TEXT) LIKE " + like + " ESCAPE '\\' OR printf('%08X',type) LIKE " + like + " ESCAPE '\\' OR CAST(package AS TEXT) LIKE " + like + " ESCAPE '\\' OR CAST(ordinal AS TEXT) LIKE " + like + " ESCAPE '\\' OR ('id:'||CAST(id AS TEXT)) LIKE " + like + " ESCAPE '\\' OR ('type:'||CAST(type AS TEXT)) LIKE " + like + " ESCAPE '\\' OR ('package:'||CAST(package AS TEXT)) LIKE " + like + " ESCAPE '\\' OR ('volume:'||volume_name) LIKE " + like + " ESCAPE '\\')");
                parameters.Add(("$searchText", "%" + EscapeLike(text) + "%"));
            }
            clauses.Add("(" + string.Join(" OR ", parts) + ")");
        }
        return (clauses.Count == 0 ? "" : " AND " + string.Join(" AND ", clauses), parameters);
    }

    private static void AddExact(uint? value, string parameter, string column, List<string> clauses,
        List<(string Name, object Value)> parameters)
    {
        if (value is null) return;
        clauses.Add(column + "=$" + parameter.TrimStart('$'));
        parameters.Add((parameter, (long)value.Value));
    }

    private static bool TryUInt(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (text.Any(c => (c is >= 'A' and <= 'F') || (c is >= 'a' and <= 'f')))
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(SqliteResourceCatalog));
    }
}
