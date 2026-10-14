using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersStreamingResultsTests
{
    [Fact]
    public void StreamingPageKeepsExactSelectionBindingLodPreviewAndExportRequests()
        => WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.CharactersBrowser;
            var selected = Descriptor(1, "Selected");
            view.SetEntries([selected], selected.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            var binding = Binding(selected);
            view.SetModelBinding(binding);
            var detail = view.SelectedModelLod;
            var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var export = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken previewToken = default, exportToken = default;
            view.PreviewProvider = (descriptor, token) => { Assert.Same(selected, descriptor); previewToken = token; return preview.Task; };
            view.ExportProvider = (descriptor, suppliedBinding, suppliedDetail, token) =>
            { Assert.Same(selected, descriptor); Assert.Same(binding, suppliedBinding); Assert.Same(detail, suppliedDetail); exportToken = token; return export.Task; };
            var previewTask = (Task)Invoke(view, "PreviewSelectedAsync", CancellationToken.None)!;
            Named<Button>(view, "ExportFbxButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var changes = 0;
            view.SelectedResourceChanged += (_, _) => changes++;

            view.SetEntries([Descriptor(2, "Earlier"), selected with { Label = "Updated row" }], selected.Revision,
                preserveSearch: true, preserveSelection: true);
            view.SetResultPage(0, 2, 20);

            Assert.Same(selected, view.SelectedDescriptor);
            Assert.Same(binding, view.ModelBinding);
            Assert.Same(detail, view.SelectedModelLod);
            Assert.False(previewToken.IsCancellationRequested);
            Assert.False(exportToken.IsCancellationRequested);
            Assert.Equal(0, changes);
            Assert.Equal(2, Named<ListView>(view, "Entries").Items.Count);
            Assert.Equal("1–2 of 20", Named<TextBlock>(view, "ResultRange").Text);
            preview.SetResult(); export.SetResult(); Pump(previewTask);
            WaitUntil(() => Named<TextBlock>(view, "ExportStatus").Text == "Export complete.");
            Assert.Equal("Preview ready", Named<TextBlock>(view, "PreviewStatus").Text);
        });

    [Fact]
    public void FocusMovingOffPageKeepsOneDescriptorAndCanBeExplicitlyCleared()
        => WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.CharactersBrowser;
            var selected = Descriptor(1, "Selected");
            view.SetEntries([selected], selected.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            var changes = 0;
            view.SelectedResourceChanged += (_, _) => changes++;
            for (var page = 0; page < 5; page++)
                view.SetEntries([Descriptor(page + 10, "New result")], selected.Revision,
                    preserveSearch: true, preserveSelection: true);

            Assert.Same(selected, view.SelectedDescriptor);
            Assert.Null(Named<ListView>(view, "Entries").SelectedItem);
            Assert.Single(Named<ListView>(view, "Entries").Items.Cast<object>());
            Assert.Equal(0, changes);
            view.ClearSelection();
            Assert.Null(view.SelectedDescriptor);
            Assert.Equal(1, changes);
        });

    [Fact]
    public void DelayedVariantResolutionSurvivesAnOffPageRefresh()
        => WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.CharactersBrowser;
            var summary = Descriptor(1, "Selected group");
            var variant = summary with { Identity = summary.Identity with { Ordinal = 2 }, Label = "Selected variant" };
            var variants = new TaskCompletionSource<IReadOnlyList<AssetDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.VariantProviderAsync = (_, _) => variants.Task;
            view.SetEntries([summary], summary.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;

            view.SetEntries([Descriptor(3, "New result")], summary.Revision, preserveSearch: true, preserveSelection: true);
            variants.SetResult([variant]);

            WaitUntil(() => ReferenceEquals(view.SelectedDescriptor, variant));
            view.SetEntries([summary with { Label = "Updated group" }], summary.Revision, preserveSearch: true, preserveSelection: true);
            Assert.Same(variant, view.SelectedDescriptor);
            Assert.Same(summary, DescriptorOf(Named<ListView>(view, "Entries").SelectedItem!));
        });

    [Fact]
    public void RevisionReplacementClearsFocusAndCancelsItsPreview()
        => WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.CharactersBrowser;
            var selected = Descriptor(1, "Selected");
            view.SetEntries([selected], selected.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            CancellationToken previewToken = default;
            view.PreviewProvider = (_, token) => { previewToken = token; return Task.Delay(Timeout.Infinite, token); };
            var preview = (Task)Invoke(view, "PreviewSelectedAsync", CancellationToken.None)!;
            var replacement = Descriptor(2, "Replacement") with { Revision = new("streaming-index", "replacement-stamp") };

            view.SetEntries([replacement], replacement.Revision, preserveSearch: true, preserveSelection: true);

            Assert.Null(view.SelectedDescriptor);
            Assert.True(previewToken.IsCancellationRequested);
            Pump(preview);
        });

    [Fact]
    public void RefreshButtonIsAvailableDuringIndexingWithoutRequestingAnotherIndexPass()
        => WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.CharactersBrowser;
            var refreshed = 0; var indexed = 0;
            view.RefreshResultsRequested += (_, _) => refreshed++;
            view.IndexMoreRequested += (_, _) => indexed++;
            view.SetIndexingState(true, true);
            var button = Named<Button>(view, "IndexMoreButton");
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, refreshed);
            Assert.Equal(0, indexed);
        });

    [Fact]
    public void ProductionRefreshUpdatesCountsPreservesFocusAndReleasesOldPageReservations()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (catalog, revision) = PrepareProduction(window, fixture, 300);
            var view = window.CharactersBrowser;
            var list = Named<ListView>(view, "Entries");
            window.CharacterBindingsProvider = (descriptor, _, _) => Task.FromResult(
                ((IReadOnlyList<CharacterModelBindingResult>)[Binding(descriptor)], (IDisposable)new EmptyLease()));
            list.SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            var focus = view.SelectedDescriptor!;
            var binding = view.ModelBinding;
            var changes = 0; view.SelectedResourceChanged += (_, _) => changes++;
            var budget = window.RenderMemoryBudget!;
            var before = budget.UsedBytes;
            using (var lease = window.CurrentSession!.AcquireSharedFeatureStore())
                lease.Store.UpsertAssets([ProductionDescriptor(revision, 350, "AAA newly discovered", catalog.NameProfile)]);
            view.SetIndexingState(true, true);

            Named<Button>(view, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Named<TextBlock>(view, "ResultRange").Text == "1–200 of 301");

            Assert.Same(focus, view.SelectedDescriptor);
            Assert.Same(binding, view.ModelBinding);
            Assert.Equal(0, changes);
            Assert.InRange(budget.UsedBytes - before, -65536, 65536);
            Assert.Equal("AAA newly discovered", DescriptorOf(list.Items[0]).Label);
            view.ClearSelection();
            Assert.Null(Field<AssetMemoryBudget.Reservation?>(window, "characterFocusReservation"));
            Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
            Assert.Equal(0, budget.UsedBytes);
        });

    [Fact]
    public void LaterPagesKeepTheirCatalogBoundaryUntilAnExplicitRefresh()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (catalog, revision) = PrepareProduction(window, fixture, 300);
            var owner = window.CurrentSession!;
            var navigation = Field<long>(window, "navigationGeneration");
            var generation = Field<long>(window, "characterCatalogGeneration");
            var query = new AssetQuery(Offset: 200, Limit: 200, Sort: AssetSort.Name);
            Pump((Task)Invoke(window, "PublishCharacterResultsAsync", owner, catalog, query, navigation, generation, false, CancellationToken.None)!);
            var identities = Named<ListView>(window.CharactersBrowser, "Entries").Items.Cast<object>().Select(DescriptorOf).Select(item => item.Identity).ToArray();
            var boundary = Field<long?>(window, "characterPageBoundary");
            using (var lease = owner.AcquireSharedFeatureStore())
                lease.Store.UpsertAssets([ProductionDescriptor(revision, 350, "AAA newly discovered", catalog.NameProfile)]);

            Pump((Task)Invoke(window, "PublishCharacterResultsAsync", owner, catalog, query, navigation, generation, false, CancellationToken.None)!);

            Assert.Equal(boundary, Field<long?>(window, "characterPageBoundary"));
            Assert.Equal(identities, Named<ListView>(window.CharactersBrowser, "Entries").Items.Cast<object>().Select(DescriptorOf).Select(item => item.Identity));
            Assert.Equal("201–300 of 300", Named<TextBlock>(window.CharactersBrowser, "ResultRange").Text);
            Pump((Task)Invoke(window, "PublishCharacterResultsAsync", owner, catalog, query, navigation, generation, true, CancellationToken.None)!);
            Assert.Equal("201–301 of 301", Named<TextBlock>(window.CharactersBrowser, "ResultRange").Text);
        });

    private static (CharacterCatalogSession Catalog, SourceRevision Revision) PrepareProduction(AudioWindow window, DiscoveryFixture fixture, int count)
    {
        DiscoveryFixture.WriteIndex(fixture.Index, Enumerable.Range(0, 400).Select(index => (999u, new byte[] { (byte)(index % 251) })).ToArray());
        Pump(window.OpenArchiveAsync(fixture.Index));
        var owner = window.CurrentSession!;
        var catalog = (CharacterCatalogSession)Invoke(window, "EnsureProductionCharacters", owner)!;
        var revision = new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp);
        using (var lease = owner.AcquireSharedFeatureStore())
            lease.Store.ReplaceAssets("character-model", Enumerable.Range(0, count)
                .Select(index => ProductionDescriptor(revision, index, $"Generated model {index:D4}", catalog.NameProfile)).ToArray());
        Pump(window.ShowCharactersAsync());
        return (catalog, revision);
    }

    private static AssetDescriptor ProductionDescriptor(SourceRevision revision, int ordinal, string label, string profile) =>
        Descriptor(ordinal, label) with { Identity = new(revision.IndexContentId, ordinal, "generated/model"), Revision = revision,
            SearchTerms = label, SortKey = NaturalOrder.Key(label), Metadata = new Dictionary<string, string>
            { ["model.kind"] = "character", ["character.key"] = "generated-" + ordinal, ["character.name"] = label,
              ["model.name-profile"] = profile, ["model.animation-state"] = "unknown", ["model.animation-identities"] = "[]" } };

    private static AssetDescriptor Descriptor(int ordinal, string label) => new(new("streaming-index", ordinal, "generated/model"),
        new("streaming-index", "streaming-stamp"), "character-model", label, null, AssetAvailability.Available,
        AssetCompleteness.Complete, AssetCapabilities.Preview, [], []);

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor)
    {
        var container = new ResourceIdentity(descriptor.Identity.IndexContentId, 90);
        var mesh = new ResourceIdentity(descriptor.Identity.IndexContentId, 91, "generated/mesh");
        CharacterMeshLodBinding lod = new(0, 1, new(IdentifierNamespace.Native, "generated-mesh", container),
            new(ReferenceStatus.Unique, [mesh]), mesh, 0, 1, null, new(mesh, 0), new(container, 0, 8), null, null, null);
        return new(descriptor.Identity, new(descriptor.Identity.IndexContentId, 89), container, descriptor.Revision,
            2009, 1, null, 0, null, null, new(ReferenceStatus.Unique, [container]), [], [], [lod], null, null);
    }

    private static AssetDescriptor DescriptorOf(object row) => (AssetDescriptor)row.GetType().GetProperty("Descriptor")!.GetValue(row)!;
    private static T Named<T>(FrameworkElement view, string name) where T : FrameworkElement => Assert.IsAssignableFrom<T>(view.FindName(name));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) Pump(Task.Delay(10, timeout.Token));
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
