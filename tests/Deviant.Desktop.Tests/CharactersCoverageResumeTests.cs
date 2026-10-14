using System.Buffers.Binary;
using System.IO;
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
public sealed class CharactersCoverageResumeTests
{
    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2)]
    public void FindMoreRestoresVerifiedPassesAndCompletesOnlyTheMissingGroup(params int[] indexed)
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, indexed);
            var findMore = Named<Button>(window.CharactersBrowser, "IndexMoreButton");
            Assert.True(findMore.IsEnabled);
            findMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Equal(Visibility.Visible, findMore.Visibility);
            Assert.Contains("3 of 3 groups checked", window.CharactersBrowser.ActivityStatus);
            Assert.Single(window.CharactersBrowser.Entries.Items.Cast<object>());
        });

    [Fact]
    public void SparseLaterCoverageDoesNotSkipTheUnverifiedPrefix()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, [2]);
            Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Contains("3 of 3 groups checked", window.CharactersBrowser.ActivityStatus);
            Assert.True(Named<Button>(window.CharactersBrowser, "IndexMoreButton").IsEnabled);
            Assert.Equal(3, Field<int>(window, "nextCharacterCatalogIndex"));
        });

    [Fact]
    public void CancellationDuringCoverageRestorePreservesRowsAndAllowsRetry()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, [0, 2]);
            var budget = window.RenderMemoryBudget!;
            var before = budget.UsedBytes;
            var gate = Field<SemaphoreSlim>(window.CharacterCatalog!, "operationGate");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
            try
            {
                Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(Wait(() => Tasks(window).Count > 0));
                Named<Button>(window.CharactersBrowser, "IndexCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(Wait(() => Tasks(window).Count == 0));
                Assert.Single(window.CharactersBrowser.Entries.Items.Cast<object>());
                Assert.Equal(before, budget.UsedBytes);
                Assert.True(Named<Button>(window.CharactersBrowser, "IndexMoreButton").IsEnabled);
            }
            finally { gate.Release(); }
            Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Equal(Visibility.Visible, Named<Button>(window.CharactersBrowser, "IndexMoreButton").Visibility);
        });

    [Fact]
    public void SourceReplacementClearsRestoredCoverageBeforeIndexingTheNewWorkspace()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, [0, 1, 2]);
            Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Equal(Visibility.Visible, Named<Button>(window.CharactersBrowser, "IndexMoreButton").Visibility);

            var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
            DiscoveryFixture.WriteIndex(replacement, [(3u, EmptyCatalog()), (999u, new byte[] { 2, 3 })]);
            Pump(window.OpenArchiveAsync(replacement));
            Assert.Null(window.CharacterCatalog);
            Assert.Equal(0, Field<int>(window, "characterCoverageRestoreCursor"));
            Assert.Empty(Field<HashSet<ResourceIdentity>>(window, "coveredCharacterCatalogs"));
            SeedCachedModel(window, 1);
            Pump(window.ShowCharactersAsync());
            Assert.True(Named<Button>(window.CharactersBrowser, "IndexMoreButton").IsEnabled);
            Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Contains("1 of 1 groups checked", window.CharactersBrowser.ActivityStatus);
            Assert.Equal(Visibility.Visible, Named<Button>(window.CharactersBrowser, "IndexMoreButton").Visibility);
        });

    [Fact]
    public void RetryReconcilesCommittedCoverageBeforeDiscoveringTheNextGroup()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, []);
            var more = Named<Button>(window.CharactersBrowser, "IndexMoreButton");
            var first = window.CharacterCatalog!.DiscoverModelsForCatalogAsync(window.SharedCatalogReferences!.CatalogIdentities[0]);
            Pump(first); using (var result = first.GetAwaiter().GetResult()) Assert.Equal(2, result.UnscannedCatalogs);
            var committed = window.CharacterCatalog!.DiscoverModelsForCatalogAsync(window.SharedCatalogReferences!.CatalogIdentities[1]);
            Pump(committed);
            using (var result = committed.GetAwaiter().GetResult()) Assert.Equal(1, result.UnscannedCatalogs);
            // The producer committed this pass before the UI observed it, as can occur at cancellation.
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Contains("3 of 3 groups checked", window.CharactersBrowser.ActivityStatus);
            Assert.Equal(Visibility.Visible, more.Visibility);
        });

    [Fact]
    public void NavigationAwayCancelsRestoreAndReopeningCanResume()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Prepare(window, fixture, [0, 2]);
            var gate = Field<SemaphoreSlim>(window.CharacterCatalog!, "operationGate");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
            try
            {
                Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(Wait(() => Tasks(window).Count > 0));
                Pump(window.OpenHomeViewAsync("audio"));
                Pump(Wait(() => Tasks(window).Count == 0));
                Assert.Equal(0, Field<int>(window, "characterCoverageRestoreCursor"));
                Assert.Empty(Field<HashSet<int>>(window, "inventoriedCharacterCatalogs"));
            }
            finally { gate.Release(); }
            Pump(window.ShowCharactersAsync());
            Assert.Single(window.CharactersBrowser.Entries.Items.Cast<object>());
            Named<Button>(window.CharactersBrowser, "IndexMoreButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Wait(() => Tasks(window).Count == 0));
            Assert.Equal(Visibility.Visible, Named<Button>(window.CharactersBrowser, "IndexMoreButton").Visibility);
        });

    private static void Prepare(AudioWindow window, DiscoveryFixture fixture, int[] indexed)
    {
        DiscoveryFixture.WriteIndex(fixture.Index, [(3u, EmptyCatalog()), (3u, EmptyCatalog()),
            (3u, EmptyCatalog()), (999u, new byte[] { 1 })]);
        Pump(window.OpenArchiveAsync(fixture.Index));
        var owner = window.CurrentSession!;
        using var store = owner.AcquireSharedFeatureStore();
        var budget = new AssetMemoryBudget();
        var provider = new CatalogReferenceProvider(owner.Snapshot, budget, store.Store);
        var catalog = new CharacterCatalogSession(owner.Snapshot, provider, budget, features: store.Store);
        try
        {
            foreach (var index in indexed)
            {
                var task = catalog.DiscoverModelsForCatalogAsync(provider.CatalogIdentities[index]);
                Pump(task); using var result = task.GetAwaiter().GetResult();
                Assert.Equal(3 - indexed.TakeWhile(item => item != index).Count() - 1, result.UnscannedCatalogs);
            }
        }
        finally { Pump(catalog.DisposeAsync().AsTask()); Pump(provider.DisposeAsync().AsTask()); }
        Assert.Equal(0, budget.UsedBytes);
        SeedCachedModel(window, 3);
        var selected = new ResourceIdentity(owner.Snapshot.IndexContentId, 3);
        Pump((Task)typeof(AudioWindow).GetMethod("ShowCharactersCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [CancellationToken.None, null, false, selected])!);
    }

    private static void SeedCachedModel(AudioWindow window, int ordinal)
    {
        var owner = window.CurrentSession!;
        var catalog = (CharacterCatalogSession)typeof(AudioWindow)
            .GetMethod("EnsureProductionCharacters", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [owner])!;
        using var store = owner.AcquireSharedFeatureStore();
        var identity = new ResourceIdentity(owner.Snapshot.IndexContentId, ordinal);
        store.Store.ReplaceAssets("character-model", [new AssetDescriptor(identity,
            new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp),
            "character-model", "Generated model", "Generated catalog", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.None, [], []) { LookupKey = identity.Key,
                Metadata = new Dictionary<string, string> { ["model.kind"] = "character", ["character.key"] = "generated",
                    ["character.name"] = "Generated model", ["model.name-profile"] = catalog.NameProfile,
                    ["model.animation-state"] = "unknown", ["model.animation-identities"] = "[]" } }]);
    }

    private static byte[] EmptyCatalog()
    {
        var bytes = new byte[20]; "COM_CONT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        return bytes;
    }
    private static T Named<T>(FrameworkElement view, string name) where T : FrameworkElement => (T)view.FindName(name);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static HashSet<Task> Tasks(AudioWindow window) => Field<HashSet<Task>>(window, "characterCatalogTasks");
    private static async Task Wait(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Pump(Task task)
    { var frame = new DispatcherFrame(); task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext()); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
}
