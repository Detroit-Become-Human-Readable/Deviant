using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public sealed record RenderTextureSelection(ResourceIdentity SourceIdentity, ResourceIdentity NativeIdentity,
    ResourceIdentity CatalogIdentity, SourceRevision Revision, string Label)
{
    public static RenderTextureSelection From(RenderTexture texture, SourceRevision revision, string label)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(revision);
        if (texture.SourceIdentity is not { } source || texture.CatalogIdentity is not { } catalog)
            throw new InvalidOperationException("The texture source is unavailable.");
        if (source.IndexContentId != revision.IndexContentId || catalog.IndexContentId != revision.IndexContentId ||
            texture.NativeIdentity.IndexContentId != revision.IndexContentId)
            throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        return new(source, texture.NativeIdentity, catalog, revision, label);
    }
}
