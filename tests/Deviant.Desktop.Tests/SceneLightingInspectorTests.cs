using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneLightingInspectorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RenderedSceneReturnRestoresTheSelectedLightAndReleasesItsState(int selectedTab) => Run(() =>
    {
        var section = Section(0);
        var light = section.IblLightsByEntity.Values.Last().Result.Identity;
        var budget = new AssetMemoryBudget(8192);
        using var service = new AssetViewportService();
        var view = new SceneView();
        view.SetViewportService(service);
        SceneRenderViewState? state = null;
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(light));
            var snapshot = EmptySnapshot(section.Result.Revision, budget);
            Set(view, "renderSnapshot", snapshot);
            Set(view, "lastSuccessfulSceneRenderRequest", new SceneRenderRequest(section, view.SelectedBranchIdentity!, view.Region));
            Set(view, "lastSuccessfulRenderBudget", budget);
            ((TabControl)view.FindName("SceneTabs")).SelectedIndex = selectedTab;
            state = Assert.IsType<SceneRenderViewState>(view.CaptureRenderState());
            Assert.Equal(light, state.SelectedStructureIdentity);
            Assert.Null(state.SelectedOccurrenceKey);
            Pump(view.ClearRenderingAsync());
            Assert.Equal(state.ReservedBytes, budget.UsedBytes);
            Assert.Equal(Visibility.Collapsed, Control<StackPanel>(view, "LightingPanel").Visibility);
            view.RenderProvider = (_, _, _) => Task.FromResult(EmptySnapshot(section.Result.Revision, budget));
            Pump(view.RestoreRenderStateAsync(state));
            Assert.Equal(light, Selected(view).Result.Identity);
            Assert.Equal("2", Control<TextBlock>(view, "LightIntensity").Text);
            Assert.Equal(1, Control<TabControl>(view, "SceneTabs").SelectedIndex);
            state.Dispose(); state = null;
            Pump(view.ClearRenderingAsync());
            Assert.Equal(0, budget.UsedBytes);
            Assert.Equal(0, service.GpuBytes);
        }
        finally { state?.Dispose(); Pump(view.DisposeAsync().AsTask()); Assert.Equal(0, budget.UsedBytes); }
    });

    [Fact]
    public void ReopenedApprovedLargeSceneRestoresLightRegionCameraAndBudgetWithoutReprompting() => Run(() =>
    {
        var section = Section(7);
        var light = section.IblLightsByEntity.Values.Last().Result.Identity;
        var budget = new AssetMemoryBudget(8192);
        using var service = new AssetViewportService();
        var view = new SceneView();
        SceneRenderViewState? state = null;
        int approvals = 0;
        SceneRenderRequest? restoredRequest = null;
        try
        {
            view.SetViewportService(service);
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(light));
            var region = new GeometryBounds(new(11, 12, 13), new(31, 32, 33));
            view.SetRegion(region);
            var camera = Assert.IsType<HelixToolkit.Wpf.SharpDX.PerspectiveCamera>(view.Viewport.Viewport.Camera);
            camera.Position = new(7, 8, 9);
            camera.LookDirection = new(-1, -2, -3);
            camera.UpDirection = new(0, 1, 0);
            camera.FieldOfView = 61;

            var request = new SceneRenderRequest(section, view.SelectedBranchIdentity!, region, SceneRenderScope.Scene)
            { MaximumPreviewDimension = 256, AllowLargeData = true };
            Set(view, "renderSnapshot", EmptySnapshot(section.Result.Revision, budget));
            Set(view, "lastSuccessfulSceneRenderRequest", request);
            Set(view, "lastSuccessfulRenderBudget", budget);
            typeof(SceneView).GetMethod("ApproveLargeData", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [section]);
            state = Assert.IsType<SceneRenderViewState>(view.CaptureRenderState());
            Assert.True(state.AllowLargeData);
            Assert.Equal(light, state.SelectedStructureIdentity);

            Pump(view.ClearRenderingAsync());
            var reopened = Section(7);
            Assert.NotSame(section, reopened);
            Assert.Equal(section.Descriptor.Identity, reopened.Descriptor.Identity);
            Assert.Equal(section.Result.Revision, reopened.Result.Revision);
            Pump(view.LoadAsync(new Session(reopened)));
            Pump(view.SelectResourceAsync(light));
            Assert.False((bool)typeof(SceneView).GetMethod("HasLargeDataApproval", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [reopened])!);

            view.LargeDataConfirmationProvider = (_, _) =>
            { approvals++; return Task.FromResult(false); };
            view.RenderProvider = (value, _, _) =>
            {
                restoredRequest = value;
                return value.AllowLargeData
                    ? Task.FromResult(EmptySnapshot(reopened.Result.Revision, budget))
                    : Task.FromException<RenderAssetSnapshot>(new ArchiveException("SceneInstanceLimit", "Generated large scene"));
            };
            camera.Position = new(-4, -5, -6);
            camera.LookDirection = new(4, 5, 6);
            camera.FieldOfView = 40;

            Pump(view.RestoreRenderStateAsync(state));
            Assert.Equal(0, approvals);
            Assert.NotNull(restoredRequest);
            Assert.True(restoredRequest!.AllowLargeData);
            Assert.Equal(SceneRenderScope.Scene, restoredRequest.Scope);
            Assert.Equal(256, restoredRequest.MaximumPreviewDimension);
            Assert.Equal(region, restoredRequest.Region);
            Assert.Same(budget, view.CurrentRenderSnapshot!.MemoryBudget);
            Assert.Equal(region, view.Region);
            Assert.Equal(new System.Windows.Media.Media3D.Point3D(7, 8, 9), camera.Position);
            Assert.Equal(new System.Windows.Media.Media3D.Vector3D(-1, -2, -3), camera.LookDirection);
            Assert.Equal(61, camera.FieldOfView);
            Assert.Equal(light, Selected(view).Result.Identity);
            Pump(view.LoadRegionAsync());
            Assert.True(restoredRequest!.AllowLargeData);
            Assert.Equal(0, approvals);

            state.Dispose(); state = null;
            Pump(view.ClearRenderingAsync());
            Assert.Equal(0, budget.UsedBytes);
            Assert.Equal(0, service.GpuBytes);
        }
        finally { state?.Dispose(); Pump(view.DisposeAsync().AsTask()); Assert.Equal(0, budget.UsedBytes); }
    });

    [Fact]
    public void RestoreRejectsAKnownDifferentRenderBudgetBeforeCallingProvider() => Run(() =>
    {
        var section = Section(0);
        var budget = new AssetMemoryBudget(8192);
        var otherBudget = new AssetMemoryBudget(8192);
        using var service = new AssetViewportService();
        var view = new SceneView();
        SceneRenderViewState? state = null;
        int providerCalls = 0;
        try
        {
            view.SetViewportService(service);
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(section.IblLightsByEntity.Keys.Last()));
            Set(view, "renderSnapshot", EmptySnapshot(section.Result.Revision, budget));
            Set(view, "lastSuccessfulSceneRenderRequest", new SceneRenderRequest(section,
                view.SelectedBranchIdentity!, view.Region));
            Set(view, "lastSuccessfulRenderBudget", budget);
            state = Assert.IsType<SceneRenderViewState>(view.CaptureRenderState());
            Pump(view.ClearRenderingAsync());
            Set(view, "lastSuccessfulRenderBudget", otherBudget);
            view.RenderProvider = (_, _, _) =>
            { providerCalls++; return Task.FromResult(EmptySnapshot(section.Result.Revision, budget)); };

            Assert.Throws<InvalidOperationException>(() => Pump(view.RestoreRenderStateAsync(state)));
            Assert.Equal(0, providerCalls);
            state.Dispose(); state = null;
        }
        finally { state?.Dispose(); Pump(view.DisposeAsync().AsTask()); Assert.Equal(0, budget.UsedBytes); }
    });

    [Fact]
    public void RestoreRejectsProviderSnapshotFromDifferentBudgetBeforePublication() => Run(() =>
    {
        var section = Section(0);
        var budget = new AssetMemoryBudget(8192);
        var providerBudget = new AssetMemoryBudget(8192);
        using var service = new AssetViewportService();
        var view = new SceneView();
        SceneRenderViewState? state = null;
        int providerCalls = 0;
        try
        {
            view.SetViewportService(service);
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(section.IblLightsByEntity.Keys.Last()));
            Set(view, "renderSnapshot", EmptySnapshot(section.Result.Revision, budget));
            Set(view, "lastSuccessfulSceneRenderRequest", new SceneRenderRequest(section,
                view.SelectedBranchIdentity!, view.Region) { AllowLargeData = true });
            Set(view, "lastSuccessfulRenderBudget", budget);
            state = Assert.IsType<SceneRenderViewState>(view.CaptureRenderState());
            Pump(view.ClearRenderingAsync());
            var reopened = Section(0);
            Pump(view.LoadAsync(new Session(reopened)));
            Pump(view.SelectResourceAsync(reopened.Result.Identity));
            view.RenderProvider = (_, _, _) =>
            { providerCalls++; return Task.FromResult(EmptySnapshot(reopened.Result.Revision, providerBudget)); };

            Assert.Throws<InvalidOperationException>(() => Pump(view.RestoreRenderStateAsync(state)));
            Assert.Equal(1, providerCalls);
            Assert.Null(view.CurrentRenderSnapshot);
            Assert.False((bool)typeof(SceneView).GetMethod("HasLargeDataApproval", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [reopened])!);
            Assert.Equal(0, service.GpuBytes);
            Assert.Equal(0, providerBudget.UsedBytes);
            state.Dispose(); state = null;
        }
        finally { state?.Dispose(); Pump(view.DisposeAsync().AsTask()); Assert.Equal(0, budget.UsedBytes); }
    });

    [Fact]
    public void RestoreRejectsADifferentSourceRevisionBeforeCallingProvider() => Run(() =>
    {
        var section = Section(0);
        var budget = new AssetMemoryBudget(8192);
        using var service = new AssetViewportService();
        var view = new SceneView();
        SceneRenderViewState? state = null;
        int providerCalls = 0;
        try
        {
            view.SetViewportService(service);
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(section.IblLightsByEntity.Keys.Last()));
            Set(view, "renderSnapshot", EmptySnapshot(section.Result.Revision, budget));
            Set(view, "lastSuccessfulSceneRenderRequest", new SceneRenderRequest(section,
                view.SelectedBranchIdentity!, view.Region));
            Set(view, "lastSuccessfulRenderBudget", budget);
            state = Assert.IsType<SceneRenderViewState>(view.CaptureRenderState());
            Pump(view.ClearRenderingAsync());

            var otherRevision = new SourceRevision("generated-other-index", "generated-other-revision");
            var otherSection = Section(0, revisionOverride: otherRevision);
            Pump(view.LoadAsync(new Session(otherSection)));
            Pump(view.SelectResourceAsync(otherSection.Result.Identity));
            view.RenderProvider = (_, _, _) =>
            { providerCalls++; return Task.FromResult(EmptySnapshot(otherSection.Result.Revision, budget)); };

            Assert.Throws<InvalidOperationException>(() => Pump(view.RestoreRenderStateAsync(state)));
            Assert.Equal(0, providerCalls);
            state.Dispose(); state = null;
        }
        finally { state?.Dispose(); Pump(view.DisposeAsync().AsTask()); Assert.Equal(0, budget.UsedBytes); }
    });

    [Fact]
    public void UnavailableLightKeepsItsIdentityAndDiagnostics() => Run(() =>
    {
        var section = Section(0);
        var lights = section.IblLightsByEntity.ToDictionary(pair => pair.Key, pair => pair.Value);
        var identity = lights.Keys.Last();
        lights[identity] = new(lights[identity].Result with { Value = null, Diagnostics = ["UnsupportedVersion"] }, []);
        section = section with { IblLightsByEntity = lights };
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(identity));
            Assert.Equal(identity, Selected(view).Result.Identity);
            Assert.Contains("Unavailable", Selected(view).Label);
            Assert.Equal(Visibility.Collapsed, Control<StackPanel>(view, "LightingValues").Visibility);
            Assert.Contains("UnsupportedVersion", Control<TextBox>(view, "LightingDetails").Text);
            Assert.False(Control<Button>(view, "QueryTextures").IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ExplicitEntityAndComboSelectionKeepEachLightsFields() => Run(() =>
    {
        var section = Section(0);
        var lights = section.IblLightsByEntity.Values.ToArray();
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(lights[1].Result.Identity));
            Assert.Equal(Visibility.Visible, Control<StackPanel>(view, "LightingPanel").Visibility);
            Assert.Equal(lights[1].Result.Identity, Selected(view).Result.Identity);
            Assert.Equal("2", Control<TextBlock>(view, "LightIntensity").Text);
            Control<ComboBox>(view, "LightingChoice").SelectedIndex = 0;
            Assert.Equal(lights[0].Result.Identity, Selected(view).Result.Identity);
            Assert.Equal("1", Control<TextBlock>(view, "LightIntensity").Text);
            Assert.Contains(lights[0].Result.Identity.Key, Control<TextBox>(view, "LightingDetails").Text);
            Pump(view.SelectResourceAsync(section.Result.Value!.Hierarchy.Nodes[0].Identity));
            Assert.Equal(lights[0].Result.Identity, Selected(view).Result.Identity);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void QueriesOnlySelectedLightAndOpensTheExactNativeTexture() => Run(() =>
    {
        var section = Section(0);
        var fields = section.IblLightsByEntity.Values.Last().Result.Value!;
        ResourceIdentity? requested = null;
        var entry = SceneLightingTextureTests.Entry(fields, fields.TextureReferences[0],
            new(fields.Identity.IndexContentId, 7), "Generated texture");
        var navigator = new Navigator();
        RenderTextureSelection? opened = null;
        var view = new SceneView
        {
            AssetNavigator = navigator,
            LightingTexturePreviewProvider = (selection, token) => { opened = selection; return Task.CompletedTask; },
            TextureOwnerQueryProvider = (owner, offset, limit, token) =>
            {
                requested = owner;
                return Task.FromResult(SceneLightingTextureTests.Result(fields, [entry]));
            }
        };
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(fields.Identity));
            Control<Button>(view, "QueryTextures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fields.Identity, requested);
            var results = Control<ListBox>(view, "TextureResults");
            var row = Assert.IsType<SceneLightingTextureRow>(Assert.Single(results.Items.Cast<object>()));
            Assert.Equal(entry.Identity, row.Target);
            results.SelectedItem = row;
            results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent });
            Assert.Equal(row.Preview, opened);
            Assert.Equal(entry.Identity, opened!.SourceIdentity);
            Assert.Equal(section.IblLightsByEntity[fields.Identity].TextureResolutions[0].Candidates[0], opened.NativeIdentity);
            Assert.Equal(new ResourceIdentity(fields.Identity.IndexContentId, 90), opened.CatalogIdentity);
            Assert.Equal(fields.Revision, opened.Revision);
            Assert.Null(navigator.Identity);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LightingRowsWithoutAnExactRouteDoNotUseTheGenericNavigator() => Run(() =>
    {
        var section = Section(0);
        var fields = section.IblLightsByEntity.Values.Last().Result.Value!;
        var entry = SceneLightingTextureTests.Entry(fields, fields.TextureReferences[0],
            new(fields.Identity.IndexContentId, 7), "Generated texture", catalogEvidence: []);
        var navigator = new Navigator();
        var previewCalls = 0;
        var view = new SceneView
        {
            AssetNavigator = navigator,
            LightingTexturePreviewProvider = (_, _) => { previewCalls++; return Task.CompletedTask; },
            TextureOwnerQueryProvider = (_, _, _, _) => Task.FromResult(SceneLightingTextureTests.Result(fields, [entry]))
        };
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(fields.Identity));
            Pump(Query(view));
            var results = Control<ListBox>(view, "TextureResults");
            results.SelectedItem = Assert.Single(results.Items.Cast<object>());
            results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent });
            Assert.Equal(0, previewCalls);
            Assert.Null(navigator.Identity);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OldOwnerQueryCannotReplaceTheNewLightSelection() => Run(() =>
    {
        var section = Section(0);
        var fields = section.IblLightsByEntity.Values.Select(item => item.Result.Value!).ToArray();
        var pending = new TaskCompletionSource<TextureOwnerQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var newer = SceneLightingTextureTests.Entry(fields[1], fields[1].TextureReferences[0],
            new(fields[1].Identity.IndexContentId, 8), "New texture");
        var view = new SceneView
        {
            TextureOwnerQueryProvider = (owner, offset, limit, token) => owner == fields[0].Identity
                ? Delay(token) : Task.FromResult(SceneLightingTextureTests.Result(fields[1], [newer]))
        };
        Task<TextureOwnerQueryResult> Delay(CancellationToken token) { oldToken = token; return pending.Task; }
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(fields[0].Identity));
            var old = Query(view);
            Control<ComboBox>(view, "LightingChoice").SelectedIndex = 1;
            Assert.True(oldToken.IsCancellationRequested);
            Pump(Query(view));
            pending.SetResult(SceneLightingTextureTests.Result(fields[0], []));
            Pump(old);
            var row = Assert.IsType<SceneLightingTextureRow>(Assert.Single(Control<ListBox>(view, "TextureResults").Items.Cast<object>()));
            Assert.Equal(newer.Identity, row.Target);
            Assert.Equal(fields[1].Identity, Selected(view).Result.Identity);
        }
        finally { pending.TrySetResult(SceneLightingTextureTests.Result(fields[0], [])); Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SectionChangeDiscardsAnOldSameRevisionQuery() => Run(() =>
    {
        var first = Section(0); var second = Section(1);
        var fields = first.IblLightsByEntity.Values.First().Result.Value!;
        var pending = new TaskCompletionSource<TextureOwnerQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var view = new SceneView { TextureOwnerQueryProvider = (owner, offset, limit, cancellation) =>
            { token = cancellation; return pending.Task; } };
        try
        {
            Pump(view.LoadAsync(new Session(first, second)));
            Pump(view.SelectResourceAsync(fields.Identity));
            var old = Query(view);
            Pump(view.SelectResourceAsync(second.Result.Identity));
            Assert.True(token.IsCancellationRequested);
            var expected = second.IblLightsByEntity.Values.First().Result.Identity;
            pending.SetResult(SceneLightingTextureTests.Result(fields, []));
            Pump(old);
            Assert.Same(second, view.CurrentSection);
            Assert.Equal(expected, Selected(view).Result.Identity);
            var row = Assert.IsType<SceneLightingTextureRow>(Assert.Single(Control<ListBox>(view, "TextureResults").Items.Cast<object>()));
            Assert.Null(row.Target);
            Assert.DoesNotContain("Unavailable", row.Label);
        }
        finally { pending.TrySetResult(SceneLightingTextureTests.Result(fields, [])); Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SuspensionDrainsBothCurrentAndSupersededOwnerQueries() => Run(() =>
    {
        var section = Section(0);
        var fields = section.IblLightsByEntity.Values.Select(item => item.Result.Value!).ToArray();
        var pending = fields.Select(_ => new TaskCompletionSource<TextureOwnerQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var view = new SceneView { TextureOwnerQueryProvider = (owner, offset, limit, token) =>
            pending[owner == fields[0].Identity ? 0 : 1].Task };
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(fields[0].Identity));
            var older = Query(view);
            Control<ComboBox>(view, "LightingChoice").SelectedIndex = 1;
            var newer = Query(view);
            var suspension = view.SuspendAsync();
            Assert.False(suspension.IsCompleted);
            pending[1].SetResult(SceneLightingTextureTests.Result(fields[1], []));
            Pump(newer);
            Assert.False(suspension.IsCompleted);
            pending[0].SetResult(SceneLightingTextureTests.Result(fields[0], []));
            Pump(Task.WhenAll(older, suspension));
        }
        finally
        {
            for (var index = 0; index < pending.Length; index++) pending[index].TrySetResult(SceneLightingTextureTests.Result(fields[index], []));
            Pump(view.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public void DisplaysNonfiniteBitsAndClearsLightingOnRenderedSelection() => Run(() =>
    {
        var section = Section(0, secondIntensityBits: 0x7FC12345);
        var second = section.IblLightsByEntity.Values.Last().Result;
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(new Session(section)));
            Pump(view.SelectResourceAsync(second.Identity));
            Assert.Equal("NaN", Control<TextBlock>(view, "LightIntensity").Text);
            Assert.Contains("0x7FC12345", Control<TextBox>(view, "LightingDetails").Text);
            view.SelectOccurrence(null);
            Assert.Equal(Visibility.Collapsed, Control<StackPanel>(view, "LightingPanel").Visibility);
            Assert.Empty(Control<TextBox>(view, "LightingDetails").Text);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    private static SceneLoadedSection Section(long ordinal, uint secondIntensityBits = 0x40000000,
        SourceRevision? revisionOverride = null)
    {
        var revision = revisionOverride ?? new SourceRevision("generated-index", "generated-revision");
        var container = new ResourceIdentity(revision.IndexContentId, ordinal);
        var nodeIdentity = container with { MemberPath = "node" };
        var matrix = new SourceTransform([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var node = new SceneNodeRecord(nodeIdentity, revision, 1, 0, Vector3.Zero, matrix, matrix, null, null, null,
            -1, 0, 0, new(container, 0, 196), new Dictionary<string, DecodedExtent>(), new byte[196]);
        var hierarchy = new SceneHierarchy(container, revision,
            [new(node, null, null, null, null, [], matrix, matrix, SceneTransformStatus.Available)], [nodeIdentity], []);
        var entities = new List<SceneEntityRecord>();
        var lights = new Dictionary<ResourceIdentity, SceneIblLight>();
        for (var index = 0; index < 2; index++)
        {
            var identity = container with { MemberPath = $"light/{index}" };
            var fields = SceneLightingTextureTests.FieldsFor(identity, revision, $"Light {index + 1}",
                index == 0 ? 0x3F800000 : secondIntensityBits, (2137, (uint)(index + 10)));
            var result = new DecodeResult<IblLightStoredFields>(fields, identity, revision, [], [], [], [], new Dictionary<string, string>());
            lights.Add(identity, new(result, [new(ReferenceStatus.Unique, [container with { MemberPath = $"texture/{index}" }])]));
            entities.Add(new(identity, 2211, (uint)(index + 2), new(IdentifierNamespace.Native, "1", identity, 2002),
                [], 0, 0, 4, 81, new(container, 196 + index * 512, 256), new Dictionary<string, DecodedExtent>()));
        }
        var branchEntity = new SceneEntityRecord(container with { MemberPath = "branch" }, 2207, 99,
            new(IdentifierNamespace.Native, "1", container, 2002), [], 0, 0, 4, 81,
            new(container, 2048, 128), new Dictionary<string, DecodedExtent>());
        entities.Add(branchEntity);
        var branch = new SceneCatalogInstance(branchEntity.Identity, branchEntity, "Branch", "Catalog",
            new(IdentifierNamespace.Native, "100", branchEntity.Identity, 3), new Dictionary<string, DecodedExtent>());
        var environment = new SceneEnvironmentRecord(container with { MemberPath = "environment" }, 1, new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty);
        var sectionIdentity = container with { MemberPath = SceneSectionReader.MemberPath };
        var document = new SceneSectionDocument(sectionIdentity, container, revision, hierarchy, entities, [branch], environment, [], []);
        var decoded = new DecodeResult<SceneSectionDocument>(document, sectionIdentity, revision, [], [], [], [], new Dictionary<string, string>());
        var descriptor = new AssetDescriptor(sectionIdentity, revision, "scene", "Generated scene", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []);
        return new(descriptor, decoded, new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [nodeIdentity] = entities },
            new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>()) { IblLightsByEntity = lights };
    }

    private static T Control<T>(SceneView view, string name) where T : FrameworkElement => (T)view.FindName(name);
    private static void Set(SceneView view, string name, object value) =>
        typeof(SceneView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
    private static RenderAssetSnapshot EmptySnapshot(SourceRevision revision, AssetMemoryBudget budget) =>
        new(revision, new Dictionary<string, RenderMesh>(), [], new Dictionary<ResourceIdentity, RenderMaterial>(),
            new(Vector3.Zero, Vector3.One), [], [], new(0, 0, 0, 0, 0, 0), [], budget,
            [budget.Reserve(512, "Generated scene")]);
    private static SceneLightingChoice Selected(SceneView view) => Assert.IsType<SceneLightingChoice>(Control<ComboBox>(view, "LightingChoice").SelectedItem);
    private static Task Query(SceneView view) => (Task)typeof(SceneView).GetMethod("QueryTexturesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Lighting inspector test timed out.");
        if (failure is not null) throw new InvalidOperationException("Lighting inspector test failed.", failure);
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    private sealed class Navigator : IAssetNavigator
    {
        public ResourceIdentity? Identity { get; private set; }
        public SourceRevision? Revision { get; private set; }
        public Task OpenAsync(ResourceIdentity identity, SourceRevision revision, CancellationToken token = default)
        { Identity = identity; Revision = revision; return Task.CompletedTask; }
    }
    private sealed class Session(params SceneLoadedSection[] sections) : ISceneCatalogSession
    {
        public ArchiveSnapshot Snapshot => null!;
        public SourceRevision Revision => sections[0].Result.Revision;
        public SceneDiscoveryProgress Progress => new(false, sections.Length, sections.Length, sections.Length, false);
        public event EventHandler? Updated { add { } remove { } }
        public Task DiscoverMoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default) =>
            Task.FromResult(new SceneCatalogPage(sections.Select(item => item.Descriptor).ToArray(), sections.Length, offset));
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) =>
            Task.FromResult(sections.Single(item => item.Result.Identity.Ordinal == identity.Ordinal));
        public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Task.CompletedTask;
        public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Task.CompletedTask;
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
