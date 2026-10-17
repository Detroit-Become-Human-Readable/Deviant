using System.Diagnostics;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly List<Exception> closeFailures = [];

    private void RecordCloseFailure(string operation, Exception error)
    {
        var failure = new InvalidOperationException($"Closing {operation} failed.", error);
        closeFailures.Add(failure);
        Trace.TraceError("{0}", failure);
    }

    private async Task CloseOwnedResourcesAsync()
    {
        Run("Activity", DisposeActivity);
        Run("Navigation", navigationCancellation.Cancel);
        IDisposable? foreground = null;
        Run("Preparation", () => foreground = workspacePreparation?.EnterForeground());
        try
        {
            Run("Audio discovery", () => session?.CancelEnrichment());
            Run("Texture discovery", CancelTextureCatalogWork);
            Run("Scene work", sceneView.CancelActivity);
            Run("Related items", () => relatedCancellation?.Cancel());
            Run("Filters", filterTimer.Stop);
            Run("Playback timer", playbackTimer.Stop);
            Run("Seek timer", seekTimer.Stop);
            Run("Opening", () => openCancellation?.Cancel());
            Run("Queries", () => queryCancellation?.Cancel());
            Run("Jobs", () => jobCancellation?.Cancel());
            await Step("Characters", ClearCharactersAsync);
            await Step("Dialogue", ClearAudioDialogueAsync);
            await Step("Physics", () => ClearPhysicsAsync());
            if (activeJob is { } job) await Step("Jobs", () => job, cancellationExpected: true);
            await Step("Opening", () => Task.WhenAll(pendingOpens.ToArray()), cancellationExpected: true);
            await Step("Playback", () => StopPlaybackAsync());
            var sceneDrain = sceneNavigationDrain;
            sceneNavigationDrain = Task.CompletedTask;
            await Step("Scene navigation", () => sceneDrain, cancellationExpected: true);
            await Step("Scenes", ClearSceneSessionAsync);
            await Step("Strings", () => stringsView.DisposeAsync().AsTask());
            await Step("DDS", () => ddsView.DisposeAsync().AsTask());
            await Step("Animation", () => animationView.DisposeAsync().AsTask());
            await Step("Shaders", () => shaderView.DisposeAsync().AsTask());
            await Step("References", () => referencesView.DisposeAsync().AsTask());
            await Step("Rendering", DisposeSceneRenderingAsync);
            await Step("Textures", ClearTextureCatalogsAsync);
            await Step("Catalog references", ClearSharedCatalogReferencesAsync);
            foreach (var item in advancedWindows.ToArray())
                await Step("Advanced archive", async () =>
                {
                    await item.Controller.CloseAsync(acknowledgeManifestFailure: true);
                    item.Window.Close();
                });
            var closedSession = session;
            session = null; admittedWorkspace = null;
            if (closedSession is not null) await Step("Archive", () => closedSession.DisposeAsync().AsTask());
            await Step("Texture browser", () => textureExplorer.DisposeAsync().AsTask());
            await Step("Asset browser", () => familyView.DisposeAsync().AsTask());
            await Step("Buffer sources", ClearBufferAsync);
            await Step("Buffer view", () => bufferView.DisposeAsync().AsTask());
            Run("Audio relationships", () => audioRelationships?.ClearBankGraphCache());
            await Step("Library", DrainLibraryTasksAsync);
            Run("Playback handler", () => player.PlaybackStopped -= Player_PlaybackStopped);
            Run("Player", player.Dispose);
            Run("Processing", WindowProcessing.Dispose);
        }
        finally { Run("Preparation lease", () => foreground?.Dispose()); }

        void Run(string operation, Action action)
        {
            CloseStage = operation;
            try { action(); }
            catch (Exception error) { RecordCloseFailure(operation, error); }
        }
        async Task Step(string operation, Func<Task> action, bool cancellationExpected = false)
        {
            CloseStage = operation;
            try { await action(); }
            catch (OperationCanceledException) when (cancellationExpected) { }
            catch (Exception error) { RecordCloseFailure(operation, error); }
        }
    }
}
