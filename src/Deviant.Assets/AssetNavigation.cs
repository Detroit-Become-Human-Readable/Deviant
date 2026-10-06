namespace Deviant.Assets;

public sealed record AssetSelection(ResourceIdentity Identity, SourceRevision Revision, string? OccurrenceKey = null);

public interface IAssetNavigator
{
    Task OpenAsync(ResourceIdentity identity, SourceRevision revision, CancellationToken token = default);
}
