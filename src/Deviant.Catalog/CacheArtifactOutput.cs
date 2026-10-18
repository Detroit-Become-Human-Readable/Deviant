namespace Deviant.Catalog;

/// <summary>An owned cache output whose writes participate in the shared storage budget.</summary>
public sealed class CacheArtifactOutput
{
    private readonly string root;

    internal CacheArtifactOutput(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        Path = System.IO.Path.GetFullPath(path);
        var relative = System.IO.Path.GetRelativePath(this.root, Path);
        if (relative == "." || relative == ".." || System.IO.Path.IsPathRooted(relative)
            || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The artifact output must be inside the cache folder.");
    }

    public string Path { get; }

    /// <summary>Creates a new readable, seekable output. Existing files are preserved.</summary>
    public Stream OpenWrite()
    {
        // Validate once before obtaining the pinned output handle, rather than
        // walking the filesystem again for every bounded write.
        if (!Deviant.Core.DirectoryBoundary.IsWithin(Path, root))
            throw new IOException("The artifact output must be inside the cache folder.");
        for (var ancestor = System.IO.Path.GetDirectoryName(Path); ancestor is not null;
             ancestor = System.IO.Path.GetDirectoryName(ancestor))
        {
            if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache folders cannot contain symbolic links or junctions.");
        }
        if (File.Exists(Path) && (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cache output files cannot be symbolic links.");

        var budget = CacheStorageBudget.ForRoot(root);
        CacheStorageBudget.ClearFailure();
        using var lease = budget.Acquire(Path, 0, 0)
            ?? throw CacheStorageBudget.StorageFailure(new IOException("The artifact output could not be created."));
        FileStream? file = null;
        try
        {
            file = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            lease.Complete(file.Length);
            return new GuardedArtifactStream(file, budget, Path);
        }
        catch { file?.Dispose(); throw; }
    }

    private sealed class GuardedArtifactStream(FileStream file, CacheStorageBudget budget, string path) : Stream
    {
        private const int ChunkBytes = 128 * 1024;
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool disposed;

        public override bool CanRead => !Volatile.Read(ref disposed);
        public override bool CanSeek => !Volatile.Read(ref disposed);
        public override bool CanWrite => !Volatile.Read(ref disposed);
        public override long Length
        {
            get
            {
                gate.Wait();
                try { EnsureOpen(); return file.Length; }
                finally { gate.Release(); }
            }
        }
        public override long Position
        {
            get
            {
                gate.Wait();
                try { EnsureOpen(); return file.Position; }
                finally { gate.Release(); }
            }
            set
            {
                gate.Wait();
                try { EnsureOpen(); file.Position = value; }
                finally { gate.Release(); }
            }
        }

        private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);

        // This method and SetLength hold the thread-affine budget lease only
        // around synchronous, unbuffered file I/O. No root mutex crosses await.
        private void WriteChunk(ReadOnlySpan<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var current = file.Length;
            var requested = Math.Max(current, checked(file.Position + buffer.Length));
            CacheStorageBudget.ClearFailure();
            using var lease = budget.Acquire(path, current, requested)
                ?? throw CacheStorageBudget.StorageFailure(new IOException("The artifact output could not grow."));
            try { token.ThrowIfCancellationRequested(); file.Write(buffer); }
            finally { lease.Complete(file.Length); }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            gate.Wait();
            try
            {
                EnsureOpen();
                while (!buffer.IsEmpty)
                {
                    var count = Math.Min(ChunkBytes, buffer.Length);
                    WriteChunk(buffer[..count], CancellationToken.None);
                    buffer = buffer[count..];
                }
            }
            finally { gate.Release(); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void WriteByte(byte value)
        {
            Span<byte> one = stackalloc byte[1];
            one[0] = value;
            Write(one);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureOpen();
                while (!buffer.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(ChunkBytes, buffer.Length);
                    var chunk = buffer[..count];
                    // The worker owns the entire acquire/write/reconcile/release
                    // sequence. The caller holds only the per-stream gate.
                    await Task.Run(() => WriteChunk(chunk.Span, cancellationToken), cancellationToken).ConfigureAwait(false);
                    buffer = buffer[count..];
                }
            }
            finally { gate.Release(); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void SetLength(long value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            gate.Wait();
            try
            {
                EnsureOpen();
                CacheStorageBudget.ClearFailure();
                using var lease = budget.Acquire(path, file.Length, value)
                    ?? throw CacheStorageBudget.StorageFailure(new IOException("The artifact output could not grow."));
                try { file.SetLength(value); }
                finally { lease.Complete(file.Length); }
            }
            finally { gate.Release(); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            gate.Wait();
            try { EnsureOpen(); return file.Seek(offset, origin); }
            finally { gate.Release(); }
        }

        public override int Read(Span<byte> buffer)
        {
            gate.Wait();
            try { EnsureOpen(); return file.Read(buffer); }
            finally { gate.Release(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int ReadByte()
        {
            gate.Wait();
            try { EnsureOpen(); return file.ReadByte(); }
            finally { gate.Release(); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { EnsureOpen(); return await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush()
        {
            gate.Wait();
            try { EnsureOpen(); file.Flush(); }
            finally { gate.Release(); }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { EnsureOpen(); await file.FlushAsync(cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                gate.Wait();
                try
                {
                    if (!disposed) { disposed = true; file.Dispose(); }
                }
                finally { gate.Release(); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!disposed) { disposed = true; await file.DisposeAsync().ConfigureAwait(false); }
            }
            finally { gate.Release(); }
            GC.SuppressFinalize(this);
        }
    }
}
