using System.IO;
using System.Windows;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AssetModelView
{
    private IDisposable? animationSources;
    private readonly SemaphoreSlim animationTransition = new(1, 1);
    private CancellationTokenSource? animationLifetime;
    private long animationGeneration;
    private ulong animationFrameSequence;

    /// <summary>Transfers source retention to the preview and starts a qualified clip paused and in place.</summary>
    public async Task SetAnimationAsync(AssetDescriptor clip, double duration,
        Func<double, bool, CancellationToken, Task<ModelAnimationFrame>> frames,
        Func<CancellationToken, Task> validateSources, IDisposable retention, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(clip); ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(validateSources); ArgumentNullException.ThrowIfNull(retention);
        IDisposable? owned = retention;
        bool entered = false;
        long request = ++animationGeneration;
        animationLifetime?.Cancel();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool lifetimeTransferred = false;
        var selected = snapshot;
        try
        {
            await animationTransition.WaitAsync(lifetime.Token); entered = true;
            if (request != animationGeneration) throw new OperationCanceledException(token);
            await ClearAnimationCoreAsync(); token.ThrowIfCancellationRequested();
            if (selected is null || !ReferenceEquals(snapshot, selected) || request != animationGeneration ||
                selected.Revision != clip.Revision || clip.Identity.IndexContentId != selected.Revision.IndexContentId)
                throw new InvalidDataException("AnimationPreviewSourceMismatch");
            animationLifetime = lifetime; lifetimeTransferred = true;
            await validateSources(lifetime.Token);
            if (request != animationGeneration || !ReferenceEquals(snapshot, selected)) throw new OperationCanceledException(token);
            animationSources = owned; owned = null;
            ModelAnimationName.Text = clip.Label; ModelAnimationTab.Visibility = Visibility.Visible;
            await ModelAnimationPlayback.SetClipAsync(duration, async (seconds, inPlace, cancellation) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
                cancellation = linked.Token;
                cancellation.ThrowIfCancellationRequested();
                if (request != animationGeneration || !ReferenceEquals(snapshot, selected)) throw new OperationCanceledException(cancellation);
                await validateSources(cancellation);
                using var frame = await frames(seconds, inPlace, cancellation);
                if (frame.Revision != selected.Revision) throw new InvalidDataException("AnimationPreviewFrameMismatch");
                await ModelViewport.UpdatePoseAsync(new(frame.Revision, ++animationFrameSequence, frame.Primitives), cancellation);
                await validateSources(cancellation);
            });
            token.ThrowIfCancellationRequested();
            if (request != animationGeneration || !ReferenceEquals(snapshot, selected)) throw new OperationCanceledException(token);
        }
        catch
        {
            if (request == animationGeneration) { ++animationGeneration; await ClearAnimationCoreAsync(); }
            throw;
        }
        finally { owned?.Dispose(); if (!lifetimeTransferred) lifetime.Dispose(); if (entered) animationTransition.Release(); }
    }

    public async Task ClearAnimationAsync()
    {
        Dispatcher.VerifyAccess(); ++animationGeneration; animationLifetime?.Cancel();
        await animationTransition.WaitAsync();
        try { await ClearAnimationCoreAsync(); }
        finally { animationTransition.Release(); }
    }

    private async Task ClearAnimationCoreAsync()
    {
        var lifetime = animationLifetime; animationLifetime = null; lifetime?.Cancel();
        try { await ModelAnimationPlayback.ClearAsync(); }
        finally
        {
            animationSources?.Dispose(); animationSources = null;
            lifetime?.Dispose();
            ModelAnimationName.Text = ""; ModelAnimationTab.Visibility = Visibility.Collapsed;
        }
    }

    private void ModelAnimationFailed(object? sender, Exception error)
    { ModelStatus.Text = "Unable to play animation"; ModelDetails.Text = error.ToString(); }
}
