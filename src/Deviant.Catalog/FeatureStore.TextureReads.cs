using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private readonly object textureReadLifetimeGate = new();
    private readonly CancellationTokenSource textureReadLifetime = new();
    private readonly HashSet<TextureProjectionReadLease> textureReadLeases = [];
    private readonly HashSet<SqliteConnection> activeTextureReadConnections = [];
    private volatile bool textureReadDisposed;

    /// <summary>Registers a short-lived texture-facet read without waiting for a query connection.</summary>
    public TextureProjectionReadLease AcquireTextureProjectionReadLease(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        lock (textureReadLifetimeGate)
        {
            ObjectDisposedException.ThrowIf(textureReadDisposed, this);
            var lease = new TextureProjectionReadLease(this, profile, revision);
            textureReadLeases.Add(lease);
            return lease;
        }
    }

    private async Task<TextureDiscoveryFilterOptions> ReadTextureProjectionFiltersAsync(
        TextureProjectionReadLease lease, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lease.CancellationToken,
            textureReadLifetime.Token);
        var cancellation = own.Token;
        await Task.Yield();
        cancellation.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            long generation;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(textureReadDisposed, this);
                generation = CatalogService.ExecuteInterruptible(connection, cancellation, () =>
                {
                    EnsureTextureSchema();
                    using var transaction = connection.BeginTransaction(deferred: true);
                    using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", lease.Profile, transaction);
                    var value = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
                    transaction.Commit();
                    return value;
                });
            }

            try
            {
                if (generation > 0)
                    await PrepareCurrentTextureRolesAsync(lease.Profile, generation, cancellation).ConfigureAwait(false);
            }
            catch (ArchiveException error) when (error.Code == "TextureProjectionGenerationChanged")
            { continue; }

            SqliteConnection? read = null;
            SqliteTransaction? transaction = null;
            bool registered = false;
            try
            {
                lock (gate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(textureReadDisposed, this);
                    using var prepare = connection.BeginTransaction(deferred: true);
                    using var head = TextureCommand("SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", lease.Profile, prepare);
                    var writerGeneration = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
                    if (writerGeneration != generation) continue;

                    bool current = writerGeneration != 0 && UseCurrentTextureProjection(lease.Profile, writerGeneration, prepare);
                    if (!current)
                    {
                        RefreshTextureHistory(lease.Profile, prepare);
                        using var historyIndex = connection.CreateCommand(); historyIndex.Transaction = prepare;
                        historyIndex.CommandText = "CREATE INDEX IF NOT EXISTS texture_history_options ON texture_projection_history_index " +
                            "(revision,profile,width,height,format,context_key,context_label,has_owner,identity,generation,next_generation)";
                        historyIndex.ExecuteNonQuery();
                    }
                    prepare.Commit();
                }

                read = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                read.Open();
                lock (textureReadLifetimeGate)
                {
                    ObjectDisposedException.ThrowIf(textureReadDisposed, this);
                    activeTextureReadConnections.Add(read);
                    registered = true;
                }
                CatalogService.ExecuteInterruptible(read, cancellation, () =>
                {
                    using var pragmas = read.CreateCommand();
                    pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
                    pragmas.ExecuteNonQuery();
                    return true;
                });
                transaction = read.BeginTransaction(deferred: true);
                var snapshot = ReadTextureSnapshot(read, transaction, lease.Profile, cancellation);
                if (snapshot.Generation != generation || (snapshot.CurrentDirty != 0 && snapshot.HistoryDirty))
                    continue;
                lease.SetSnapshot(snapshot.Generation, snapshot.DataVersion, snapshot.CurrentDirty == 0);
                var result = CatalogService.ExecuteInterruptible(read!, cancellation, () =>
                    ReadTextureProjectionOptions(read!, transaction!, lease.Profile, generation, lease.IsCurrent, cancellation));
                transaction!.Commit();
                return result;
            }
            finally
            {
                transaction?.Dispose();
                if (read is not null)
                {
                    if (registered) ReleaseTextureReadConnection(read);
                    else read.Dispose();
                }
            }
        }
        throw new ArchiveException("TextureProjectionGenerationChanged", "Texture filter options changed while they were being read.");
    }

    private (long Generation, long DataVersion, int CurrentDirty, bool HistoryDirty) ReadTextureSnapshot(
        SqliteConnection read, SqliteTransaction transaction, string profile, CancellationToken token)
        => CatalogService.ExecuteInterruptible(read, token, () =>
        {
            using var head = ReadTextureCommand(read, transaction,
                "SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", revision, profile);
            var generation = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
            using var version = read.CreateCommand(); version.Transaction = transaction;
            version.CommandText = "PRAGMA data_version";
            var dataVersion = Convert.ToInt64(version.ExecuteScalar() ?? 0L);
            using var dirty = ReadTextureCommand(read, transaction,
                "SELECT EXISTS(SELECT 1 FROM texture_projection_current_dirty WHERE revision=$revision AND profile=$profile)", revision, profile);
            var currentDirty = Convert.ToInt32(dirty.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
            using var historyDirty = ReadTextureCommand(read, transaction,
                "SELECT EXISTS(SELECT 1 FROM texture_projection_history_dirty WHERE revision=$revision AND profile=$profile)", revision, profile);
            var historyPending = Convert.ToInt32(historyDirty.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture) != 0;
            return (generation, dataVersion, currentDirty, historyPending);
        });

    private TextureDiscoveryFilterOptions ReadTextureProjectionOptions(SqliteConnection read,
        SqliteTransaction transaction, string profile, long generation, bool current, CancellationToken token)
    {
        var dimensions = new List<TextureDimensionOption>();
        var formats = new List<string>();
        var roles = new List<string>();
        var contexts = new List<TextureContextOption>();
        Read("dimensions", row => dimensions.Add(new(row.GetInt32(0), row.GetInt32(1))));
        Read("formats", row => formats.Add(row.GetString(0)));
        Read("roles", row => roles.Add(row.GetString(0)));
        Read("contexts", row => contexts.Add(new(row.GetString(0), row.GetString(1))));
        return new(dimensions, formats, roles, contexts);

        void Read(string facet, Action<SqliteDataReader> accept)
        {
            token.ThrowIfCancellationRequested();
            using var command = TextureOptionsCommand(profile, generation, current, facet, transaction, read);
            using var rows = command.ExecuteReader();
            while (rows.Read()) { token.ThrowIfCancellationRequested(); accept(rows); }
        }
    }

    private static SqliteCommand ReadTextureCommand(SqliteConnection read, SqliteTransaction transaction,
        string sql, string sourceRevision, string profile)
    {
        var command = read.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$revision", sourceRevision);
        command.Parameters.AddWithValue("$profile", profile);
        return command;
    }

    private void ReleaseTextureReadConnection(SqliteConnection read)
    {
        lock (textureReadLifetimeGate)
        {
            activeTextureReadConnections.Remove(read);
            Monitor.PulseAll(textureReadLifetimeGate);
        }
        read.Dispose();
    }

    private void ReleaseTextureReadLease(TextureProjectionReadLease lease)
    {
        lock (textureReadLifetimeGate) textureReadLeases.Remove(lease);
    }

    private void DisposeTextureProjectionReaders()
    {
        TextureProjectionReadLease[] leases;
        lock (textureReadLifetimeGate)
        {
            if (textureReadDisposed) return;
            textureReadDisposed = true;
            textureReadLifetime.Cancel();
            foreach (var read in activeTextureReadConnections)
                SQLitePCL.raw.sqlite3_interrupt(read.Handle);
            leases = textureReadLeases.ToArray();
        }
        foreach (var lease in leases) lease.CancelAndDrain();
        lock (textureReadLifetimeGate)
        {
            while (activeTextureReadConnections.Count != 0) Monitor.Wait(textureReadLifetimeGate);
        }
        textureReadLifetime.Dispose();
    }

    public sealed class TextureProjectionReadLease : IDisposable
    {
        private readonly FeatureStore owner;
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private Task<TextureDiscoveryFilterOptions>? readTask;
        private bool disposed;
        internal TextureProjectionReadLease(FeatureStore owner, string profile, string sourceRevision)
        { this.owner = owner; Profile = profile; SourceRevision = sourceRevision; }
        public string Profile { get; }
        public string SourceRevision { get; }
        public long Generation { get; private set; }
        public long CurrentEpoch { get; private set; }
        public bool IsCurrent { get; private set; }
        internal CancellationToken CancellationToken => cancellation.Token;

        public Task<TextureDiscoveryFilterOptions> ReadFilterOptionsAsync(CancellationToken token = default)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return readTask ??= Task.Run(() => owner.ReadTextureProjectionFiltersAsync(this, token));
            }
        }

        internal void SetSnapshot(long generation, long currentEpoch, bool isCurrent)
        { Generation = generation; CurrentEpoch = currentEpoch; IsCurrent = isCurrent; }

        internal void CancelAndDrain()
        {
            Task<TextureDiscoveryFilterOptions>? active;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                cancellation.Cancel();
                active = readTask;
            }
            try { active?.GetAwaiter().GetResult(); }
            catch { }
            cancellation.Dispose();
            owner.ReleaseTextureReadLease(this);
        }

        public void Dispose() => CancelAndDrain();
    }
}
