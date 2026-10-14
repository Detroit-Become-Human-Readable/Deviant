using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public sealed record SceneRegionChoice(SceneRenderRegion Region)
{
    public string Label => SceneNameDisplay.Project(Region.Label, "Region");
}

public partial class SceneView
{
    private CancellationTokenSource? scenePipelineCancellation;
    private Task scenePipelineTask = Task.CompletedTask;
    private long scenePipelineGeneration;
    private bool scenePipelineActive, regionsLoadingActive, updatingRegions;
    private int regionsCompleted, regionsTotal;
    private SceneLoadedSection? describedSection;
    private SceneRegionDiscovery? regionDiscovery;
    private SceneLoadedSection? largeDataApprovedSection;
    private SourceRevision? largeDataApprovedRevision;

    public Func<SceneLoadedSection, IProgress<RenderLoadProgress>?, CancellationToken, Task<SceneRegionDiscovery>>? RegionProvider { get; set; }
    public Func<SceneLoadedSection, bool, IProgress<RenderLoadProgress>?, CancellationToken, Task<SceneRegionDiscovery>>? RegionProviderWithLargeData { get; set; }
    public IReadOnlyList<SceneRenderRegion> CurrentRegions => regionDiscovery?.Regions ?? [];

    private bool HasLargeDataApproval(SceneLoadedSection loaded) =>
        ReferenceEquals(largeDataApprovedSection, loaded) && largeDataApprovedRevision == loaded.Result.Revision;

    private void ClearLargeDataApproval()
    {
        largeDataApprovedSection = null;
        largeDataApprovedRevision = null;
        lastSuccessfulSceneRenderRequest = null;
        lastSuccessfulRenderBudget = null;
    }

    private void ApproveLargeData(SceneLoadedSection loaded)
    {
        largeDataApprovedSection = loaded;
        largeDataApprovedRevision = loaded.Result.Revision;
    }

