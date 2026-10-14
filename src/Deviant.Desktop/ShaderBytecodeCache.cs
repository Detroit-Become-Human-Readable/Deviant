using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;

namespace Deviant.Desktop;

public sealed record ShaderCompilerIdentity(string ModuleName, string FileVersion, string Sha256,
    string Architecture, string ManagedBackendVersion)
{
    /// <summary>Identifies the compiler already loaded by the renderer, without loading another DLL.</summary>
    public static ShaderCompilerIdentity FromLoadedCompiler()
    {
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().Where(module =>
            string.Equals(module.ModuleName, "d3dcompiler_47.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (modules.Length != 1) throw new InvalidOperationException("The loaded shader compiler identity is unavailable.");
        var module = modules[0];
        using var file = new FileStream(module.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 64 * 1024 * 1024) throw new InvalidDataException("The shader compiler file exceeds its identity limit.");
        var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        return new(module.ModuleName.ToLowerInvariant(), module.FileVersionInfo.FileVersion ?? "",
            hash, RuntimeInformation.ProcessArchitecture.ToString(),
            typeof(SharpDX.D3DCompiler.ShaderBytecode).Assembly.GetName().Version?.ToString() ?? "");
    }
}

public sealed record ShaderBytecodeRequest(string Source, string EntryPoint, string Target,
    int Flags, ShaderCompilerIdentity Compiler);

/// <summary>Shares device-independent bytecode and persists validated records within the shared cache budget.</summary>
public sealed partial class ShaderBytecodeCache(CacheManager? diskCache = null, long maximumHotBytes = 16L * 1024 * 1024) : IDisposable
{
    private readonly CacheManager? bundleDiskCache = diskCache;
    private readonly long hotLimit = maximumHotBytes;
    public const string Profile = "surface-shader-bytecode-v1";
    public const int MaximumBytecodeBytes = 4 * 1024 * 1024;
    public const int MaximumSourceBytes = 2 * 1024 * 1024;
    private const int HeaderLength = 76;
    private readonly object sync = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Entry> hot = [];
    private readonly LinkedList<string> order = [];
    private long hotBytes;
    private int activeOperations;
    private bool disposed;
    public long HotBytes { get { lock (sync) return hotBytes; } }

    public static string Key(ShaderBytecodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Compiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source); ArgumentException.ThrowIfNullOrWhiteSpace(request.EntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Target);
        if (Encoding.UTF8.GetByteCount(request.Source) > MaximumSourceBytes || request.EntryPoint.Length > 256 || request.Target.Length > 64 ||
            request.Compiler.Sha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(request.Compiler.ModuleName) || request.Compiler.ModuleName.Length > 256 ||
            request.Compiler.FileVersion is null || request.Compiler.FileVersion.Length > 256 ||
            request.Compiler.Architecture is null || request.Compiler.Architecture.Length > 64 ||
            request.Compiler.ManagedBackendVersion is null || request.Compiler.ManagedBackendVersion.Length > 256)
            throw new ArgumentException("The shader compile identity exceeds its limits.", nameof(request));
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Source))).ToLowerInvariant();
        var identity = JsonSerializer.Serialize(new { Profile, Source = sourceHash, request.EntryPoint, request.Target,
            request.Flags, Compiler = request.Compiler with { Sha256 = digest.ToLowerInvariant() } });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public async Task<ShaderBytecodeLease> AcquireAsync(ShaderBytecodeRequest request, AssetMemoryBudget budget,
        Func<CancellationToken, Task<byte[]>> compile, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(budget); ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Source);
        token.ThrowIfCancellationRequested();
        using var identityScratch = budget.Reserve(checked(Encoding.UTF8.GetByteCount(request.Source) + 8192L), "shader compile identity");
        string key = Key(request);
        if (hotLimit is < 0 or > 128L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(hotLimit));
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); activeOperations++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (hot.TryGetValue(key, out var entry) && ReferenceEquals(entry.Budget, budget))
                    {
                        order.Remove(entry.Node!); order.AddLast(entry.Node!); entry.References++;
                        AssetLoadDiagnostics.Cache("ShaderBytecodeMemoryCache", key, true);
                        return new(this, entry);
                    }
                    if (hot.TryGetValue(key, out entry)) RemoveHot(entry);
                }
                AssetLoadDiagnostics.Cache("ShaderBytecodeMemoryCache", key, false);
                using var scratch = budget.Reserve(2L * MaximumBytecodeBytes + HeaderLength + 4096, "shader bytecode compile and cache read");
                byte[] bytes;
                bool produced = false;
                if (bundleDiskCache is null)
                {
                    bytes = await Compile(linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await using var file = await bundleDiskCache.AcquirePreparedAsync("shaders", key, ".shader",
                        async (path, cancellation) =>
                        {
                            var code = await Compile(cancellation).ConfigureAwait(false); produced = true;
                            await WriteAsync(path, key, code, cancellation).ConfigureAwait(false);
                        }, async (path, cancellation) =>
                        {
                            try { _ = await ReadAsync(path, key, cancellation).ConfigureAwait(false); return true; }
                            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                            { return false; }
                        }, linked.Token).ConfigureAwait(false);
                    bytes = await ReadAsync(file.Path, key, linked.Token).ConfigureAwait(false);
                    AssetLoadDiagnostics.Cache("ShaderBytecodeDiskCache", key, !produced);
                }
                linked.Token.ThrowIfCancellationRequested();
                var memory = budget.Reserve(checked(bytes.Length + 512L), "retained shader bytecode");
                try
                {
                    lock (sync)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        var entry = new Entry(key, bytes, budget, memory) { References = 1 };
                        if (entry.AccountedBytes <= hotLimit)
                        {
                            while (order.First is { } first && hotBytes > hotLimit - entry.AccountedBytes) RemoveHot(hot[first.Value]);
                            entry.Cached = true; entry.Node = order.AddLast(key); hot.Add(key, entry); hotBytes += entry.AccountedBytes;
                        }
                        return new(this, entry);
                    }
                }
                catch { memory.Dispose(); throw; }

                async Task<byte[]> Compile(CancellationToken cancellation)
                {
                    using var timing = AssetLoadDiagnostics.Measure("ShaderCompilation", key);
                    cancellation.ThrowIfCancellationRequested(); var code = await compile(cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested(); ValidateBytecode(code); return code;
                }
            }
            finally { operation.Release(); }
        }
        finally
        {
            lock (sync)
                if (--activeOperations == 0 && disposed) { operation.Dispose(); lifetime.Dispose(); }
        }
    }

    private static void ValidateBytecode(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 32 or > MaximumBytecodeBytes || !bytes.AsSpan(0, 4).SequenceEqual("DXBC"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)) != bytes.Length)
            throw new InvalidDataException("The compiled shader bytecode is invalid or exceeds its size limit.");
    }
    private static async Task WriteAsync(string path, string key, byte[] bytes, CancellationToken token)
    {
        ValidateBytecode(bytes); var header = new byte[HeaderLength]; "DSHADER1"u8.CopyTo(header);
        Convert.FromHexString(key).CopyTo(header, 8); SHA256.HashData(bytes).CopyTo(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72), bytes.Length);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadAsync(string path, string key, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length < HeaderLength + 32 || stream.Length > HeaderLength + MaximumBytecodeBytes)
            throw new InvalidDataException("The cached shader length is invalid.");
        var header = new byte[HeaderLength]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual("DSHADER1"u8) || !header.AsSpan(8, 32).SequenceEqual(Convert.FromHexString(key)))
            throw new InvalidDataException("The cached shader identity does not match.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(72));
        if (count < 32 || count > MaximumBytecodeBytes || stream.Length != HeaderLength + count)
            throw new InvalidDataException("The cached shader length is invalid.");
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (!header.AsSpan(40, 32).SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("The cached shader checksum changed.");
        ValidateBytecode(bytes); token.ThrowIfCancellationRequested(); return bytes;
    }

    private void RemoveHot(Entry entry)
    {
        hot.Remove(entry.Key); order.Remove(entry.Node!); entry.Node = null; entry.Cached = false;
        hotBytes -= entry.AccountedBytes;
        if (entry.References == 0) entry.Memory.Dispose();
    }
    private void Release(Entry entry)
    {
        lock (sync) if (--entry.References == 0 && !entry.Cached) entry.Memory.Dispose();
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true; lifetime.Cancel();
            foreach (var entry in hot.Values.ToArray()) RemoveHot(entry);
            if (activeOperations == 0) { operation.Dispose(); lifetime.Dispose(); }
        }
    }
    internal sealed class Entry(string key, byte[] bytes, AssetMemoryBudget budget, IDisposable memory)
    {
        public string Key { get; } = key; public byte[] Bytes { get; } = bytes;
        public AssetMemoryBudget Budget { get; } = budget; public IDisposable Memory { get; } = memory;
        public long AccountedBytes => Bytes.Length + 512L;
        public bool Cached; public int References; public LinkedListNode<string>? Node;
    }
    public sealed class ShaderBytecodeLease : IDisposable
    {
        internal ShaderBytecodeCache? owner; internal readonly Entry entry;
        internal ShaderBytecodeLease(ShaderBytecodeCache owner, Entry entry) { this.owner = owner; this.entry = entry; }
        public ReadOnlyMemory<byte> Bytecode => owner is null ? throw new ObjectDisposedException(nameof(ShaderBytecodeLease)) : entry.Bytes;
        internal byte[] Bytes => owner is null ? throw new ObjectDisposedException(nameof(ShaderBytecodeLease)) : entry.Bytes;
        public string Key => owner is null ? throw new ObjectDisposedException(nameof(ShaderBytecodeLease)) : entry.Key;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entry);
    }
}
