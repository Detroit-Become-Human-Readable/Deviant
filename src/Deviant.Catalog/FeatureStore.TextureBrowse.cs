using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Deviant.Catalog;

public sealed class TextureBrowseEvidenceException(IReadOnlyList<ResourceIdentity> identities, long? generation = null)
    : IOException("Some texture index entries need repair.")
{
    public IReadOnlyList<ResourceIdentity> Identities { get; } = identities;
    public long? Generation { get; } = generation;
}

public sealed partial class FeatureStore
{
    public long CommitNamedTextureProjectionCatalog(string profile, SourceRevision sourceRevision, ResourceIdentity catalog,
        string tableSha256, IReadOnlyList<ResourceIdentity> members, string label, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 4096) throw new ArgumentOutOfRangeException(nameof(label));
        return CommitTextureProjectionCatalogCore(profile, sourceRevision, catalog, tableSha256, members, label, token);
    }

    public TextureBrowseReadLease AcquireTextureBrowseReadLease(string profile, SourceRevision sourceRevision,
        long? generation = null, CancellationToken token = default,
        Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator = null,
        bool rebindUnpublished = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentNullException.ThrowIfNull(sourceRevision);
        if (rebindUnpublished && generation is not null) throw new ArgumentException("An explicit generation cannot be replaced.", nameof(generation));
        if (SourceIdentity.DerivedCacheKey(sourceRevision.IndexContentId, sourceRevision.PhysicalSourceStamp) != revision)
            throw new ArchiveException("TextureBrowseSourceMismatch", "The texture index belongs to a different archive revision.");
        long published = WithTextureBrowseConnection(token, (read, transaction, cancellation) =>
            ValidateTextureBrowseGeneration(read, transaction, profile, generation, cancellation));
        lock (textureReadLifetimeGate)
        {
            ObjectDisposedException.ThrowIf(textureReadDisposed, this);
            var lease = new TextureBrowseReadLease(this, new(sourceRevision, profile, published), evidenceValidator, rebindUnpublished);
            textureBrowseLeases.Add(lease);
            return lease;
        }
    }

    private T WithTextureBrowseConnection<T>(CancellationToken token,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> action)
    {
        SqliteConnection read;
        CancellationTokenSource own;
        lock (textureReadLifetimeGate)
        {
            ObjectDisposedException.ThrowIf(textureReadDisposed, this);
            own = CancellationTokenSource.CreateLinkedTokenSource(token, textureReadLifetime.Token);
            read = new(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            activeTextureReadConnections.Add(read);
        }
        try
        {
            own.Token.ThrowIfCancellationRequested();
            read.Open();
            read.CreateFunction<string, string, bool>("deviant_contains",
                static (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
            return CatalogService.ExecuteInterruptible(read, own.Token, () =>
            {
                using (var pragmas = read.CreateCommand())
                {
                    pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
                    pragmas.ExecuteNonQuery();
                }
                using var transaction = read.BeginTransaction(deferred: true);
                var result = action(read, transaction, own.Token);
                own.Token.ThrowIfCancellationRequested();
                transaction.Commit();
                return result;
            });
        }
        finally
        {
            ReleaseTextureReadConnection(read);
            own.Dispose();
        }
    }

    private long ValidateTextureBrowseGeneration(SqliteConnection read, SqliteTransaction transaction,
        string profile, long? generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var head = ReadTextureCommand(read, transaction,
            "SELECT generation,minimum_generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile " +
            "AND EXISTS(SELECT 1 FROM texture_projection_alias_migrations WHERE version=1) " +
            "AND NOT EXISTS(SELECT 1 FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile)", revision, profile);
        using var rows = head.ExecuteReader();
        if (!rows.Read() || (generation is { } requested && (requested < rows.GetInt64(1) || requested > rows.GetInt64(0))))
            throw new ArchiveException("TextureBrowseGenerationUnavailable", "The texture index has changed. Refresh the page.");
        return generation ?? rows.GetInt64(0);
    }

    private TextureBrowseToken ResolveTextureBrowsePin(SqliteConnection read, SqliteTransaction transaction,
        TextureBrowseToken pin, bool rebindUnpublished, CancellationToken token)
    {
        try { ValidateTextureBrowseGeneration(read, transaction, pin.Profile, pin.Generation, token); return pin; }
        catch (ArchiveException error) when (rebindUnpublished && error.Code == "TextureBrowseGenerationUnavailable")
        { return pin with { Generation = ValidateTextureBrowseGeneration(read, transaction, pin.Profile, null, token) }; }
    }

    private TextureBrowseResponse ReadTextureBrowse(TextureBrowseToken pin, TextureDiscoveryQuery query,
        Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator,
        CancellationToken token, bool rebindUnpublished = false)
        => WithTextureBrowseConnection(token, (read, transaction, cancellation) => ReadTextureBrowseSnapshot(
            ResolveTextureBrowsePin(read, transaction, pin, rebindUnpublished, cancellation), query,
            evidenceValidator, read, transaction, cancellation));

    private TextureBrowseResponse? LocateTextureBrowse(TextureBrowseToken pin, ResourceIdentity identity, TextureDiscoveryQuery query,
        Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator,
        CancellationToken token, bool rebindUnpublished)
        => WithTextureBrowseConnection<TextureBrowseResponse?>(token, (read, transaction, cancellation) =>
        {
            pin = ResolveTextureBrowsePin(read, transaction, pin, rebindUnpublished, cancellation);
            var offset = FindTextureBrowseOffsetSnapshot(pin, identity, query, read, transaction);
            return offset is null ? null : ReadTextureBrowseSnapshot(pin, query with { Offset = offset.Value },
                evidenceValidator, read, transaction, cancellation);
        });

    private TextureBrowseResponse ReadTextureBrowseSnapshot(TextureBrowseToken pin, TextureDiscoveryQuery query,
        Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator,
        SqliteConnection read, SqliteTransaction transaction, CancellationToken cancellation)
    {
        AssetLoadDiagnostics.Counters("TextureBrowser.Store.BrowseSnapshot", pin.Profile,
            new Dictionary<string, long> { ["Generation"] = pin.Generation });
        cancellation.ThrowIfCancellationRequested();
        long total;
        using (var count = TextureFilter(pin.Profile, pin.Generation, query, "COUNT(*)", false, transaction, read))
            total = Convert.ToInt64(count.ExecuteScalar());
        var entries = new List<TextureDiscoveryEntry>();
        using (var page = TextureFilter(pin.Profile, pin.Generation, query, TexturePageColumns(false), false, transaction, read))
        {
            page.CommandText += " ORDER BY " + TextureOrder(query) + " LIMIT $limit OFFSET $offset";
            page.Parameters.AddWithValue("$limit", query.Limit); page.Parameters.AddWithValue("$offset", query.Offset);
            using var rows = page.ExecuteReader();
            while (rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = JsonSerializer.Deserialize<TextureDiscoveryEntry>(rows.GetString(0))!;
                entries.Add(entry with
                {
                    Alias = rows.IsDBNull(1) ? null : rows.GetString(1),
                    CatalogKeys = entry.CatalogKeys.Concat(JsonSerializer.Deserialize<string[]>(rows.GetString(2)) ?? [])
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                });
            }
        }
        if (entries.Count > 0)
        {
            var checkpoints = new Dictionary<ResourceIdentity, TextureProjectionOwnerCheckpoint?>();
            bool Valid(TextureEvidence evidence)
            {
                if (!checkpoints.TryGetValue(evidence.Source, out var checkpoint))
                {
                    using var owner = ReadTextureCommand(read, transaction,
                        "SELECT payload FROM texture_projection_owners WHERE revision=$revision AND profile=$profile AND identity=$identity", revision, pin.Profile);
                    owner.Parameters.AddWithValue("$identity", evidence.Source.Key);
                    if (owner.ExecuteScalar() is string json && json.Length <= 16 * 1024)
                    {
                        try { checkpoint = JsonSerializer.Deserialize<TextureProjectionOwnerCheckpoint>(json); }
                        catch (JsonException) { checkpoint = null; }
                    }
                    checkpoints[evidence.Source] = checkpoint;
                }
                return evidenceValidator!(evidence, checkpoint);
            }
            var invalid = InvalidTextureProjectionRows(pin.Profile, pin.Generation, entries.Select(entry => entry.Identity).ToArray(),
                evidenceValidator is null ? null : Valid, false, cancellation, read, transaction);
            if (invalid.Count > 0) throw new TextureBrowseEvidenceException(entries.Where(entry => invalid.Contains(entry.Identity.Key))
                .Select(entry => entry.Identity).ToArray(), pin.Generation);
        }
        var catalogs = new List<TextureContextOption>();
        using (var choices = ReadTextureCommand(read, transaction,
            "SELECT c.catalog,c.label FROM texture_projection_catalogs c WHERE c.revision=$revision AND c.profile=$profile " +
            "AND c.generation=(SELECT MAX(v.generation) FROM texture_projection_catalogs v WHERE v.revision=c.revision " +
            "AND v.profile=c.profile AND v.catalog=c.catalog AND v.generation<=$generation) ORDER BY c.label,c.catalog", revision, pin.Profile))
        {
            choices.Parameters.AddWithValue("$generation", pin.Generation);
            using var rows = choices.ExecuteReader();
            while (rows.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                catalogs.Add(new(rows.GetString(0), rows.IsDBNull(1) ? "Catalog " + IdentityOrdinal(rows.GetString(0)) : rows.GetString(1)));
            }
        }
        var options = ReadTextureProjectionOptions(read, transaction, pin.Profile, pin.Generation, false, cancellation)
            with { Catalogs = catalogs };
        using var coverage = ReadTextureCommand(read, transaction, TextureHistoricalLatest +
            "SELECT COUNT(*),COALESCE(SUM(has_metadata),0),COALESCE(SUM(name_kind IN(0,1)),0)," +
            "COALESCE(SUM(name_kind=2),0),COALESCE(SUM(name_kind=3),0),COALESCE(SUM(name_kind=4),0)," +
            "COALESCE(SUM(alias IS NOT NULL),0),COALESCE(SUM(has_conflict),0)," +
            "COALESCE(SUM(kind=0),0),COALESCE(SUM(kind=1),0),COALESCE(SUM(kind=2),0)," +
            "(SELECT COUNT(DISTINCT l.link_key) FROM latest p JOIN texture_projection_links l " +
            "ON l.revision=p.revision AND l.profile=p.profile AND l.identity=p.identity AND l.generation=" +
            HistoricalTextureDataGeneration + " WHERE l.relation=3) FROM latest", revision, pin.Profile);
        coverage.Parameters.AddWithValue("$generation", pin.Generation);
        using var values = coverage.ExecuteReader(); values.Read();
        var indexed = new TextureBrowseIndexCoverage(values.GetInt64(0), values.GetInt64(1), values.GetInt64(2), values.GetInt64(3),
            values.GetInt64(4), values.GetInt64(5), values.GetInt64(6), values.GetInt64(7), values.GetInt64(8), values.GetInt64(9),
            values.GetInt64(10), values.GetInt64(11));
        return new(pin, new(entries, total, query.Offset), options, indexed);
    }

    private long? FindTextureBrowseOffset(TextureBrowseToken pin, ResourceIdentity identity, TextureDiscoveryQuery query,
        CancellationToken token) => WithTextureBrowseConnection<long?>(token, (read, transaction, cancellation) =>
    {
        ValidateTextureBrowseGeneration(read, transaction, pin.Profile, pin.Generation, cancellation);
        return FindTextureBrowseOffsetSnapshot(pin, identity, query, read, transaction);
    });

    private long? FindTextureBrowseOffsetSnapshot(TextureBrowseToken pin, ResourceIdentity identity, TextureDiscoveryQuery query,
        SqliteConnection read, SqliteTransaction transaction)
    {
        using var locate = TextureFilter(pin.Profile, pin.Generation, query,
            "p.identity,ROW_NUMBER() OVER(ORDER BY " + TextureOrder(query) + ")-1 AS position", false, transaction, read);
        locate.CommandText = "SELECT position FROM (" + locate.CommandText + ") WHERE identity=$identity";
        locate.Parameters.AddWithValue("$identity", identity.Key);
        var position = locate.ExecuteScalar();
        return position is null or DBNull ? null : Convert.ToInt64(position) / query.Limit * query.Limit;
    }

    public sealed class TextureBrowseReadLease : IDisposable
    {
        private readonly FeatureStore owner;
        private readonly Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator;
        private readonly object gate = new(), drainGate = new(), generationGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly HashSet<Task> reads = [];
        private bool disposed;
        private readonly bool rebindUnpublished;
        private bool published;
        internal TextureBrowseReadLease(FeatureStore owner, TextureBrowseToken token,
            Func<TextureEvidence, TextureProjectionOwnerCheckpoint?, bool>? evidenceValidator, bool rebindUnpublished)
        { this.owner = owner; Token = token; this.evidenceValidator = evidenceValidator; this.rebindUnpublished = rebindUnpublished; }
        public TextureBrowseToken Token { get; private set; }
        public Task<TextureBrowseResponse> ReadAsync(TextureDiscoveryQuery query, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
            return StartRead(cancellation => Publish(owner.ReadTextureBrowse(Token, query, evidenceValidator,
                cancellation, rebindUnpublished && !published)), token);
        }
        public Task<long?> FindPageOffsetAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(query);
            if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
            if (identity.IndexContentId != Token.Revision.IndexContentId) throw new ArgumentException("The texture belongs to a different archive.", nameof(identity));
            return StartRead(cancellation => owner.FindTextureBrowseOffset(Token, identity, query, cancellation), token);
        }
        public Task<TextureBrowseResponse?> LocateAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(query);
            if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
            if (identity.IndexContentId != Token.Revision.IndexContentId) throw new ArgumentException("The texture belongs to a different archive.", nameof(identity));
            return StartRead(cancellation =>
            {
                var response = owner.LocateTextureBrowse(Token, identity, query, evidenceValidator,
                    cancellation, rebindUnpublished && !published);
                return response is null ? null : Publish(response);
            }, token);
        }
        private TextureBrowseResponse Publish(TextureBrowseResponse response)
        { Token = response.Token; published = true; return response; }
        private Task<T> StartRead<T>(Func<CancellationToken, T> read, CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var task = Task.Run(() =>
                {
                    using var own = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                    if (!rebindUnpublished) return read(own.Token);
                    lock (generationGate) { own.Token.ThrowIfCancellationRequested(); return read(own.Token); }
                });
                reads.Add(task);
                _ = task.ContinueWith(completed => { lock (gate) reads.Remove(completed); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }
        public void Dispose()
        {
            lock (drainGate)
            {
                Task[] active;
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    cancellation.Cancel();
                    active = reads.ToArray();
                }
                try { Task.WhenAll(active).GetAwaiter().GetResult(); }
                catch { /* Read failures remain on the callers' tasks. */ }
                cancellation.Dispose();
                lock (owner.textureReadLifetimeGate) owner.textureBrowseLeases.Remove(this);
            }
        }
    }
}
