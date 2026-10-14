using Deviant.Browser;

namespace Deviant.Desktop;

public partial class SceneView
{
    internal Func<RenderAssetSnapshot, CancellationToken, Task>? SnapshotLoadAsyncOverride { get; set; }

    internal Task LoadViewportSnapshotAsync(RenderAssetSnapshot snapshot, CancellationToken token) =>
        SnapshotLoadAsyncOverride is { } load
            ? load(snapshot, token)
            : SceneViewport.LoadSnapshotAsync(snapshot, token);
}
