using System.ComponentModel;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    // SQLite's largest database is (2^32-2)*65536 bytes. This reserved byte
    // is beyond that limit and separate from SQLite's lock bytes at 0x40000000.
    // LockFileEx permits locking past EOF without extending the file.
    private const long TextureRetentionLockOffset = 0x0001_0000_0000_0000;
    private static readonly object textureRetentionRegistryGate = new();
    private static readonly Dictionary<string, TextureRetentionState> textureRetentionRegistry =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int pendingTextureLeaseAcquisitions;
    private sealed record TextureLeaseRegistration(TextureRetentionLease Retention, long Generation);

    private sealed class TextureRetentionState(string key)
    {
        internal readonly string Key = key;
        internal int Readers;
        internal bool Retiring;
        internal bool RetirementDisabled;
    }

    private TextureLeaseRegistration RegisterTextureBrowseLease(string profile, long? generation,
        CancellationToken token, Action<TextureLeaseRegistration> publish)
    {
        CancellationTokenSource own;
        lock (textureReadLifetimeGate)
        {
            ObjectDisposedException.ThrowIf(textureReadDisposed, this);
            own = CancellationTokenSource.CreateLinkedTokenSource(token, textureReadLifetime.Token);
            pendingTextureLeaseAcquisitions++;
        }
        TextureRetentionLease? retention = null;
        bool transferred = false;
        try
        {
            own.Token.ThrowIfCancellationRequested();
            retention = AcquireTextureRetention(exclusive: false)
                ?? throw new ArchiveException("TextureBrowseBusy", "The texture index is busy. Try again.");
            long selected = WithTextureBrowseConnection(own.Token, (read, transaction, cancellation) =>
                ValidateTextureBrowseGeneration(read, transaction, profile, generation, cancellation));
            own.Token.ThrowIfCancellationRequested();
            var registration = new TextureLeaseRegistration(retention, selected);
            publish(registration);
            transferred = true;
            return registration;
        }
        finally
        {
            try { if (!transferred) retention?.Dispose(); }
            finally
            {
                own.Dispose();
                lock (textureReadLifetimeGate)
                {
                    pendingTextureLeaseAcquisitions--;
                    Monitor.PulseAll(textureReadLifetimeGate);
                }
            }
        }
    }

    // Future floor advancement must hold this exclusive guard through its
    // transaction commit. Any open browse view defers retirement altogether.
    internal IDisposable? TryAcquireTextureRetirementLock()
    {
        lock (textureReadLifetimeGate)
            if (textureReadDisposed) return null;
        return AcquireTextureRetention(exclusive: true);
    }

    private TextureRetentionLease? AcquireTextureRetention(bool exclusive)
    {
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        bool memory = builder.Mode == SqliteOpenMode.Memory;
        string key = memory ? "memory:" + builder.DataSource : "file:" + Path.GetFullPath(path);
        TextureRetentionState state;
        lock (textureRetentionRegistryGate)
        {
            if (!textureRetentionRegistry.TryGetValue(key, out state!))
                textureRetentionRegistry.Add(key, state = new(key));
            if (exclusive)
            {
                if (state.RetirementDisabled || state.Retiring || state.Readers != 0) return null;
                state.Retiring = true;
            }
            else
            {
                if (state.Retiring) return null;
                state.Readers++;
            }
        }
        var lease = new TextureRetentionLease(state, exclusive);
        if (memory) return lease;
        if (!OperatingSystem.IsWindows())
        {
            lease.DisableRetirement();
            if (!exclusive) return lease;
            lease.Dispose(); return null;
        }
        SafeFileHandle? file = null;
        try
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = TextureRetentionNative.Offset();
            uint flags = 1u | (exclusive ? 2u : 0u); // FAIL_IMMEDIATELY; shared unless EXCLUSIVE_LOCK.
            if (TextureRetentionNative.LockFileEx(file, flags, 0, 1, 0, ref offset))
            {
                lease.File = file; file = null;
                return lease;
            }
            int error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87) // Filesystem does not support this lock range.
            {
                lease.DisableRetirement();
                if (!exclusive) return lease;
            }
            else if (error is not (33 or 997) && !exclusive)
                throw new IOException("The texture cache could not be pinned.", new Win32Exception(error));
            lease.Dispose(); return null;
        }
        catch
        {
            lease.Dispose();
            if (!exclusive) throw;
            return null;
        }
        finally { file?.Dispose(); }
    }

    private sealed class TextureRetentionLease(TextureRetentionState state, bool exclusive) : IDisposable
    {
        private TextureRetentionState? state = state;
        internal SafeFileHandle? File;

        internal void DisableRetirement()
        {
            lock (textureRetentionRegistryGate)
                if (state is { } current) current.RetirementDisabled = true;
            AssetLoadDiagnostics.Counters("TextureBrowser.Store.RetentionUnavailable", null,
                new Dictionary<string, long> { ["RetirementDisabled"] = 1 });
        }

        public void Dispose()
        {
            TextureRetentionState? released = Interlocked.Exchange(ref state, null);
            if (released is null) return;
            try
            {
                if (File is { } file)
                {
                    try
                    {
                        var offset = TextureRetentionNative.Offset();
                        if (!TextureRetentionNative.UnlockFileEx(file, 0, 1, 0, ref offset))
                            AssetLoadDiagnostics.Counters("TextureBrowser.Store.RetentionUnlock", null,
                                new Dictionary<string, long> { ["NativeError"] = Marshal.GetLastWin32Error() });
                    }
                    finally { file.Dispose(); }
                }
            }
            finally
            {
                lock (textureRetentionRegistryGate)
                {
                    if (exclusive) released.Retiring = false; else released.Readers--;
                    if (!released.Retiring && released.Readers == 0 && !released.RetirementDisabled)
                        textureRetentionRegistry.Remove(released.Key);
                }
            }
        }
    }

    private static class TextureRetentionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal UIntPtr Internal, InternalHigh;
            internal uint OffsetLow, OffsetHigh;
            internal IntPtr Event;
        }

        internal static Overlapped Offset() => new()
        {
            OffsetLow = unchecked((uint)TextureRetentionLockOffset),
            OffsetHigh = (uint)(TextureRetentionLockOffset >> 32)
        };

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved,
            uint lengthLow, uint lengthHigh, ref Overlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnlockFileEx(SafeFileHandle file, uint reserved,
            uint lengthLow, uint lengthHigh, ref Overlapped overlapped);
    }
}
