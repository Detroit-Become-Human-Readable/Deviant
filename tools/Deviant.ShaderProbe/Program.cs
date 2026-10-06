using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Archives;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Shaders;

if (args.Length != 3 || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal < 0)
{
    Console.Error.WriteLine("Usage: Deviant.ShaderProbe <index.idx> <resource-ordinal> <output-directory>");
    return 2;
}

var indexPath = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[2]);
var archive = await new ArchiveReader().OpenAsync(indexPath);
if (DirectoryBoundary.IsWithin(outputDirectory, archive.SourceRoot))
    throw new ArchiveException("UnsafeOutput", "Choose an output directory outside the archive installation.");

var resource = archive.Records.FirstOrDefault(record => record.Ordinal == ordinal);
if (resource is null)
    throw new ArchiveException("OrdinalNotFound", "The selected resource ordinal does not exist in the index.");

var decoded = await new SegsContainerReader().ReadAsync(archive, resource);
var scan = StoredGlslReader.Scan(decoded);
if (scan.Sources.Count == 0)
    throw new ArchiveException("NoStoredGlsl", "The selected resource did not contain a bounded GLSL source region.");

Directory.CreateDirectory(outputDirectory);
var exported = new List<ShaderExportRecord>();
foreach (var source in scan.Sources)
{
    var extension = source.CanPreview ? ".glsl" : ".bin";
    var fileName = $"shader-{source.RegionOrdinal:D4}-{source.DecodedExtent.Offset:X8}-{source.Sha256[..12]}{extension}";
    var outputPath = Path.Combine(outputDirectory, fileName);
    await using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        await source.WriteOriginalAsync(stream);
    exported.Add(new(fileName, source.Identity, source.Sha256, source.VersionDirective,
        source.PhysicalExtent, source.PhysicalExtents, source.DecodedExtent, source.ContextExtent,
        Convert.ToHexString(source.ContextBefore.Span), source.HasNullTerminator, source.DecodeSteps));
}

var manifest = new ShaderProbeManifest("deviant-stored-glsl-v1", archive.IndexContentId,
    archive.PhysicalSourceStamp, resource.Ordinal, resource.Type, resource.Id,
    scan.Diagnostics, exported);
var manifestPath = Path.Combine(outputDirectory, "manifest.json");
await using (var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
    await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true });

Console.WriteLine($"Exported {exported.Count} stored GLSL region(s) to {Path.GetFileName(outputDirectory)}.");
Console.WriteLine($"Manifest: {Path.GetFileName(manifestPath)}");
return 0;

internal sealed record ShaderProbeManifest(string Schema, string IndexContentId, string PhysicalSourceStamp,
    long ResourceOrdinal, uint ResourceType, uint ResourceId, IReadOnlyList<ShaderDiagnostic> Diagnostics,
    IReadOnlyList<ShaderExportRecord> Sources);

internal sealed record ShaderExportRecord(string FileName, ResourceIdentity Identity, string Sha256,
    string VersionDirective, SourceExtent PhysicalExtent, IReadOnlyList<SourceExtent> PhysicalExtents,
    DecodedExtent TextExtent,
    DecodedExtent ContextExtent, string ContextBeforeHex, bool HasNullTerminator,
    IReadOnlyList<DecodeStep> DecodeSteps);
