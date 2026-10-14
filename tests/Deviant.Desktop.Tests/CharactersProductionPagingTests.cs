using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersProductionPagingTests
{
    private const int ProductionPageSize = 200;
    private const int GeneratedModelCount = 1205;

    [Fact]
    public void DefaultProductionRoutePagesFeatureStoreSearchesFromZeroAndSelectsRequestedIdentity()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (descriptors, sessionBaseline) = SeedProductionCatalog(window, fixture, GeneratedModelCount);
            var budget = window.RenderMemoryBudget ?? throw new InvalidOperationException("The production catalog did not acquire a memory budget.");
            var entries = Entries(window);
            var range = Named<TextBlock>(window.CharactersBrowser, "ResultRange");
            var maximumPageBytes = ProductionPageSize * 64 * 1024L;

            Assert.Equal($"1–200 of {GeneratedModelCount:N0}", range.Text);
            Assert.Equal(ProductionPageSize, entries.Items.Count);
            Assert.InRange(budget.UsedBytes - sessionBaseline, 1, maximumPageBytes);
            Assert.False(Named<Button>(window.CharactersBrowser, "PreviousResults").IsEnabled);
            Assert.True(Named<Button>(window.CharactersBrowser, "NextResults").IsEnabled);

            ClickNext(window, $"201–400 of {GeneratedModelCount:N0}");
            Assert.Equal(ProductionPageSize, entries.Items.Count);
            Assert.InRange(budget.UsedBytes - sessionBaseline, 1, maximumPageBytes);
            ClickNext(window, $"401–600 of {GeneratedModelCount:N0}");
            ClickNext(window, $"601–800 of {GeneratedModelCount:N0}");
            ClickNext(window, $"801–1,000 of {GeneratedModelCount:N0}");
            ClickNext(window, $"1,001–1,200 of {GeneratedModelCount:N0}");
            Assert.Equal(200, entries.Items.Count);
            ClickNext(window, $"1,201–1,205 of {GeneratedModelCount:N0}");
            Assert.Equal(5, entries.Items.Count);
            Assert.InRange(budget.UsedBytes - sessionBaseline, 1, maximumPageBytes);
            Assert.False(Named<Button>(window.CharactersBrowser, "NextResults").IsEnabled);

            var finalEntry = descriptors[^1];
            var modelProviderCalls = 0;
            window.CharacterModelProvider = (_, _, _) =>
            {
                modelProviderCalls++;
                return Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("No preview should run during selection navigation."));
            };

            window.CharactersBrowser.SetSearchQuery("Generated character 1204");
            WaitUntil(() => range.Text == "1–1 of 1");
            Assert.Equal("Generated character 1204", window.CharactersBrowser.SearchQuery);
            Assert.True(entries.Items.Count > 0,
                $"Search={window.CharactersBrowser.SearchQuery}; cached page={string.Join(" | ", Field<CharacterCatalogAssetPage>(window, "characterAssetPage").Assets.Select(d => d.Label))}");
            Assert.Single(entries.Items.Cast<object>());
            Assert.Equal(finalEntry.Identity, DescriptorOf(entries.Items[0]).Identity);

            var navigation = ((long Generation, CancellationToken Token))Invoke(window, "BeginNavigation")!;
            Pump((Task)Invoke(window, "NavigateDiscoveryAsync", "characters", finalEntry.Identity,
                navigation.Generation, navigation.Token)!);

            Assert.Equal(finalEntry.Identity, window.CharactersBrowser.SelectedIdentity);
            Assert.Equal(finalEntry.Identity, window.CharactersBrowser.SelectedDescriptor?.Identity);
            Assert.Equal(string.Empty, window.CharactersBrowser.SearchQuery);
            Assert.Equal(0, modelProviderCalls);
            Assert.InRange(budget.UsedBytes - sessionBaseline, 1, maximumPageBytes);
        });

    [Theory]
    [InlineData("source")]
    [InlineData("cache")]
    public void SourceAndCacheDrainsReleaseTheOwnedProductionPage(string drain)
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (_, sessionBaseline) = SeedProductionCatalog(window, fixture, GeneratedModelCount);
            var owner = window.CurrentSession!;
            var budget = window.RenderMemoryBudget ?? throw new InvalidOperationException("The production catalog did not acquire a memory budget.");
            Assert.InRange(budget.UsedBytes - sessionBaseline, 1, ProductionPageSize * 64 * 1024L);

            if (drain == "source")
            {
                var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacement, [(999u, new byte[] { 8, 9, 10 })]);
                Pump(window.OpenArchiveAsync(replacement));
                Assert.NotSame(owner, window.CurrentSession);
                Assert.Equal(Path.GetFullPath(replacement), window.CurrentSession!.Snapshot.IndexPath);
            }
            else
            {
                Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
                Assert.Null(window.CurrentSession);
            }

            Assert.Equal(0, budget.UsedBytes);
            Assert.Null(Field<CharacterCatalogAssetPage?>(window, "characterAssetPage"));
        });

    private static (IReadOnlyList<AssetDescriptor> Descriptors, long SessionBaseline) SeedProductionCatalog(AudioWindow window,
        DiscoveryFixture fixture, int count)
    {
        var records = Enumerable.Range(0, count)
            .Select(index => (999u, new byte[] { checked((byte)(index % 251 + 1)) }))
            .ToArray();
        DiscoveryFixture.WriteIndex(fixture.Index, records);
        Pump(window.OpenArchiveAsync(fixture.Index));
        var owner = window.CurrentSession ?? throw new InvalidOperationException("The generated archive did not open.");
        var catalog = (CharacterCatalogSession)Invoke(window, "EnsureProductionCharacters", owner)!;
        var revision = new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp);
        var descriptors = Enumerable.Range(0, count).Select(index => Descriptor(owner.Snapshot, revision, index, catalog.NameProfile)).ToArray();
        using (var lease = owner.AcquireSharedFeatureStore())
            lease.Store.ReplaceAssets("character-model", descriptors);

        var sessionBaseline = window.RenderMemoryBudget!.UsedBytes;
        Pump(window.ShowCharactersAsync());
        WaitUntil(() => Entries(window).Items.Count == ProductionPageSize);
        return (descriptors, sessionBaseline);
    }

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, SourceRevision revision, int ordinal, string nameProfile)
    {
        var label = $"Generated character {ordinal:D4}";
        var identity = new ResourceIdentity(snapshot.IndexContentId, ordinal, "generated/character-model");
        return new AssetDescriptor(identity, revision, "character-model", label, "generated-catalog",
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label, NameOrigin.Stored, "MESHDATA v41 generated fixture")], [])
        {
            LookupKey = identity.Key,
            SearchTerms = label,
            SortKey = NaturalOrder.Key(label),
            Metadata = new Dictionary<string, string> { ["model.kind"] = "character", ["character.key"] = "generated-" + ordinal,
                ["character.name"] = label, ["model.name-profile"] = nameProfile,
                ["model.animation-state"] = "unknown", ["model.animation-identities"] = "[]" }
        };
    }

    private static void ClickNext(AudioWindow window, string expectedRange)
    {
        Named<Button>(window.CharactersBrowser, "NextResults").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => Named<TextBlock>(window.CharactersBrowser, "ResultRange").Text == expectedRange);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static AssetDescriptor DescriptorOf(object row) =>
        (AssetDescriptor)row.GetType().GetProperty("Descriptor", BindingFlags.Instance | BindingFlags.Public)!.GetValue(row)!;

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) Pump(Task.Delay(10, timeout.Token));
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
