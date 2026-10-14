using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly SemaphoreSlim characterContainerGate = new(1, 1);
    private NativeContainerLease? characterContainer;
    private NativeSkeletonBindingSession? characterRigSession;
    private AudioExplorerSession? characterContainerOwner;
    private CharacterModelBindingResult? characterContainerBinding;

    private async Task<T> WithCharacterContainerAsync<T>(AudioExplorerSession owner,
        CharacterModelBindingResult binding, AssetMemoryBudget budget,
        Func<NativeContainerLease, CancellationToken, Task<T>> action, CancellationToken token)
    {
        await characterContainerGate.WaitAsync(token);
        try
        {
            Verify();
            if (!ReferenceEquals(owner, characterContainerOwner) ||
                !ReferenceEquals(binding, characterContainerBinding))
            {
                characterRigSession?.Dispose(); characterRigSession = null;
                characterContainer?.Dispose(); characterContainer = null;
                characterContainerOwner = null; characterContainerBinding = null;
                var opened = await NativeContainerLease.OpenMetadataAsync(owner.Snapshot,
                    binding.ContainerIdentity, budget, token);
                try { Verify(); }
                catch { opened.Dispose(); throw; }
                characterContainer = opened;
                characterContainerOwner = owner; characterContainerBinding = binding;
            }
            characterContainer!.EnsureUnchanged();
            var result = await action(characterContainer, token);
            try { Verify(); characterContainer.EnsureUnchanged(); }
            catch { (result as IDisposable)?.Dispose(); throw; }
            return result;
        }
        catch
        {
            // A failed or cancelled preview must not reopen a second source
            // while the current Rig inspector still retains its projection.
            if (characterRigSession is null)
            {
                characterContainer?.Dispose(); characterContainer = null;
                characterContainerOwner = null; characterContainerBinding = null;
            }
            throw;
        }
        finally { characterContainerGate.Release(); }

        void Verify()
        {
            token.ThrowIfCancellationRequested();
            if (closingInProgress || workspaceTransitionDepth != 0 || !IsWorkspaceValidated ||
                !ReferenceEquals(owner, session) || currentView != "characters" ||
                !ReferenceEquals(binding, charactersView.ModelBinding) ||
                charactersView.SelectedDescriptor?.Identity != binding.SelectedIdentity ||
                charactersView.SelectedDescriptor?.Revision != binding.Revision ||
                !ReferenceEquals(budget, renderMemoryBudget))
                throw new OperationCanceledException(token);
        }
    }

    private async Task<NativeSkeletonBindingSession.NativeSkeletonBindingLease> ReadProductionCharacterRigAsync(
        AssetDescriptor selected, CharacterModelBindingResult binding, CancellationToken token)
    {
        var owner = session ?? throw new InvalidOperationException("Open a workspace first.");
        if (selected.Identity != binding.SelectedIdentity || selected.Revision != binding.Revision)
            throw new InvalidOperationException("The model selection changed.");
        var budget = renderMemoryBudget ?? throw new InvalidOperationException("The asset session is unavailable.");
        return await WithCharacterContainerAsync(owner, binding, budget, async (container, cancellation) =>
        {
            return await AcquireCharacterRigAsync(container, binding, cancellation);
        }, token);
    }

    // Called inside characterContainerGate, so inspection and rendering share
    // one parsed projection and source owner for the current selection.
    private async Task<NativeSkeletonBindingSession.NativeSkeletonBindingLease> AcquireCharacterRigAsync(
        NativeContainerLease container, CharacterModelBindingResult binding, CancellationToken token)
    {
        characterRigSession ??= await NativeSkeletonBindingSession.FromContainerAsync(
            container, binding, binding.Revision, token);
        token.ThrowIfCancellationRequested();
        var consumer = characterRigSession.AcquireLease();
        try { consumer.EnsureUnchanged(); return consumer; }
        catch { consumer.Dispose(); throw; }
    }

    private async Task ClearCharacterContainerAsync()
    {
        await characterContainerGate.WaitAsync();
        try
        {
            characterRigSession?.Dispose(); characterRigSession = null;
            characterContainer?.Dispose(); characterContainer = null;
            characterContainerOwner = null; characterContainerBinding = null;
        }
        finally { characterContainerGate.Release(); }
    }
}
