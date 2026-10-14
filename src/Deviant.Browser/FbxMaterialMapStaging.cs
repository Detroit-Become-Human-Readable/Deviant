using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using System.Security.Cryptography;

namespace Deviant.Browser;

/// <summary>Retains encoded material maps and file pins for one export job.</summary>
internal sealed class FbxMaterialMapStaging : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-Fbx-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> pins = [];
    private bool disposed;
    private int sequence;

    public async Task<FbxStagedMaterialMapInput> StageAsync(FbxBakedMaterialMapInput input,
        string sourceRoot, AssetMemoryBudget budget, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var diagnosticKey = $"{input.Identity?.Key}|{string.Join(";", input.PrimitiveKeys)}|role={input.Role}|{input.Width}x{input.Height}";
        using var stageTiming = AssetLoadDiagnostics.Measure("ExportMapStage", diagnosticKey);
        using var scratch = budget.Reserve(checked((long)input.Width * 8 + 256 * 1024), "FBX map PNG staging");
        var result = await new TextureExportService().ExportAsync(input.Document, input.Surface, AssetExportFormat.Png,
            sourceRoot, root, $"map-{sequence++:D6}.png", cancellationToken: token,
            expectedRevision: input.Revision, preCommitValidation: input.ValidateSources).ConfigureAwait(false);
        var path = Path.GetFullPath(Path.Combine(root, result.RelativePath));
        var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (pin.Length != result.Length) throw new IOException("The staged material map length changed.");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(pin, token).ConfigureAwait(false)).ToLowerInvariant();
            if (digest != result.Sha256) throw new IOException("The staged material map hash changed.");
            pins.Add(pin); pin = null!;
        }
        finally { pin?.Dispose(); }
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
            { PolygonCornerCoordinates = input.PolygonCornerCoordinates };
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var pin in pins) pin.Dispose();
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
        Directory.Delete(root, recursive: false);
    }
}
