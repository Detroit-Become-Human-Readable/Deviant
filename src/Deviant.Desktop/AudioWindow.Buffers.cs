using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private CancellationTokenSource? bufferReadCancellation;
    private Task? bufferReadTask;
    private readonly HashSet<Task> bufferReads=[];
    private int bufferDrainDepth;
    public BufferInspectionView BufferBrowser=>bufferView;
    public Task InspectTextureBufferAsync(ResourceIdentity identity,CancellationToken token=default)
    {
        EnsureBufferRequestAllowed();
        var navigation=BeginNavigation();
        return InspectBufferAsync(identity,navigation.Generation,token.CanBeCanceled?token:navigation.Token);
    }
    private async Task InspectBufferAsync(ResourceIdentity identity,long navigation,CancellationToken token)
    {
        EnsureBufferRequestAllowed();
        var owner=session??throw new InvalidOperationException("Open an archive first.");
        bufferReadCancellation?.Cancel();
        using var own=CancellationTokenSource.CreateLinkedTokenSource(token,navigationCancellation.Token);
        bufferReadCancellation=own;
        var read=Task.Run(()=>BufferInspections.InspectTextureAsync(owner.Snapshot,identity,TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1,own.Token),own.Token);
        bufferReadTask=read;
        bufferReads.Add(read);
        try
        {
            var result=await read;own.Token.ThrowIfCancellationRequested();
            if(closingInProgress||bufferDrainDepth!=0||navigation!=navigationGeneration||!ReferenceEquals(owner,session))return;
            ShowBrowse("buffer");await bufferView.LoadAsync(owner.Snapshot,result,"Texture buffer",own.Token);
        }
        finally
        {
            if(ReferenceEquals(bufferReadTask,read))bufferReadTask=null;
            if(ReferenceEquals(bufferReadCancellation,own))bufferReadCancellation=null;
            bufferReads.Remove(read);
        }
    }
    private async Task ClearBufferAsync()
    {
        ++bufferDrainDepth;
        try
        {
            bufferReadCancellation?.Cancel();var pending=bufferReads.ToArray();
            try{await Task.WhenAll(pending);}catch{}
            foreach(var task in pending)bufferReads.Remove(task);
            await bufferView.ClearAsync();
        }
        finally{--bufferDrainDepth;}
    }
    private void EnsureBufferRequestAllowed()
    {
        if(closingInProgress)throw new InvalidOperationException("The window is closing.");
        if(bufferDrainDepth!=0)throw new InvalidOperationException("The workspace is changing.");
    }
}
