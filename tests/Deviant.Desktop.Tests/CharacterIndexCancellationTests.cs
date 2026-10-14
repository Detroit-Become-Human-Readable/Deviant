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
public sealed class CharacterIndexCancellationTests
{
    [Fact]
    public void CancellationAcknowledgesBeforeTheProviderDrains()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var pending = new TaskCompletionSource<IReadOnlyList<AssetDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken requested = default;
            window.CharacterCatalogProvider = (_, token) => { requested = token; return pending.Task; };
            var operation = window.ShowCharactersAsync();
            try
            {
                window.CharactersBrowser.SetIndexingState(true, true);
                Named<Button>(window.CharactersBrowser, "IndexCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested.IsCancellationRequested);
                Assert.False(operation.IsCompleted);
                Assert.Equal("Cancelling indexing…", window.CharactersBrowser.ActivityStatus);
                Assert.Equal(Visibility.Collapsed, Named<Button>(window.CharactersBrowser, "IndexCancelButton").Visibility);
            }
            finally { pending.TrySetResult([]); Pump(operation); }
        });

    [Fact]
    public void SwitchingBrowserModeAfterCancellationDoesNotRestartInventory()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            NativeTextureFixture.WriteArchive(fixture.Index);
            Pump(window.OpenArchiveAsync(fixture.Index));
            var owner = window.CurrentSession!;
            var catalog = (CharacterCatalogSession)Invoke(window, "EnsureProductionCharacters", owner)!;
            var revision = new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp);
            var identity = new ResourceIdentity(revision.IndexContentId, 1, "generated/model");
            var row = new AssetDescriptor(identity, revision, "character-model", "Generated model", null,
                AssetAvailability.Available, AssetCompleteness.Partial, AssetCapabilities.None, [], [])
            { LookupKey = identity.Key, Metadata = new Dictionary<string, string>
                { ["model.name-profile"] = catalog.NameProfile, ["model.kind"] = "unclassified" } };
            using (var lease = owner.AcquireSharedFeatureStore()) lease.Store.UpsertAssets([row]);
            using var cancellation = new CancellationTokenSource();
            Field(window, "characterCatalogCancellation").SetValue(window, cancellation);
            window.CharactersBrowser.SetIndexingState(true, true);
            Named<Button>(window.CharactersBrowser, "IndexCancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Named<ComboBox>(window.CharactersBrowser, "BrowserMode").SelectedIndex = 1;
            Pump(window.ShowCharactersAsync());
            Assert.Single(Named<ListView>(window.CharactersBrowser, "Entries").Items.Cast<object>());
            Assert.Equal(0, (int)Field(window, "nextCharacterCatalogIndex").GetValue(window)!);
            Assert.Equal(Visibility.Collapsed, Named<Button>(window.CharactersBrowser, "IndexCancelButton").Visibility);
        });

    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static T Named<T>(FrameworkElement view, string name) => (T)view.FindName(name);
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
