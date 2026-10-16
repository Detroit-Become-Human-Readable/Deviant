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
        SqliteConnection? read = null;
        SqliteTransaction? transaction = null;
        bool registered = false;
        try
        {
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
            if (!textureSchemaReady)
            {
                using var prepared = read.CreateCommand();
                prepared.Transaction = transaction;
                prepared.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE " +
                    "(type='table' AND name='texture_projection_heads') OR (type='index' AND name='texture_history_options')";
                long preparedObjects = CatalogService.ExecuteInterruptible(read, cancellation,
                    () => Convert.ToInt64(prepared.ExecuteScalar()));
                if (preparedObjects != 2)
                    throw new ArchiveException("TextureProjectionUnavailable", "Texture projections are not prepared.");
            }
            var snapshot = ReadTextureSnapshot(read, transaction, lease.Profile, cancellation);
            lease.SetSnapshot(snapshot.Generation, snapshot.DataVersion, isCurrent: true);
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.OptionsSnapshot", lease.Profile,
                new Dictionary<string, long> { ["Generation"] = snapshot.Generation });
            var result = CatalogService.ExecuteInterruptible(read, cancellation, () =>
                ReadTextureProjectionOptions(read, transaction, lease.Profile, snapshot.Generation, current: false, cancellation));
            cancellation.ThrowIfCancellationRequested();
            transaction.Commit();
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

    private (long Generation, long DataVersion) ReadTextureSnapshot(
        SqliteConnection read, SqliteTransaction transaction, string profile, CancellationToken token)
        => CatalogService.ExecuteInterruptible(read, token, () =>
        {
            using var head = ReadTextureCommand(read, transaction,
                "SELECT generation FROM texture_projection_heads WHERE revision=$revision AND profile=$profile", revision, profile);
            var generation = Convert.ToInt64(head.ExecuteScalar() ?? 0L);
            using var version = read.CreateCommand(); version.Transaction = transaction;
            version.CommandText = "PRAGMA data_version";
            var dataVersion = Convert.ToInt64(version.ExecuteScalar() ?? 0L);
            return (generation, dataVersion);
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
        /// <summary>The generation was the published head when this read snapshot was captured.</summary>
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