    public async Task LoadSceneAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        scenePipelineCancellation?.Cancel();
        var previous = scenePipelineTask;
        var generation = ++scenePipelineGeneration;
        try { await previous; } catch (OperationCanceledException) { } catch { }
        if (generation != scenePipelineGeneration) return;
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        scenePipelineCancellation = own;
        var task = LoadSceneCoreAsync(identity, own, generation);
        scenePipelineTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(scenePipelineTask, task)) scenePipelineTask = Task.CompletedTask;
            if (ReferenceEquals(scenePipelineCancellation, own)) scenePipelineCancellation = null;
            own.Dispose();
        }
    }

    private async Task LoadSceneCoreAsync(ResourceIdentity identity, CancellationTokenSource cancellation, long generation)
    {
        activityTerminalStage = null;
        scenePipelineActive = true;
        LoadRegionButton.IsEnabled = LoadAllButton.IsEnabled = false;
        UpdateLoadingProgress();
        try
        {
            var loaded = await TrackLoadAsync(identity, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != scenePipelineGeneration || !ReferenceEquals(section, loaded)) throw new OperationCanceledException(cancellation.Token);
            var regions = await DescribeRegionsAsync(loaded, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != scenePipelineGeneration || !ReferenceEquals(section, loaded)) throw new OperationCanceledException(cancellation.Token);
            if (regions.Regions.Count == 0) { RenderStatus.Text = "No geometry found"; return; }
            var selected = regions.Regions.OrderByDescending(region => region.VisibleOccurrenceCount ?? region.OccurrenceCount).ThenBy(region => region.InstanceIdentity.Ordinal).First();
            ChooseRegion(selected);
            await LoadRegionAsync(cancellation.Token, maximumPreviewDimension: 256);
        }
        catch (OperationCanceledException) { SetActivityTerminal("Cancelled"); throw; }
        catch { SetActivityTerminal("Failed"); throw; }
        finally
        {
            if (generation == scenePipelineGeneration)
            {
                scenePipelineActive = false;
                UpdateLoadingProgress();
                UpdateRegionButtons();
            }
        }
    }

    private async Task<SceneRegionDiscovery> DescribeRegionsAsync(SceneLoadedSection loaded, CancellationToken token)
    {
        if (ReferenceEquals(describedSection, loaded) && regionDiscovery is not null) return regionDiscovery;
        var provider = RegionProvider;
        var providerWithLargeData = RegionProviderWithLargeData;
        if (provider is null && providerWithLargeData is null)
            throw new InvalidOperationException("Scene regions are unavailable.");
        activityTerminalStage = null;
        regionsLoadingActive = true; regionsCompleted = regionsTotal = 0;
        UpdateLoadingProgress();
        try
        {
            using var progress = new CoalescedDispatcherProgress<RenderLoadProgress>(Dispatcher, value =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(section, loaded)) return;
                regionsCompleted = value.Completed; regionsTotal = value.Total; UpdateLoadingProgress();
            });
            SceneRegionDiscovery result;
            var allowLargeData = HasLargeDataApproval(loaded);
            try
            {
                if (allowLargeData)
                {
                    result = providerWithLargeData is not null
                        ? await providerWithLargeData(loaded, true, progress, token)
                        : await provider!(loaded, progress, token);
                }
                else if (providerWithLargeData is not null)
                    result = await providerWithLargeData(loaded, false, progress, token);
                else result = await provider!(loaded, progress, token);
            }
            catch (ArchiveException error) when (IsLargeWorkloadLimit(error.Code) && !allowLargeData)
            {
                var message = "This scene contains a lot of data and may take extra time to load. Continue?";
                renderLoadingStage = "Confirming large load";
                UpdateLoadingProgress();
                if (!await ConfirmLargeDataAsync(message, token))
                {
                    RenderStatus.Text = "Load cancelled";
                    throw new OperationCanceledException(token);
                }
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(section, loaded) || loaded.Result.Revision != session?.Revision)
                    throw new OperationCanceledException(token);
                ApproveLargeData(loaded);
                var largeProvider = providerWithLargeData ??
                    throw new InvalidOperationException("Large scene region discovery is unavailable.");
                result = await largeProvider(loaded, true, progress, token);
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(section, loaded)) throw new OperationCanceledException(token);
            describedSection = loaded; regionDiscovery = result;
            updatingRegions = true;
            try { RenderRegionChoice.ItemsSource = result.Regions.Select(region => new SceneRegionChoice(region)).ToArray(); }
            finally { updatingRegions = false; }
            Advanced.Text = FormatIssues(result.Issues);
            return result;
        }
        finally { regionsLoadingActive = false; UpdateLoadingProgress(); }
    }

    private void ChooseRegion(SceneRenderRegion region)
    {
        updatingRegions = true;
        try { RenderRegionChoice.SelectedItem = RenderRegionChoice.Items.OfType<SceneRegionChoice>().FirstOrDefault(choice => choice.Region.InstanceIdentity == region.InstanceIdentity); }
        finally { updatingRegions = false; }
        SelectBranch(region.InstanceIdentity);
        SetRegion(region.PreviewBounds);
    }

    public async Task LoadAllInstancesAsync(CancellationToken token = default)
    {
        var loaded = section ?? throw new InvalidOperationException("Open a scene first.");
        scenePipelineCancellation?.Cancel();
        var previous = scenePipelineTask;
        var generation = ++scenePipelineGeneration;
        try { await previous; } catch (OperationCanceledException) { } catch { }
        if (generation != scenePipelineGeneration || !ReferenceEquals(section, loaded)) return;
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        scenePipelineCancellation = own;
        var task = LoadAllCoreAsync(loaded, own, generation);
        scenePipelineTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(scenePipelineTask, task)) scenePipelineTask = Task.CompletedTask;
            if (ReferenceEquals(scenePipelineCancellation, own)) scenePipelineCancellation = null;
            own.Dispose();
        }
    }

    private async Task LoadAllCoreAsync(SceneLoadedSection loaded, CancellationTokenSource cancellation, long generation)
    {
        activityTerminalStage = null;
        scenePipelineActive = true; UpdateRegionButtons(); UpdateLoadingProgress();
        try
        {
            var regions = await DescribeRegionsAsync(loaded, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != scenePipelineGeneration || !ReferenceEquals(section, loaded)) throw new OperationCanceledException(cancellation.Token);
            if (regions.Regions.Count == 0) { RenderStatus.Text = "No geometry found"; return; }
            if (BranchChoice.SelectedItem is null) ChooseRegion(regions.Regions.OrderByDescending(region => region.VisibleOccurrenceCount ?? region.OccurrenceCount).First());
            await LoadRegionAsync(cancellation.Token, SceneRenderScope.Scene);
        }
        catch (OperationCanceledException) { SetActivityTerminal("Cancelled"); throw; }
        catch { SetActivityTerminal("Failed"); throw; }
        finally
        {
            if (generation == scenePipelineGeneration) { scenePipelineActive = false; UpdateLoadingProgress(); UpdateRegionButtons(); }
        }
    }

    private async void RenderRegionChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingRegions || RenderRegionChoice.SelectedItem is not SceneRegionChoice choice) return;
        scenePipelineCancellation?.Cancel();
        try
        {
            var previous = scenePipelineTask;
            try { await previous; } catch (OperationCanceledException) { } catch { }
            if (RenderRegionChoice.SelectedItem is not SceneRegionChoice current || current != choice || section is null) return;
            ChooseRegion(choice.Region);
            await LoadRegionAsync(lifetime.Token, maximumPreviewDimension: 256);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }

    private async void LoadAll_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadAllInstancesAsync(lifetime.Token); }
        catch (OperationCanceledException) { RenderStatus.Text = "Load cancelled"; }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }

    private void ClearRegionChoices()
    {
        describedSection = null; regionDiscovery = null;
        updatingRegions = true;
        try { RenderRegionChoice.ItemsSource = null; }
        finally { updatingRegions = false; }
        LoadAllButton.IsEnabled = false;
    }
    private void UpdateRegionButtons()
    {
        LoadRegionButton.IsEnabled = !scenePipelineActive && !renderLoadingActive && section is not null && BranchChoice.SelectedItem is not null && RenderProvider is not null;
        LoadAllButton.IsEnabled = !scenePipelineActive && !renderLoadingActive && section is not null &&
            (RegionProvider is not null || RegionProviderWithLargeData is not null) && RenderProvider is not null;
    }
}
