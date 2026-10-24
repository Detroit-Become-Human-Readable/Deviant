using System.Security.Cryptography;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Assets;

/// <summary>Pins archive sources and verifies the exact extents used by a decoded asset.</summary>
public sealed class SourceExtentVerifier
{
    public const long DefaultMaximumBytes = 256L * 1024 * 1024;
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumExtentCount = 4096;
    public const int MaximumLargeExtentCount = 65_536;

    public async Task<SourceExtentVerificationLease> AcquireAsync(ArchiveSnapshot snapshot,
        IReadOnlyList<SourceExtent> extents, CancellationToken token = default,
        long maximumBytes = DefaultMaximumBytes, int maximumExtentCount = MaximumExtentCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(extents);
        using var timing = AssetLoadDiagnostics.Measure("SourceVerification");
        if (maximumBytes is < 1 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumExtentCount is < 1 or > MaximumLargeExtentCount) throw new ArgumentOutOfRangeException(nameof(maximumExtentCount));
        token.ThrowIfCancellationRequested();
        if (extents.Count is < 1 || extents.Count > maximumExtentCount) throw new ArgumentException("Source extent count is invalid.", nameof(extents));
        var shared = ArchiveSourceSession.TryAcquire(snapshot);
        if (shared is not null)
        {
            try
            {
                var consumed = extents.ToArray();
                var names = consumed.Select(extent => extent.RelativeSource).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var packages = snapshot.Volumes.Where(volume => names.Contains(volume.RelativeName))
                    .Select(volume => volume.Package).Distinct().ToArray();
                await shared.Session.VerifyAsync(consumed, token, maximumBytes, maximumExtentCount).ConfigureAwait(false);
                return new SourceExtentVerificationLease(shared, packages);
            }
            catch { shared.Dispose(); throw; }
        }
        var provider = new FileRevisionProvider();
        var pins = new List<(string Path, FileStamp Stamp, SafeFileHandle Handle)>();
        var transferred = false;
        try
        {
            var index = SourceIdentity.OpenPinned(snapshot.IndexPath);
            pins.Add((snapshot.IndexPath, snapshot.IndexStamp, index));
            SourceIdentity.EnsureUnchanged(provider, index, snapshot.IndexPath, snapshot.IndexStamp);
            using (AssetLoadDiagnostics.Measure("VerificationRead", relativeSource: Path.GetFileName(snapshot.IndexPath),
                requestedBytes: snapshot.IndexStamp.Length))
                if (await SourceIdentity.HashAsync(index, snapshot.IndexStamp.Length, token).ConfigureAwait(false) != snapshot.IndexContentId)
                    throw Changed();
            var volumes = snapshot.Volumes.ToDictionary(volume => volume.RelativeName, StringComparer.OrdinalIgnoreCase);
            var opened = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[128 * 1024];
            long total = 0;
            foreach (var extent in extents.DistinctBy(item => (item.RelativeSource, item.Offset, item.Length, item.PayloadSha256)))
            {
                token.ThrowIfCancellationRequested();
                if (extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                    extent.PayloadSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
                    !volumes.TryGetValue(extent.RelativeSource, out var volume) || !volume.Stamp.Exists ||
                    extent.Offset + extent.Length > volume.Stamp.Length)
                    throw new ArgumentException("Source extent is invalid.", nameof(extents));
                total = checked(total + extent.Length);
                if (total > maximumBytes) throw new ArgumentException("Source verification exceeds its byte limit.", nameof(extents));
                if (!opened.TryGetValue(volume.RelativeName, out var handle))
                {
                    handle = SourceIdentity.OpenPinned(volume.FilePath);
                    pins.Add((volume.FilePath, volume.Stamp, handle));
                    SourceIdentity.EnsureUnchanged(provider, handle, volume.FilePath, volume.Stamp);
                    opened.Add(volume.RelativeName, handle);
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var extentTiming = AssetLoadDiagnostics.Measure("VerificationRead", relativeSource: extent.RelativeSource,
                    offset: extent.Offset, requestedBytes: extent.Length);
                for (long read = 0; read < extent.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(buffer.Length, extent.Length - read);
                    var count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, length), extent.Offset + read, token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("The source extent is truncated.");
                    hash.AppendData(buffer, 0, count); read += count;
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), digest, StringComparison.OrdinalIgnoreCase))
                    throw Changed();
            }
            foreach (var pin in pins) SourceIdentity.EnsureUnchanged(provider, pin.Handle, pin.Path, pin.Stamp);
            var lease = new SourceExtentVerificationLease(pins, provider);
            transferred = true; return lease;
        }
        finally { if (!transferred) foreach (var pin in pins) pin.Handle.Dispose(); }
    }

    private static ArchiveException Changed() => new("SourceChanged", "The source changed. Reopen the archive.");
}

public sealed class SourceExtentVerificationLease : IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyList<(string Path, FileStamp Stamp, SafeFileHandle Handle)> pins;
    private readonly ISourceRevisionProvider provider;
    private readonly ArchiveSourceSession.Lease? sharedOwner;
    private readonly IReadOnlyList<uint> packages = [];
    private int disposed;

    internal SourceExtentVerificationLease(IReadOnlyList<(string Path, FileStamp Stamp, SafeFileHandle Handle)> pins,
        ISourceRevisionProvider provider) { this.pins = pins; this.provider = provider; }

    internal SourceExtentVerificationLease(ArchiveSourceSession.Lease sharedOwner, IReadOnlyList<uint> packages)
    { pins = []; provider = new FileRevisionProvider(); this.sharedOwner = sharedOwner; this.packages = packages; }

    public void EnsureUnchanged()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        sharedOwner?.Session.EnsurePackagesCurrent(packages);
        foreach (var pin in pins) SourceIdentity.EnsureUnchanged(provider, pin.Handle, pin.Path, pin.Stamp);
    }

    /// <summary>Checks retained receipts together, visiting each shared archive source once.</summary>
    public void EnsureUnchangedWith(IReadOnlyList<SourceExtentVerificationLease> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        if (additional.Count == 0) { EnsureUnchanged(); return; }
        var sessions = new Dictionary<ArchiveSourceSession, HashSet<uint>>();
        Collect(this);
        foreach (var lease in additional) Collect(lease);
        foreach (var pair in sessions) pair.Key.EnsurePackagesCurrent(pair.Value);

        void Collect(SourceExtentVerificationLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref lease.disposed) != 0, lease);
            if (lease.sharedOwner is { } owner)
            {
                if (!sessions.TryGetValue(owner.Session, out var consumed))
                    sessions.Add(owner.Session, consumed = []);
                consumed.UnionWith(lease.packages);
            }
            else foreach (var pin in lease.pins)
                SourceIdentity.EnsureUnchanged(lease.provider, pin.Handle, pin.Path, pin.Stamp);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var pin in pins) pin.Handle.Dispose();
        sharedOwner?.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
