using System.Windows;
using System.Windows.Threading;
using Deviant.Browser;

namespace Deviant.Desktop;

public sealed record SceneActivityProgress(bool IsActive, string Stage, int Completed, int Total);

public partial class SceneView
{
    private CancellationTokenSource? automaticDiscoveryCancellation;
    private Task automaticDiscoveryTask = Task.CompletedTask;
    private long automaticDiscoveryGeneration;
    private bool automaticDiscoveryActive, batchDiscoveryActive, sectionLoadingActive, renderLoadingActive;
    private string renderLoadingStage = "Reading scene";
    private int renderLoadingCompleted, renderLoadingTotal;
    private bool renderLoadingTotalKnown;
    private string? activityTerminalStage;

    public event EventHandler<SceneActivityProgress>? ActivityProgressChanged;
    public SceneActivityProgress ActivityProgress { get; private set; } = new(false, "Ready", 0, 0);

    public bool IsDiscoveringAll => automaticDiscoveryActive;
    public bool IsLoading => automaticDiscoveryActive || batchDiscoveryActive || sectionLoadingActive || renderLoadingActive || scenePipelineActive || regionsLoadingActive;

    public async Task DiscoverAllAsync(CancellationToken token = default)
    {
        var current = session;
        if (current is null) return;
        if (!automaticDiscoveryTask.IsCompleted)
        {
            await automaticDiscoveryTask.WaitAsync(token);
            return;
        }
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        automaticDiscoveryCancellation = own;
        var generation = ++automaticDiscoveryGeneration;
        var task = DiscoverAllCoreAsync(current, own, generation);
        automaticDiscoveryTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(automaticDiscoveryTask, task)) automaticDiscoveryTask = Task.CompletedTask;
            if (ReferenceEquals(automaticDiscoveryCancellation, own)) automaticDiscoveryCancellation = null;
            own.Dispose();
        }
    }

    private async Task DiscoverAllCoreAsync(ISceneCatalogSession current, CancellationTokenSource cancellation, long generation)
    {
        activityTerminalStage = null;
        automaticDiscoveryActive = true;
        DiscoverMore.IsEnabled = false;
        UpdateLoadingProgress();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, cancellation.Token);
            var first = current.Progress.Candidates == 0;
            while (first || current.Progress.HasMore)
            {
                first = false;
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != automaticDiscoveryGeneration || !ReferenceEquals(current, session)) return;
                var before = current.Progress;
                await DiscoverMoreAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != automaticDiscoveryGeneration || !ReferenceEquals(current, session)) return;
                var after = current.Progress;
                if (after.Visited == before.Visited && after.Candidates == before.Candidates &&
                    after.Message is "Discovery failed" or "Cancelled") break;
                UpdateLoadingProgress();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == automaticDiscoveryGeneration) { Status.Text = "Discovery cancelled"; SetActivityTerminal("Cancelled"); }
        }
        catch { if (generation == automaticDiscoveryGeneration) SetActivityTerminal("Failed"); throw; }
        finally
        {
            if (generation == automaticDiscoveryGeneration)
            {
                automaticDiscoveryActive = false;
                DiscoverMore.IsEnabled = current.Progress.HasMore;
                UpdateLoadingProgress();
            }
        }
    }

    private void UpdateLoadingProgress()
    {
        var discovery = session?.Progress;
        SceneCount.Text = discovery is null ? "" : $"{discovery.Available:N0} scenes";
        string stage;
        int completed, total;
        bool known;
        if (renderLoadingActive)
        {
            stage = renderLoadingStage; completed = renderLoadingCompleted; total = renderLoadingTotal;
            known = renderLoadingTotalKnown;
        }
        else if (regionsLoadingActive)
        {
            stage = "Finding regions"; completed = regionsCompleted; total = regionsTotal; known = total > 0;
        }
        else if (sectionLoadingActive || scenePipelineActive)
        {
            stage = "Reading scene"; completed = total = 0; known = false;
        }
        else
        {
            stage = "Discovering scenes"; completed = discovery?.Visited ?? 0; total = discovery?.Candidates ?? 0; known = total > 0;
        }
        if (!IsLoading) stage = activityTerminalStage ?? "Ready";
        var update = new SceneActivityProgress(IsLoading, stage, completed, known ? total : 0);
        ActivityProgress = update;
        ActivityProgressChanged?.Invoke(this, update);
    }

    private void SetActivityTerminal(string stage)
    {
        activityTerminalStage = stage;
        if (!IsLoading) UpdateLoadingProgress();
    }

    private static bool IsLargeWorkloadLimit(string code) => code is
        "AssetMemoryLimit" or "SceneGraphLimit" or "SceneInstanceLimit" or
        "ModelSelectionLimit" or "SceneVerificationLimit" or
        "NativeContainerLimit" or "NativePayloadLimit";

    private void UpdateRenderLoadingProgress(RenderLoadProgress progress)
    {
        if (!renderLoadingActive) return;
        renderLoadingStage = progress.Stage switch
        {
            "Metadata" => "Reading scene",
            "Geometry" => "Loading geometry",
            "Materials" => "Loading materials",
            "Textures" => "Loading textures",
            "Loading" => "Uploading",
            "Ready" => "Finishing",
            _ => progress.Stage
        };
        renderLoadingCompleted = progress.Completed;
        renderLoadingTotal = progress.Total;
        renderLoadingTotalKnown = progress.Total > 0 && progress.Stage != "Metadata";
        UpdateLoadingProgress();
    }
}
