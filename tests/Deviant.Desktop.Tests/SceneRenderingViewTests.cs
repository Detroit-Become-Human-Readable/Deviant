using System.Numerics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneRenderingViewTests
{
    [Fact]
    public void ApprovedRetryReleasesFailedStreamBeforePreparingAgain() => Run(() =>
    {
        var loaded = Section();
        var budget = new AssetMemoryBudget();
        var sourceMemory = budget.Reserve(128, "generated first attempt");
        var source = Snapshot(budget, [sourceMemory]);
        using var retry = Snapshot(budget);
        using var service = new AssetViewportService();
        var calls = 0;
        var view = new SceneView
        {
            RenderProvider = (_, _, _) => Task.FromResult(retry),
            LargeDataConfirmationProvider = (_, _) => Task.FromResult(true),
            StreamingRenderProvider = async (_, _, publish, token) =>
            {
                if (++calls == 1)
                {
                    await publish(source, token);
                    source.Dispose();
                    throw new ArchiveException("NativeContainerLimit", "Generated source limit");
                }
                Assert.Equal(0, sourceMemory.Bytes);
                return retry;
            }
        };
        view.SetViewportService(service);
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            Pump(view.LoadRegionAsync());
            Assert.Equal(2, calls);
        }
        finally { Pump(view.DisposeAsync().AsTask()); source.Dispose(); }
    });

    [Fact]
    public void PendingLargeLoadDecisionKeepsStreamedGeometryUsable() => Run(() =>
    {
        var loaded = Section();
        using var source = Snapshot(new AssetMemoryBudget());
        using var service = new AssetViewportService();
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompted = false;
        var view = new SceneView
        {
            RenderProvider = (_, _, _) => Task.FromResult(source),
            StreamingRenderProvider = async (_, _, publish, token) =>
            {
                await publish(source, token);
                throw new ArchiveException("NativeContainerLimit", "Generated source limit");
            },
            LargeDataConfirmationProvider = (_, _) => { prompted = true; return decision.Task; }
        };
        view.SetViewportService(service);
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var loading = view.LoadRegionAsync();
            PumpUntil(() => prompted);
            Assert.True(view.Viewport.VisiblePartCount > 0);
            Assert.True(view.Viewport.IsEnabled);
            view.CancelActivity();
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(TimeSpan.FromSeconds(2))));
            Assert.Equal(0, view.Viewport.VisiblePartCount);
        }
        finally { decision.TrySetResult(false); Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void CancellingPendingLargeLoadDecisionDrainsWithoutUserAnswer() => Run(() =>
    {
        var loaded = Section();
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompted = false;
        var view = new SceneView
        {
            RenderProvider = (_, _, _) => Task.FromException<RenderAssetSnapshot>(new ArchiveException("NativeContainerLimit", "Generated source limit")),
            LargeDataConfirmationProvider = (_, _) => { prompted = true; return decision.Task; }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var loading = view.LoadRegionAsync();
            PumpUntil(() => prompted);
            view.CancelActivity();
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(TimeSpan.FromSeconds(2))));
            Assert.False(view.IsLoading);
            Assert.Equal("Cancelled", view.ActivityProgress.Stage);
        }
        finally { decision.TrySetResult(false); Pump(view.DisposeAsync().AsTask()); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeExportRetriesOnlyAfterApprovalAndRestoresTheNormalBudget(bool approve) => Run(() =>
    {
        var budget = new AssetMemoryBudget();
        using var source = Snapshot(budget);
        var view = new SceneView();
        int calls = 0, prompts = 0;
        try
        {
            typeof(SceneView).GetField("renderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, source);
            view.LargeDataConfirmationProvider = (message, _) =>
            { prompts++; Assert.Contains("512 MiB", message); Assert.Contains("2 GiB", message); return Task.FromResult(approve); };
            view.GeometryExportProvider = (snapshot, request, path, progress, token) =>
            {
                calls++;
                if (calls == 1) { Assert.Equal(AssetMemoryBudget.DefaultMaximumBytes, budget.MaximumBytes); throw new ArchiveException("AssetMemoryLimit", "Generated staging limit"); }
                Assert.Equal(2L * 1024 * 1024 * 1024, budget.MaximumBytes);
                var identity = source.Occurrences[0].Entity;
                return Task.FromResult(new Deviant.Export.Models.FbxGeometryExportReport(identity, source.Revision,
                    new(identity,"selection"),Path.GetFileName(path),new string('a',64),1,1,0,[]));
            };
            var export = view.ExportGeometryAsync("Generated.fbx",entireLoadedScene:true,includeHidden:true);
            if (approve) Pump(export);
            else Assert.Throws<OperationCanceledException>(() => Pump(export));
            Assert.Equal(approve ? 2 : 1, calls); Assert.Equal(1, prompts);
            Assert.Equal(AssetMemoryBudget.DefaultMaximumBytes,budget.MaximumBytes);
            Assert.Equal(0,budget.UsedBytes);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void FramingARegionAppliesTheImprovedDepthRange() => Run(() =>
    {
        var viewport = new AssetViewport();
        try
        {
            viewport.Measure(new Size(800, 600));
            viewport.Arrange(new Rect(0, 0, 800, 600));
            typeof(AssetViewport).GetMethod("Frame", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewport, [new GeometryBounds(new(-10), new(10))]);
            var camera = Assert.IsType<HelixToolkit.Wpf.SharpDX.PerspectiveCamera>(viewport.Viewport.Camera);
            Assert.InRange(camera.NearPlaneDistance, .17, .18);
            Assert.InRange(camera.FarPlaneDistance, 1732, 1733);
        }
        finally { Pump(viewport.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void RegionValuesRoundTripAndRejectNonPositiveSizes() => Run(() =>
    {
        var view = new SceneView();
        try
        {
            var bounds = new GeometryBounds(new(-3, 2, 5), new(7, 8, 13));
            view.SetRegion(bounds);
            Assert.Equal(bounds, view.Region);
            ((TextBox)view.FindName("RegionSizeY")).Text = "0";
            Assert.Throws<InvalidOperationException>(() => view.Region);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ClearingRendererKeepsTheLoadedBranchAndRegion() => Run(() =>
    {
        var loaded = Section();
        var view = new SceneView { RenderProvider = (_, _, _) => Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("Generated provider")) };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var branch = view.SelectedBranchIdentity;
            var region = new GeometryBounds(new(-3, 2, 5), new(7, 8, 13));
            view.SetRegion(region);
            Pump(view.ClearRenderingAsync());
            Assert.Same(loaded, view.CurrentSection);
            Assert.Equal(branch, view.SelectedBranchIdentity);
            Assert.Equal(region, view.Region);
            Assert.True(((Button)view.FindName("LoadRegionButton")).IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void UnloadCancelsPendingRenderAndReleasesLateSnapshot() => Run(() =>
    {
        var loaded = Section();
        var pending = new TaskCompletionSource<RenderAssetSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken renderToken = default;
        var view = new SceneView { RenderProvider = (_, _, token) => { renderToken = token; return pending.Task; } };
        var budget = new AssetMemoryBudget(4096);
        var candidate = Snapshot(budget, [budget.Reserve(128, "generated geometry")]);
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var rendering = view.LoadRegionAsync();
            PumpUntil(() => renderToken.CanBeCanceled);
            var unload = view.UnloadAsync();
            Assert.True(renderToken.IsCancellationRequested);
            pending.SetResult(candidate);
            Pump(unload);
            Assert.True(rendering.IsCanceled);
            Assert.Null(view.CurrentRenderSnapshot);
            Assert.Null(view.CurrentSection);
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { pending.TrySetResult(candidate); Pump(view.DisposeAsync().AsTask()); candidate.Dispose(); }
    });

    [Theory]
    [InlineData("AssetMemoryLimit")]
    [InlineData("SceneGraphLimit")]
    [InlineData("SceneInstanceLimit")]
    [InlineData("ModelSelectionLimit")]
    [InlineData("SceneVerificationLimit")]
    public void WorkloadLimitPromptsOnceAndRetriesWithLargeDataEnabled(string limitCode) => Run(() =>
    {
        var loaded = Section();
        var requests = new List<SceneRenderRequest>();
        string? prompt = null;
        var view = new SceneView
        {
            RenderProvider = (request, _, _) =>
            {
                requests.Add(request);
                return requests.Count == 1
                    ? Task.FromException<RenderAssetSnapshot>(new ArchiveException(limitCode, "Generated workload limit"))
                    : Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("Generated retry stop"));
            },
            LargeDataConfirmationProvider = (message, _) => { prompt = message; return Task.FromResult(true); }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var loading = view.LoadRegionAsync();
            Pump(Assert.ThrowsAsync<InvalidOperationException>(() => loading));
            Assert.Equal(2, requests.Count);
            Assert.False(requests[0].AllowLargeData);
            Assert.True(requests[1].AllowLargeData);
            Assert.Contains("region contains a lot of data", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.False(view.ActivityProgress.IsActive);
            Assert.Equal("Failed", view.ActivityProgress.Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void DecliningLargeDataConfirmationStopsWithoutRetry() => Run(() =>
    {
        var loaded = Section();
        int attempts = 0;
        var view = new SceneView
        {
            RenderProvider = (_, _, _) =>
            {
                attempts++;
                return Task.FromException<RenderAssetSnapshot>(new ArchiveException("AssetMemoryLimit", "Generated budget limit"));
            },
            LargeDataConfirmationProvider = (_, _) => Task.FromResult(false)
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var previous = Snapshot(new AssetMemoryBudget());
            typeof(SceneView).GetField("renderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, previous);
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.LoadRegionAsync()));
            Assert.Equal(1, attempts);
            Assert.Equal("Cancelled", view.ActivityProgress.Stage);
            Assert.False(view.ActivityProgress.IsActive);
            Assert.Same(previous, view.CurrentRenderSnapshot);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void AcceptedSceneBudgetHasAFiniteLeaseAndRestoresItsPreviousLimit()
    {
        var budget = new AssetMemoryBudget(1024);
        using (budget.AllowLargeSceneLoad())
        {
            Assert.Equal(2L * 1024 * 1024 * 1024, budget.MaximumBytes);
            using var reservation = budget.Reserve(2048, "generated scene workload");
            Assert.Equal(2048L, budget.UsedBytes);
        }
        Assert.Equal(1024L, budget.MaximumBytes);
        Assert.Equal(0L, budget.UsedBytes);
        Assert.Equal(65_536, SceneRenderSession.MaximumLargeCatalogOccurrences);
        Assert.Equal(65_536, NativeModelSession.MaximumLargeSelections);
        Assert.Equal(65_536, SourceExtentVerifier.MaximumLargeExtentCount);
    }

    [Fact]
    public void LargeRegionPreflightApprovalIsReusedByTheSceneRender() => Run(() =>
    {
        var loaded = SyntheticRegionsSection();
        int normalCalls = 0, largeCalls = 0, confirmations = 0;
        bool renderAllowedLargeData = false;
        var first = loaded.Result.Value!.CatalogInstances[0];
        var region = new SceneRenderRegion(first.Identity, "Region", new(Vector3.Zero, Vector3.One),
            new(Vector3.Zero, Vector3.One), 1);
        var view = new SceneView
        {
            RegionProvider = (_, _, _) =>
            {
                throw new InvalidOperationException("The legacy provider must not replace the large-data provider.");
            },
            RegionProviderWithLargeData = (_, allowLargeData, _, _) =>
            {
                if (!allowLargeData)
                {
                    normalCalls++;
                    return Task.FromException<SceneRegionDiscovery>(new ArchiveException("SceneInstanceLimit", "Generated preflight limit"));
                }
                largeCalls++;
                return Task.FromResult(new SceneRegionDiscovery([region], []));
            },
            LargeDataConfirmationProvider = (_, _) => { confirmations++; return Task.FromResult(true); },
            RenderProvider = (request, _, _) =>
            {
                renderAllowedLargeData = request.AllowLargeData;
                return Task.FromCanceled<RenderAssetSnapshot>(new CancellationToken(true));
            }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.LoadSceneAsync(loaded.Result.Identity)));
            Assert.Equal(1, normalCalls);
            Assert.Equal(1, largeCalls);
            Assert.Equal(1, confirmations);
            Assert.True(renderAllowedLargeData);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void AutomaticSceneLoadingFindsRegionsAndUsesOccupiedPreviewBounds() => Run(() =>
    {
        var loaded = SyntheticRegionsSection();
        var choices = loaded.Result.Value!.CatalogInstances;
        var emptyCenter = new GeometryBounds(new(20, 30, 40), new(40, 50, 60));
        SceneRenderRequest? request = null;
        int described = 0;
        var view = new SceneView
        {
            RegionProvider = (_, _, _) =>
            {
                described++;
                return Task.FromResult(new SceneRegionDiscovery([
                    new(choices[0].Identity, "Hidden", emptyCenter, emptyCenter, 100) { VisibleOccurrenceCount = 0 },
                    new(choices[1].Identity, "Main", emptyCenter, emptyCenter, 10) { VisibleOccurrenceCount = 10 }], []));
            },
            RenderProvider = (value, _, token) => { request = value; return Task.FromCanceled<RenderAssetSnapshot>(new CancellationToken(true)); }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            Assert.Equal(0, described); Assert.Null(request);
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.LoadSceneAsync(loaded.Result.Identity)));
            Assert.Equal(1, described);
            Assert.Equal(choices[1].Identity, request!.BranchInstance);
            Assert.Equal(emptyCenter, request.Region);
            Assert.Equal(SceneRenderScope.Region, request.Scope);
            Assert.Equal(256, request.MaximumPreviewDimension);
            Assert.Equal(2, view.CurrentRegions.Count);
            request = null;
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.LoadAllInstancesAsync()));
            Assert.Equal(SceneRenderScope.Scene, request!.Scope);
            Assert.Equal(1, described);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LoadAllButtonUsesTheConfiguredLargeDataRegionProvider() => Run(() =>
    {
        var loaded = SyntheticRegionsSection();
        var branch = loaded.Result.Value!.CatalogInstances[0];
        var bounds = new GeometryBounds(Vector3.Zero, Vector3.One);
        SceneRenderRequest? requested = null;
        var view = new SceneView
        {
            RegionProviderWithLargeData = (_, _, _, _) => Task.FromResult(
                new SceneRegionDiscovery([new(branch.Identity, "Region", bounds, bounds, 1)], [])),
            RenderProvider = (request, _, _) =>
            {
                requested = request;
                return Task.FromCanceled<RenderAssetSnapshot>(new CancellationToken(true));
            }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            Pump(view.SelectResourceAsync(loaded.Result.Identity));
            var button = (Button)view.FindName("LoadAllButton");
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => requested is not null);
            Assert.Equal(SceneRenderScope.Scene, requested!.Scope);
            Assert.Equal(branch.Identity, requested.BranchInstance);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SceneMetadataCancellationCannotApplyLateRegionsOrStartGeometry() => Run(() =>
    {
        var loaded = SyntheticRegionsSection();
        var pending = new TaskCompletionSource<SceneRegionDiscovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool described = false, rendered = false;
        var view = new SceneView
        {
            RegionProvider = (_, _, _) => { described = true; return pending.Task; },
            RenderProvider = (_, _, _) => { rendered = true; throw new InvalidOperationException("A stale region must not render."); }
        };
        try
        {
            Pump(view.LoadAsync(new Session(loaded)));
            var pipeline = view.LoadSceneAsync(loaded.Result.Identity);
            PumpUntil(() => described);
            var unloading = view.UnloadAsync();
            pending.SetResult(new([new(loaded.Result.Value!.CatalogInstances[0].Identity, "Region", new(Vector3.Zero, Vector3.One), new(Vector3.Zero, Vector3.One), 1)], []));
            Pump(unloading);
            Assert.True(pipeline.IsCanceled);
            Assert.False(rendered);
            Assert.Empty(view.CurrentRegions);
            Assert.Null(view.CurrentSection);
            Assert.False(view.IsLoading);
        }
        finally { pending.TrySetResult(new([], [])); Pump(view.DisposeAsync().AsTask()); }
    });

    private static SceneLoadedSection SyntheticRegionsSection()
    {
        var section = Section();
        var document = section.Result.Value!;
        var existing = document.CatalogInstances.Single();
        var extraEntity = existing.Entity with { Identity = existing.Entity.Identity with { MemberPath = "dc-object/2/2207/3" }, NativeId = 3 };
        var extra = existing with { Identity = extraEntity.Identity, Entity = extraEntity, InstanceName = "Second" };
        var updated = document with { CatalogInstances = [existing, extra], Entities = [existing.Entity, extraEntity] };
        return section with { Result = section.Result with { Value = updated },
            EntitiesByNode = new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [document.Hierarchy.Nodes[0].Identity] = updated.Entities } };
    }

    [Fact]
    public void TreeVisibilityPropagatesToEveryOccurrence()
    {
        var identity = Identity(1);
        var toggles = new List<(string? Key, bool Visible)>();
        var root = new RenderTreeRow(new("root", null, identity, "Root", Matrix4x4.Identity, Matrix4x4.Identity, null, []),
            (row, visible) => toggles.Add((row.Node.OccurrenceKey, visible)));
        foreach (var key in new[] { "first", "second" })
            root.Children.Add(new(new(key, "root", identity, key, Matrix4x4.Identity, Matrix4x4.Identity, key, []),
                (row, visible) => toggles.Add((row.Node.OccurrenceKey, visible))));
        root.IsVisible = false;
        Assert.All(root.Children, child => Assert.False(child.IsVisible));
        Assert.Equal(2, toggles.Count(item => item.Key is not null && !item.Visible));
        root.IsVisible = true;
        Assert.All(root.Children, child => Assert.True(child.IsVisible));
        Assert.Equal(2, toggles.Count(item => item.Key is not null && item.Visible));
    }

    [Fact]
    public void OccurrenceSelectionUsesTheSameTreeKeyAndInspector() => Run(() =>
    {
        var snapshot = Snapshot(new AssetMemoryBudget());
        var view = new SceneView();
        try
        {
            typeof(SceneView).GetField("renderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, snapshot);
            typeof(SceneView).GetMethod("BuildRenderHierarchy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [snapshot]);
            ((TabItem)view.FindName("RenderedTab")).IsSelected = true;
            view.Measure(new Size(1200, 700)); view.Arrange(new Rect(0, 0, 1200, 700)); view.UpdateLayout();
            var occurrence = snapshot.Occurrences.Single();
            view.SelectOccurrence(occurrence.Key);
            Assert.Same(occurrence, view.SelectedOccurrence);
            var tree = (TreeView)view.FindName("RenderHierarchy");
            Assert.Equal(occurrence.Key, Assert.IsType<RenderTreeRow>(tree.SelectedItem).Node.OccurrenceKey);
            Assert.Contains(occurrence.Entity.Key, ((TextBlock)view.FindName("IdentityText")).Text);
            view.SelectOccurrence(null);
            Assert.Null(view.SelectedOccurrence);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void PickingRepeatedMeshesAcrossCollapsedParentsSelectsTheRealSecondTreeItem() => Run(() =>
    {
        var original = Snapshot(new AssetMemoryBudget());
        var first = original.Occurrences.Single() with { Key = "first" };
        var second = first with { Key = "second", Entity = Identity(30), Node = Identity(31), World = Matrix4x4.CreateTranslation(8, 0, 0) };
        var nodes = Enumerable.Range(0, 40).Select(index => new RenderHierarchyNode("root-" + index, null, Identity(100 + index),
            "Parent " + index, Matrix4x4.Identity, Matrix4x4.Identity, null, [])).ToList();
        nodes.Add(new("branch-first", "root-0", first.Node, "Branch", Matrix4x4.Identity, Matrix4x4.Identity, null, []));
        nodes.Add(new("branch-second", "root-39", second.Node, "Branch", Matrix4x4.Identity, Matrix4x4.Identity, null, []));
        nodes.Add(new("first", "branch-first", first.Node, first.Label, Matrix4x4.Identity, first.World, first.Key, []));
        nodes.Add(new("second", "branch-second", second.Node, second.Label, Matrix4x4.Identity, second.World, second.Key, []));
        var snapshot = new RenderAssetSnapshot(original.Revision, original.Meshes, [first, second], original.Materials,
            original.Region, [], nodes, new(2,2,0,2,1,2), [], original.MemoryBudget, [original.AcquireLease()]);
        var view = new SceneView();
        try
        {
            typeof(SceneView).GetField("renderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, snapshot);
            typeof(SceneView).GetMethod("BuildRenderHierarchy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [snapshot]);
            ((TabItem)view.FindName("RenderedTab")).IsSelected = true;
            view.Measure(new Size(1200, 500)); view.Arrange(new Rect(0, 0, 1200, 500)); view.UpdateLayout();
            var tree = (TreeView)view.FindName("RenderHierarchy");
            view.SelectOccurrence(first.Key);
            PumpUntil(() => tree.SelectedItem is RenderTreeRow row && row.Node.OccurrenceKey == first.Key);
            var roots = tree.Items.Cast<RenderTreeRow>().ToArray();
            roots[0].IsExpanded = false; roots[39].IsExpanded = false;
            ((TabControl)view.FindName("SceneTabs")).SelectedIndex = 0;
            view.SelectOccurrence(second.Key);
            PumpUntil(() => tree.SelectedItem is RenderTreeRow row && row.Node.OccurrenceKey == second.Key);
            Assert.True(roots[39].IsExpanded);
            Assert.True(roots[39].Children[0].IsExpanded);
            Assert.Same(second, view.SelectedOccurrence);
            Assert.Contains(second.Entity.Key, ((TextBlock)view.FindName("IdentityText")).Text);
            Assert.False(roots[0].Children[0].Children[0].IsSelected);
            Assert.True(roots[39].Children[0].Children[0].IsSelected);
            view.SelectOccurrence(first.Key); view.SelectOccurrence(second.Key);
            PumpUntil(() => tree.SelectedItem is RenderTreeRow row && row.Node.OccurrenceKey == second.Key);
            Assert.Same(second, view.SelectedOccurrence);
        }
        finally { Pump(view.DisposeAsync().AsTask()); original.Dispose(); }
    });

    [Fact]
    public void ModelProjectionReusesGeometryAndRetainsItsParentUntilClosed()
    {
        var budget = new AssetMemoryBudget(4096);
        var scene = Snapshot(budget, [budget.Reserve(128, "generated geometry")]);
        var model = AssetModelView.CreateModelSnapshot(scene, scene.Occurrences.Single());
        Assert.Same(scene.Meshes.Values.Single(), model.Meshes.Values.Single());
        Assert.Same(scene.Materials, model.Materials);
        Assert.Same(scene.MemoryBudget, model.MemoryBudget);
        Assert.Equal(Matrix4x4.Identity, model.Occurrences.Single().World);
        Assert.Equal(new Vector3(0, 0, 0), model.Region.Minimum);
        Assert.Equal(new Vector3(1, 1, 0), model.Region.Maximum);
        scene.Dispose();
        Assert.Equal(128, budget.UsedBytes);
        model.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void ModelViewBorrowsSnapshotAndDisplaysEveryModelLocalPlacement() => Run(() =>
    {
        var budget = new AssetMemoryBudget(4096);
        var retained = budget.Reserve(128, "generated model snapshot");
        var snapshot = ModelLocalSnapshot(budget, retained);
        var service = new AssetViewportService();
        var view = new AssetModelView();
        view.SetViewportService(service);
        try
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(view.LoadAsync(snapshot, cancelled.Token)));
            Assert.Equal(128, budget.UsedBytes);
            Pump(view.LoadAsync(snapshot));
            Assert.Same(snapshot, view.CurrentSnapshot);
            Assert.Equal("Models · 2 groups", ((TextBlock)view.FindName("ModelName")).Text);
            var details = ((TextBox)view.FindName("ModelDetails")).Text;
            Assert.Contains("Model-local mesh", details);
            Assert.Contains("Group 0", details);
            Assert.Contains("Group 1", details);
            Assert.Contains("Placement Model local", details);
            snapshot.Dispose();
            Assert.Equal(128, budget.UsedBytes);
            Pump(view.LoadAsync(snapshot));
            Assert.Same(snapshot, view.CurrentSnapshot);
            Assert.Equal(128, budget.UsedBytes);
            Pump(view.UnloadAsync());
            Assert.Equal(0, budget.UsedBytes);
        }
        finally
        {
            Pump(view.DisposeAsync().AsTask());
            snapshot.Dispose();
            service.Dispose();
        }
    });

    [Fact]
    public void IndividualHiddenModelPreservesSourceVisibilityAndNativeFlags()
    {
        using var scene = Snapshot(new AssetMemoryBudget());
        var hidden = scene.Occurrences.Single() with { SourceVisible = false, NativeFlags = 2 };
        using var model = AssetModelView.CreateModelSnapshot(scene, hidden);
        Assert.False(model.Occurrences.Single().SourceVisible);
        Assert.Equal((uint)2, model.Occurrences.Single().NativeFlags);
        Assert.Equal(Matrix4x4.Identity, model.Occurrences.Single().World);
    }

    [Fact]
    public void CancelledModelProjectionAcquiresNoParentLease()
    {
        var budget = new AssetMemoryBudget(4096);
        var scene = Snapshot(budget, [budget.Reserve(128, "generated geometry")]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AssetModelView.CreateModelSnapshot(scene, scene.Occurrences.Single(), cancellation.Token));
        scene.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    private static ResourceIdentity Identity(long ordinal) => new(new string('a', 64), ordinal);
    private static SourceRevision Revision => new(new string('a', 64), "generated-revision");
    private static RenderAssetSnapshot Snapshot(AssetMemoryBudget budget, IEnumerable<IDisposable>? resources = null)
    {
        var bounds = new GeometryBounds(Vector3.Zero, new(1, 1, 0));
        var primitive = new MeshPrimitive("primitive", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2], IndexElementFormat.UInt16, [], Bounds: bounds);
        var mesh = new RenderMesh(Identity(2), 0, new(new("generated", "1"), [], [primitive], new Dictionary<string, string>()));
        var occurrence = new RenderOccurrence("occurrence", Identity(3), Identity(4), "Object", mesh.Key,
            Matrix4x4.CreateTranslation(4, 5, 6), bounds, [Identity(5)]);
        var hierarchy = new RenderHierarchyNode("occurrence", null, occurrence.Node, occurrence.Label,
            Matrix4x4.Identity, occurrence.World, occurrence.Key, []);
        return new(Revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, [occurrence],
            new Dictionary<ResourceIdentity, RenderMaterial>(), bounds, [], [hierarchy], new(1, 1, 0, 1, 1, 1), [], budget, resources);
    }

    private static RenderAssetSnapshot ModelLocalSnapshot(AssetMemoryBudget budget, IDisposable retained)
    {
        var revision = Revision;
        var bounds = new GeometryBounds(Vector3.Zero, new(1, 1, 0));
        var geometry = new GeometryAsset(new("generated", "1"), [], [], new Dictionary<string, string>());
        var first = new RenderMesh(Identity(20), 0, geometry);
        var second = new RenderMesh(Identity(21), 1, geometry);
        var meshes = new Dictionary<string, RenderMesh> { [first.Key] = first, [second.Key] = second };
        var occurrences = new[]
        {
            LocalOccurrence(first, 0), LocalOccurrence(second, 1)
        };
        return new(revision, meshes, occurrences, new Dictionary<ResourceIdentity, RenderMaterial>(), bounds,
            [], [], new(0, 0, 0, 0, 0, 0), [], budget, [retained]);

        static RenderOccurrence LocalOccurrence(RenderMesh mesh, int group) => new(
            $"model-local/{mesh.Key}", mesh.Identity, mesh.Identity, $"Mesh group {group}", mesh.Key,
            Matrix4x4.Identity, new(Vector3.Zero, new(1, 1, 0)), [])
        {
            PlacementOrigin = RenderPlacementOrigin.ModelLocal,
            ModelPlacement = new(mesh.Identity, group),
            SourceVisibilityKnown = false,
            SourceVisible = true
        };
    }
    private static SceneLoadedSection Section()
    {
        var container = Identity(0);
        var nodeIdentity = container with { MemberPath = "dc-object/0/2002/1" };
        var transform = new SourceTransform([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1], MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var node = new SceneNodeRecord(nodeIdentity, Revision, 1, 0, Vector3.Zero, transform, transform, null, null, null, -1, 0, 0,
            new(container, 0, 196), new Dictionary<string, DecodedExtent>(), new byte[196]);
        var hierarchyNode = new SceneHierarchyNode(node, null, null, null, null, [], transform, transform, SceneTransformStatus.Available);
        var entity = new SceneEntityRecord(container with { MemberPath = "dc-object/1/2207/2" }, 2207, 2,
            new(IdentifierNamespace.Native, "1", container, 2002), [], 0, 0, 4, 0, new(container, 196, 48), new Dictionary<string, DecodedExtent>());
        var branch = new SceneCatalogInstance(entity.Identity, entity, "Branch", "Catalog", new(IdentifierNamespace.Native, "3", container, 3), new Dictionary<string, DecodedExtent>());
        var environment = new SceneEnvironmentRecord(container with { MemberPath = "environment" }, 1, new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty);
        var document = new SceneSectionDocument(container with { MemberPath = "scene" }, container, Revision,
            new(container, Revision, [hierarchyNode], [nodeIdentity], []), [entity], [branch], environment, [], []);
        var result = new DecodeResult<SceneSectionDocument>(document, document.Identity, Revision, [], [], [], [], new Dictionary<string, string>());
        return new(new(document.Identity, Revision, "scene", "Scene", null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []),
            result, new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [nodeIdentity] = [entity] },
            new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>());
    }
    private sealed class Session(SceneLoadedSection loaded) : ISceneCatalogSession
    {
        public ArchiveSnapshot Snapshot => null!;
        public SourceRevision Revision => loaded.Result.Revision;
        public SceneDiscoveryProgress Progress => new(false, 1, 1, 1, false);
        public event EventHandler? Updated { add { } remove { } }
        public Task DiscoverMoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default) => Task.FromResult(new SceneCatalogPage([loaded.Descriptor], 1, 0));
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) => Task.FromResult(loaded);
        public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Task.CompletedTask;
        public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Task.CompletedTask;
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Scene rendering view test timed out.");
        if (failure is not null) throw new InvalidOperationException("Scene rendering view test failed.", failure);
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
    }
}
