using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using System.Security.Cryptography;

namespace Deviant.Browser;

/// <summary>Retains encoded material maps and file pins for one export job.</summary>
internal sealed class FbxMaterialMapStaging(CacheManager? cache) : IDisposable
{
    private readonly List<FileStream> pins = [];
    private readonly List<CacheLease> leases = [];
    private bool disposed;

    public async Task<FbxStagedMaterialMapInput> StageAsync(FbxBakedMaterialMapInput input,
        AssetMemoryBudget budget, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cache is null)
        {
            var error = new IOException("Export scratch storage is unavailable.");
            error.Data["ErrorCode"] = "CacheStorageLimit";
            throw error;
        }
        var diagnosticKey = $"{input.Identity?.Key}|{string.Join(";", input.PrimitiveKeys)}|role={input.Role}|{input.Width}x{input.Height}";
        using var stageTiming = AssetLoadDiagnostics.Measure("ExportMapStage", diagnosticKey);
        using var scratch = budget.Reserve(checked((long)input.Width * 8 + 256 * 1024), "FBX map PNG staging");
        TextureExportResult? encoded = null;
        CacheLease? lease = await cache.AcquireScratchOutputAsync(".png", async (output, cancellation) =>
        {
            encoded = await new TextureExportService().EncodeAsync(input.Document, input.Surface, AssetExportFormat.Png,
                output, "map.png", cancellationToken: cancellation, expectedRevision: input.Revision,
                preCommitValidation: input.ValidateSources).ConfigureAwait(false);
        }, (path, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(encoded is { Length: > 0 } && new FileInfo(path).Length == encoded.Length);
        }, token).ConfigureAwait(false);
        TextureExportResult result;
        var path = lease.Path;
        FileStream? pin = null;
        try
        {
            result = encoded ?? throw new InvalidDataException("The material map encoder returned no metadata.");
            pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (pin.Length != result.Length) throw new IOException("The staged material map length changed.");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(pin, token).ConfigureAwait(false)).ToLowerInvariant();
            if (digest != result.Sha256) throw new IOException("The staged material map hash changed.");
            token.ThrowIfCancellationRequested();
            pins.Add(pin); pin = null;
            leases.Add(lease); lease = null;
        }
        catch (Exception error)
        {
            pin?.Dispose();
            if (lease is not null)
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { error.Data["ScratchCleanupError"] = cleanup.GetType().Name; }
            throw;
        }
        if (AssetLoadDiagnostics.CountersEnabled)
            AssetLoadDiagnostics.Counters("ExportMapStage", diagnosticKey, new Dictionary<string, long>
            {
                ["Width"] = input.Width, ["Height"] = input.Height, ["Pixels"] = (long)input.Width * input.Height,
                ["EncodedBytes"] = result.Length, ["Sources"] = input.Sources.Count,
                ["PrimitiveScopes"] = input.PrimitiveKeys.Count, ["Role"] = (long)input.Role,
                ["UvSet"] = input.TextureCoordinateSet
            });
        return new(input.Role, input.TextureCoordinateSet, input.Identity!, input.Revision!, input.DdsSha256,
            input.Width, input.Height, input.IsSrgb, path, result.Length, result.Sha256,
            input.GraphSha256, input.PrimitiveKeys, input.Sources, input.ValidateSources)
            { PolygonCornerCoordinates = input.PolygonCornerCoordinates,
                OpenPreparedContent = cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
                } };
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Exception? failure = null;
        foreach (var pin in pins)
            try { pin.Dispose(); } catch (Exception error) { failure ??= error; }
        for (var index = leases.Count - 1; index >= 0; index--)
            try { leases[index].Dispose(); } catch (Exception error) { failure ??= error; }
        pins.Clear(); leases.Clear();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
