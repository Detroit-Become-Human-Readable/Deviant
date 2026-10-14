using System.Windows;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private bool workspaceCancellationActive;

    private async void WorkspaceCancel_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceCancellationActive) return;
        workspaceCancellationActive = true;
        try
        {
            switch (currentView)
            {
                case "strings": stringsView.CancelActivity(); break;
                case "characters":
                    CancelCharacterIndexing(); characterBindingCancellation?.Cancel(); charactersView.CancelPreview();
                    try { await Task.WhenAll(characterCatalogTasks.Concat(characterBindingTasks).ToArray()); } catch { }
                    await Task.WhenAll(charactersView.DrainPreviewAsync(), charactersView.DrainRigAsync(), charactersView.DrainExportAsync());
                    break;
                case "scenes": sceneView.CancelActivity(); break;
                case "model": modelView.CancelActivity(); break;
                case "textures": CancelTextureCatalogWork(); textureExplorer.CancelActivity(); break;
                case "animation": animationView.CancelActivity(); break;
                case "shader": shaderView.CancelActivity(); break;
                case "references": referencesView.CancelActivity(); break;
                case "buffer": bufferView.CancelActivity(); break;
                case "dds": ddsView.CancelActivity(); break;
                case "physics": CancelPhysics(); break;
                default: Cancel_Click(sender, e); break;
            }
            if (foregroundBusy) { jobCancellation?.Cancel(); openCancellation?.Cancel(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { NavigationError(error); }
        finally { workspaceCancellationActive = false; RefreshActivity(); }
    }
}
