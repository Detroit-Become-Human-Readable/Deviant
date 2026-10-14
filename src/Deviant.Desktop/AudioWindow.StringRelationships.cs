using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private string? StringRelatedLabel(ResourceIdentity identity)
    {
        var current=session;if(current is null)return null;
        var matches=current.Catalog.Media.Where(media=>media.Identity?.IndexContentId==identity.IndexContentId&&media.Identity.Ordinal==identity.Ordinal&&media.BankKey.Length==0).Take(2).ToArray();
        if(matches.Length!=1||matches[0].Identity is not{}logical)return null;
        var rows=current.ResolveSelection([logical]);return rows.Count==1?rows[0].Label:null;
    }
    private async Task NavigateStringRelatedAsync(ResourceIdentity identity,CancellationToken token)
    {
        var current=session??throw new InvalidOperationException("Open a workspace first.");
        if(identity.IndexContentId!=current.Snapshot.IndexContentId||identity.Ordinal<0||identity.Ordinal>=current.Snapshot.Records.Count)
            throw new InvalidOperationException("The linked resource belongs to another archive.");
        var navigation=BeginNavigation();using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,navigation.Token);
        await current.EnrichmentTask.WaitAsync(linked.Token);
        if(!ReferenceEquals(current,session)||navigation.Generation!=navigationGeneration)return;
        var resource=current.Snapshot.Records[(int)identity.Ordinal];
        if(resource.Type==1033)
        {
            var matches=current.Catalog.Media.Where(media=>media.Identity?.IndexContentId==identity.IndexContentId&&media.Identity.Ordinal==identity.Ordinal&&media.BankKey.Length==0).Take(2).ToArray();
            if(matches.Length==1&&matches[0].Identity is{}logical)
            {
                var text = stringsView.SelectedRecord;
                await NavigateDiscoveryAsync("audio",logical,navigation.Generation,linked.Token);
                if (text is not null && ReferenceEquals(current, session) && CurrentRow?.Identity == logical)
                {
                    dialogueReturnContext = new(logical, text.Identity, text.ResourceIdentity,
                        new(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp));
                    UpdateAudioDialogueSelection(CurrentRow);
                }
                return;
            }
        }
        var controller=new BrowserController(new WpfUiDispatcher(Dispatcher),cache.Location);
        var window=new MainWindow(controller){Owner=this};AttachInspectors(window);advancedWindows.Add((window,controller));
        window.Closed+=(_,_)=>advancedWindows.RemoveAll(item=>ReferenceEquals(item.Window,window));window.Show();
        await controller.OpenAsync(current.Snapshot.IndexPath,linked.Token);
        await controller.QueryAsync(new(Type:resource.Type,Id:resource.Id,Package:resource.Package),linked.Token);controller.Focus(resource);
    }
}
