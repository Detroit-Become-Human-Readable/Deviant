using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private Task NavigateArchiveReferenceAsync(ResourceIdentity target,CancellationToken token)
    {
        var revision = referencesView.LoadedRevision ??
            throw new ArchiveException("SourceChanged","The reference belongs to another archive revision. Reopen the archive.");
        return NavigateAssetAsync(target, revision, token);
    }

    private async Task NavigateAssetAsync(ResourceIdentity target, SourceRevision expectedRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current=session??throw new InvalidOperationException("Open an archive first.");
        var source=current.Snapshot;
        if(expectedRevision!=new SourceRevision(source.IndexContentId,source.PhysicalSourceStamp)||
            target.IndexContentId!=source.IndexContentId||target.MemberPath is not null||target.Ordinal<0||target.Ordinal>=source.Records.Count)
            throw new ArchiveException("SourceChanged","The reference belongs to another archive revision. Reopen the archive.");
        var row=source.Records[(int)target.Ordinal];
        var navigation=BeginNavigation();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,navigation.Token);
        if(row.Type==1033)
        {
            if(!current.IsEnrichmentComplete)await current.EnrichmentTask.WaitAsync(linked.Token);
            if(navigation.Generation!=navigationGeneration||!ReferenceEquals(current,session))return;
            var media=target with{MemberPath="wem"};
            if(current.ResolveSelection([media]).Count==1)
            {await NavigateDiscoveryAsync("audio",media,navigation.Generation,linked.Token);return;}
        }
        if(current.ResolveSelection([target]).Count>0)
        {await NavigateDiscoveryAsync("audio",target,navigation.Generation,linked.Token);return;}
        if(row.Type is 2229 or 2230)
        {await NavigateDiscoveryAsync("textures",target,navigation.Generation,linked.Token);return;}
        if(row.Type==Deviant.Formats.Animation.AnimationDataReader.ResourceType)
        {await NavigateDiscoveryAsync("animation",target,navigation.Generation,linked.Token);return;}
        if(row.Type==3)
        {await InspectReferencesAsync(target,linked.Token);return;}
        if(row.Type==1016)
        {
            await ShowStringsAsync(linked.Token);linked.Token.ThrowIfCancellationRequested();
            if(navigation.Generation!=navigationGeneration||!ReferenceEquals(current,session))return;
            if(stringsView.CurrentSession is{}text&&!text.Sources.Any(item=>item.Identity==target)&&!text.IsEnrichmentComplete)
                await text.EnrichmentTask.WaitAsync(linked.Token);
            if(navigation.Generation!=navigationGeneration||!ReferenceEquals(current,session))return;
            await stringsView.ApplyFilterAsync(new Dictionary<string,string>{{"source",target.Key}},linked.Token);return;
        }
        await OpenReferencedRecordAsync(current,row,linked.Token);
    }

    private async Task OpenReferencedRecordAsync(AudioExplorerSession owner,ResourceRecord row,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!ReferenceEquals(owner,session))return;
        var controller=new BrowserController(new WpfUiDispatcher(Dispatcher),cache.Location);
        var window=new MainWindow(controller){Owner=this};advancedWindows.Add((window,controller));
        window.Closed+=(_,_)=>advancedWindows.RemoveAll(item=>ReferenceEquals(item.Window,window));
        AttachInspectors(window);window.Show();
        try
        {
            await controller.OpenAsync(owner.Snapshot.IndexPath,token);token.ThrowIfCancellationRequested();
            if(!ReferenceEquals(owner,session)){window.Close();return;}
            if(controller.State.Snapshot is not{}opened||opened.IndexContentId!=owner.Snapshot.IndexContentId||opened.PhysicalSourceStamp!=owner.Snapshot.PhysicalSourceStamp)
                throw new ArchiveException("SourceChanged","The archive changed while opening the referenced resource.");
            await controller.QueryAsync(new(Type:row.Type,Id:row.Id,Package:row.Package),token);
            token.ThrowIfCancellationRequested();controller.Focus(row);
        }
        catch{window.Close();throw;}
    }
}
