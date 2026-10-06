using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;

namespace Deviant.Browser;

public sealed partial class SceneCatalogSession
{
    public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Track(async linked =>
    {
        ValidateSection(section);
        await using var lease = await new SourceExtentVerifier().AcquireAsync(Snapshot, section.Result.Extents, linked).ConfigureAwait(false);
        lease.EnsureUnchanged(); return true;
    }, token);

    public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Track(async linked =>
    {
        ValidateSection(section);
        var document = section.Result.Value!;
        var row = Snapshot.Records[(int)document.SourceContainer.Ordinal];
        var original = section.Result.Extents.Single(extent => extent.RelativeSource == row.VolumeName &&
            extent.Offset == row.Offset && extent.Length == row.StoredSize);
        await using var lease = await new SourceExtentVerifier().AcquireAsync(Snapshot, section.Result.Extents, linked).ConfigureAwait(false);
        await AssetFileExporter.WriteAsync(Snapshot, document.SourceContainer, Revision, original, destination, false,
            async (stream, cancellation) => await JsonSerializer.SerializeAsync(stream, new
            {
                Format = "deviant-scene-structure-v1", section.Result.Identity, section.Result.Revision,
                section.Descriptor.Label, Scope = "Section", section.Result.Extents, section.Result.Steps,
                document.Hierarchy, document.Entities, document.CatalogInstances,
                Environment = new { document.Environment.Identity, document.Environment.Version, document.Environment.Extent,
                    document.Environment.References },
                Names = section.NamesByNode.Select(pair => new { Identity = pair.Key, Names = pair.Value }),
                References = section.ReferencesByNode.Select(pair => new { Identity = pair.Key, Relationships = pair.Value }),
                Objects = document.Objects.Select(frame => new
                { frame.Identity, frame.NativeType, frame.NativeId, frame.PayloadExtent, frame.DirectoryEntryExtent }),
                document.Diagnostics
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }, cancellation),
            linked, lifetime.Token, additionalValidation: cancellation =>
            { cancellation.ThrowIfCancellationRequested(); lease.EnsureUnchanged(); return Task.CompletedTask; }).ConfigureAwait(false);
        return true;
    }, token);

    private void ValidateSection(SceneLoadedSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Result.Revision != Revision || section.Result.Value is not { } value ||
            value.Revision != Revision || value.Identity != section.Result.Identity ||
            value.SourceContainer.IndexContentId != Snapshot.IndexContentId)
            throw new ArchiveException("SourceChanged", "The section belongs to another source.");
    }
}
