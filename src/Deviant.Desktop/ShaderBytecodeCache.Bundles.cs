using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Deviant.Browser;
using Deviant.Assets;

namespace Deviant.Desktop;

public sealed partial class ShaderBytecodeCache
{
    public const string BundleProfile = "surface-shader-bundle-v1";
    public const long MaximumBundleHotBytes = 16L * 1024 * 1024;
    public const int MaximumBundleEntries = 4096;
    private const int BundleHeaderBytes = 76;
    private const int BundleEntryHeaderBytes = 68;
    private const long MaximumBundleFileBytes = MaximumBundleHotBytes + BundleHeaderBytes;

    /// <summary>Persists only the supplied compiled shader consumers, without compiling or loading individual records.</summary>
    public async Task<bool> PrepareBundleAsync(string bundleKey, IReadOnlyList<ShaderBytecodeLease> shaders,
        AssetMemoryBudget budget, CancellationToken token = default)
    {
        ValidateBundleKey(bundleKey); ArgumentNullException.ThrowIfNull(shaders); ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        if (bundleDiskCache is null || shaders.Count == 0) return false;
        if (shaders.Count > MaximumBundleEntries) throw new InvalidDataException("The shader bundle has too many entries.");
        var retained = new List<ShaderBytecodeLease>();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var shader in shaders)
                {
                    if (!ReferenceEquals(shader.owner, this)) throw new ArgumentException("The shader consumer belongs to another cache.", nameof(shaders));
                    if (!keys.Add(shader.entry.Key)) continue;
                    shader.entry.References++; retained.Add(new(this, shader.entry));
                }
                activeOperations++;
            }
            catch { foreach (var shader in retained) shader.Dispose(); throw; }
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                long total = retained.Sum(shader => shader.entry.AccountedBytes);
                if (total > Math.Min(hotLimit, MaximumBundleHotBytes)) return false;
                using var scratch = budget.Reserve(MaximumBundleFileBytes, "shader bundle validation");
                var records = retained.OrderBy(shader => shader.Key, StringComparer.Ordinal)
                    .Select(shader => new BundleRecord(shader.Key, shader.Bytes)).ToArray();
                await using var file = await bundleDiskCache.AcquirePreparedAsync("shader-bundles", bundleKey, ".shbundle",
                    (path, cancellation) => WriteBundleAsync(path, bundleKey, records, cancellation),
                    async (path, cancellation) =>
                    {
                        try
                        {
                            var existing = await ReadBundleAsync(path, bundleKey, cancellation).ConfigureAwait(false);
                            return existing.Length == records.Length && existing.OrderBy(record => record.Key, StringComparer.Ordinal)
                                .Zip(records).All(pair => pair.First.Key == pair.Second.Key && pair.First.Bytes.AsSpan().SequenceEqual(pair.Second.Bytes));
                        }
                        catch (Exception error) when (InvalidBundle(error)) { return false; }
                    }, linked.Token).ConfigureAwait(false);
                return true;
            }
            finally { operation.Release(); }
        }
        finally
        {
            foreach (var shader in retained) shader.Dispose();
            EndBundleOperation();
        }
    }

    /// <summary>Reads one validated bundle and publishes all its entries together into the budgeted bytecode LRU.</summary>
    public async Task<bool> RestoreBundleAsync(string bundleKey, AssetMemoryBudget budget, CancellationToken token = default)
    {
        ValidateBundleKey(bundleKey); ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (bundleDiskCache is null || hotLimit == 0) return false;
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); activeOperations++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                using var timing = AssetLoadDiagnostics.Measure("ShaderBundleRestore", bundleKey);
                using var scratch = budget.Reserve(MaximumBundleFileBytes, "shader bundle read");
                BundleRecord[]? records = null;
                await using var file = await bundleDiskCache.TryAcquirePreparedAsync("shader-bundles", bundleKey, ".shbundle",
                    async (path, cancellation) =>
                    {
                        try { records = await ReadBundleAsync(path, bundleKey, cancellation).ConfigureAwait(false); return true; }
                        catch (Exception error) when (InvalidBundle(error)) { records = null; return false; }
                    }, linked.Token).ConfigureAwait(false);
                AssetLoadDiagnostics.Cache("ShaderBundleCache", bundleKey, file is not null);
                if (file is null || records is null) return false;
                long total = records.Sum(record => record.Bytes.Length + 512L);
                if (total > Math.Min(hotLimit, MaximumBundleHotBytes)) return false;
                var staged = new List<Entry>(records.Length);
                try
                {
                    foreach (var record in records)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        staged.Add(new(record.Key, record.Bytes, budget,
                            budget.Reserve(record.Bytes.Length + 512L, "retained bundled shader bytecode")));
                    }
                    lock (sync)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this); linked.Token.ThrowIfCancellationRequested();
                        // Reserve and validate the complete batch before changing any resident entry.
                        foreach (var entry in staged)
                            if (hot.TryGetValue(entry.Key, out var existing)) RemoveHot(existing);
                        while (order.First is { } first && hotBytes > hotLimit - total) RemoveHot(hot[first.Value]);
                        foreach (var entry in staged)
                        {
                            entry.Cached = true; entry.Node = order.AddLast(entry.Key);
                            hot.Add(entry.Key, entry); hotBytes += entry.AccountedBytes;
                        }
                    }
                    staged.Clear(); return true;
                }
                finally { foreach (var entry in staged) entry.Memory.Dispose(); }
            }
            finally { operation.Release(); }
        }
        finally { EndBundleOperation(); }
    }

    private void EndBundleOperation()
    {
        lock (sync) if (--activeOperations == 0 && disposed) { operation.Dispose(); lifetime.Dispose(); }
    }

    private static bool InvalidBundle(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;
    private static void ValidateBundleKey(string key)
    {
        if (key is not { Length: 64 } || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Use a SHA-256 shader bundle identity.", nameof(key));
    }
    private sealed record BundleRecord(string Key, byte[] Bytes);
    private static async Task WriteBundleAsync(string path, string key, BundleRecord[] records, CancellationToken token)
    {
        var header = new byte[BundleHeaderBytes]; "DSHBNDL1"u8.CopyTo(header);
        Convert.FromHexString(key).CopyTo(header, 8); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), records.Length);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested(); ValidateBytecode(record.Bytes);
            var entry = new byte[BundleEntryHeaderBytes]; Convert.FromHexString(record.Key).CopyTo(entry, 0);
            SHA256.HashData(record.Bytes).CopyTo(entry, 32); BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(64), record.Bytes.Length);
            contentHash.AppendData(entry); contentHash.AppendData(record.Bytes);
            await stream.WriteAsync(entry, token).ConfigureAwait(false); await stream.WriteAsync(record.Bytes, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); stream.Position = 44;
        await stream.WriteAsync(contentHash.GetHashAndReset(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task<BundleRecord[]> ReadBundleAsync(string path, string key, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length < BundleHeaderBytes + BundleEntryHeaderBytes + 32 || stream.Length > MaximumBundleFileBytes)
            throw new InvalidDataException("The shader bundle length exceeds its limits.");
        var header = new byte[BundleHeaderBytes]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual("DSHBNDL1"u8) || !header.AsSpan(8, 32).SequenceEqual(Convert.FromHexString(key)))
            throw new InvalidDataException("The shader bundle identity changed.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40));
        if (count is < 1 or > MaximumBundleEntries) throw new InvalidDataException("The shader bundle entry count is invalid.");
        var records = new BundleRecord[count]; var keys = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); var entry = new byte[BundleEntryHeaderBytes];
            await stream.ReadExactlyAsync(entry, token).ConfigureAwait(false);
            string shaderKey = Convert.ToHexString(entry.AsSpan(0, 32)).ToLowerInvariant();
            int length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(64));
            if (!keys.Add(shaderKey) || length is < 32 or > MaximumBytecodeBytes || length > stream.Length - stream.Position)
                throw new InvalidDataException("The shader bundle entry framing is invalid.");
            total = checked(total + length + 512L);
            if (total > MaximumBundleHotBytes) throw new InvalidDataException("The shader bundle exceeds its memory limit.");
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            contentHash.AppendData(entry); contentHash.AppendData(bytes);
            if (!entry.AsSpan(32, 32).SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("The shader bundle entry checksum changed.");
            ValidateBytecode(bytes); records[index] = new(shaderKey, bytes);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("The shader bundle contains trailing data.");
        if (!header.AsSpan(44, 32).SequenceEqual(contentHash.GetHashAndReset())) throw new InvalidDataException("The shader bundle manifest checksum changed.");
        token.ThrowIfCancellationRequested(); return records;
    }
}
