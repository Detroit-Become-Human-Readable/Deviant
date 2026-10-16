using System.IO;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly TextureExplorerView textureExplorer = new();
    public TextureExplorerView TextureBrowser => textureExplorer;

    private void InitializeTextureExplorer()
    {
        AssetsHost.Content = textureExplorer;
        textureExplorer.SelectedResourceChanged += (_, _) => SelectionForLibraryChanged();
        textureExplorer.BrowsePublished += (_, _) =>
        {
            if (currentView == "textures" && textureExplorer.PublishedBrowseResponse is { } response &&
                response.Token.Revision == textureDiscovery?.Revision) ApplyTextureContexts(response.Options.Contexts);
        };
        textureExplorer.NativeImageBrowser.ExportStemProvider = source => textureExplorer.SelectedEntry is { } entry && entry.Identity == source.SelectedIdentity ? entry.ExportStem : null;
        textureExplorer.NativeImageBrowser.NamingEntryProvider = source => textureExplorer.SelectedEntry is { } entry && entry.Identity == source.SelectedIdentity ? entry : null;
        textureExplorer.NativeImageBrowser.ValidateNamingAsync = async (source, token) =>
        {
            var discovery = textureDiscovery ?? throw new InvalidOperationException("Open textures first.");
            if (source.Revision != discovery.Revision) throw new ArchiveException("SourceChanged", "The texture name belongs to another revision.");
            await discovery.GetDetailsAsync(source.SelectedIdentity, token); token.ThrowIfCancellationRequested();
            if (discovery != textureDiscovery) throw new OperationCanceledException(token);
        };
        textureExplorer.LinkSelected += (_, args) => TrackLibraryTask(OpenTextureLinkObservedAsync(args.Link));
        textureExplorer.ExportOriginalAsync = async (entry, destination, overwrite, token) =>
        {
            var report = await ExportTextureOriginalCoreAsync(entry, destination, overwrite, token);
            var result = report.Manifest.Entries.Single();
            if (result.Status == ExportStatus.Cancelled) throw new OperationCanceledException(token);
            if (result.Status != ExportStatus.Completed) throw new IOException(result.Error ?? "The original texture could not be exported.");
        };
    }

    public Task<ExportReport> ExportTextureOriginalAsync(string destination, bool overwrite = false, CancellationToken token = default)
        => ExportTextureOriginalCoreAsync(textureExplorer.SelectedEntry ?? throw new InvalidOperationException("Select a texture first."), destination, overwrite, token);

    private async Task<ExportReport> ExportTextureOriginalCoreAsync(TextureDiscoveryEntry entry, string destination, bool overwrite, CancellationToken token)
    {
        var discovery = textureDiscovery ?? throw new InvalidOperationException("Open textures first.");
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var source = owner.Snapshot;
        if (discovery.Snapshot != source || discovery.Revision != textureExplorer.LoadedRevision || entry.Identity.IndexContentId != source.IndexContentId ||
            entry.Identity.MemberPath is not null || entry.Identity.Ordinal < 0 || entry.Identity.Ordinal >= source.Records.Count)
            throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        var row = source.Records[(int)entry.Identity.Ordinal];
        if (row.Type != entry.ResourceType || row.Id != entry.ResourceId || row.Validity != ResourceValidity.Valid || row.Type is not (2229 or 2230))
            throw new ArchiveException("InvalidSelection", "The selected texture is unavailable.");
        await discovery.GetDetailsAsync(entry.Identity, token);
        token.ThrowIfCancellationRequested();
        if (discovery != textureDiscovery || owner != session) throw new OperationCanceledException(token);
        var full = Path.GetFullPath(destination); var exporter = new RawExportService();
        var plan = exporter.Plan(source, new ExportOptions(Path.GetDirectoryName(full)!, overwrite), [row], token);
        plan = plan with { Items = [plan.Items.Single() with { RelativeOutput = Path.GetFileName(full) }] };
        return await exporter.RunAsync(plan, cancellationToken: token);
    }

    private async Task OpenTextureLinkObservedAsync(TextureDiscoveryLink link)
    {
        try { await NavigateTextureLinkAsync(link); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closingInProgress) NavigationError(error); }
    }

    public async Task NavigateTextureLinkAsync(TextureDiscoveryLink link, CancellationToken token = default)
    {
        if (!link.Verified || !link.CanNavigate) return;
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var discovery = textureDiscovery ?? throw new InvalidOperationException("Open textures first.");
        var origin = textureExplorer.SelectedIdentity; var selection = textureExplorer.SelectionVersion;
        if (origin is null || discovery.Snapshot != owner.Snapshot) return;
        var source = owner.Snapshot;
        if (link.Identity.IndexContentId != source.IndexContentId || link.Identity.Ordinal < 0 || link.Identity.Ordinal >= source.Records.Count ||
            link.Evidence.Any(evidence => evidence.Revision != discovery.Revision))
            throw new ArchiveException("SourceChanged", "The texture relationship belongs to another archive revision.");
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await discovery.GetDetailsAsync(origin, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (navigation.Generation != navigationGeneration || owner != session || discovery != textureDiscovery ||
            textureExplorer.SelectedIdentity != origin || textureExplorer.SelectionVersion != selection) return;
        var row = source.Records[(int)link.Identity.Ordinal];
        if (link.Kind == TextureRelationshipKind.RelatedTexture && link.Identity.MemberPath is null && row.Type is 2229 or 2230)
        { await textureExplorer.SelectResourceAsync(link.Identity, linked.Token); return; }
        var before = advancedWindows.Select(item => item.Window).ToHashSet();
        await OpenReferencedRecordAsync(owner, row, linked.Token);
        if (owner != session || navigation.Generation != navigationGeneration) return;
        var target = advancedWindows.LastOrDefault(item => !before.Contains(item.Window)).Window;
        if (target?.Content is not DockPanel panel) return;
        var details = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180, Margin = new(8),
            Text = $"{link.Label}\nMember {link.Identity.MemberPath ?? "—"}\nResource {link.Identity.Key}\n" +
                string.Join("\n\n", link.Evidence.Select(evidence => $"{evidence.Reader} {evidence.ReaderVersion} · {evidence.Field}\nSource {evidence.Source.Key}\nSHA-256 {evidence.SourceSha256}" +
                    (evidence.PhysicalExtent is { } physical ? $"\n{physical.RelativeSource}: {physical.Offset} + {physical.Length}" : "") +
                    (evidence.DecodedExtent is { } decoded ? $"\nDecoded {decoded.Offset} + {decoded.Length}" : "")))
        };
        var context = new Expander { Header = "Texture context", IsExpanded = true, Content = details, Margin = new(8, 4, 8, 4) };
        DockPanel.SetDock(context, Dock.Top); panel.Children.Insert(1, context);
    }
}
