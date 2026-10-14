using System.Windows;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private CancellationTokenSource? relatedCancellation;
    private long relatedGeneration;
    private ResourceIdentity? relatedIdentity;
    private AudioRelationships? audioRelationships;
    private AudioExplorerSession? relationshipSession;

    private async Task RefreshRelatedAsync()
    {
        var row=CurrentRow;var current=session;
        if(row is null||current is null){relatedCancellation?.Cancel();++relatedGeneration;relatedIdentity=null;RelatedList.ItemsSource=null;RelatedStatus.Text="Select a clip";if(!ReferenceEquals(relationshipSession,current)){audioRelationships?.ClearBankGraphCache();audioRelationships=null;relationshipSession=null;}return;}
        if(!current.IsEnrichmentComplete)
        {
            relatedCancellation?.Cancel(); ++relatedGeneration; relatedIdentity=null; RelatedList.ItemsSource=null;
            RelatedStatus.Text=current.CurrentProgress.Phase switch
            {
                "Cancelled" => "Relationships not indexed.",
                "Enrichment failed" => "Relationships unavailable.",
                _ => "Indexing relationships…"
            };
            return;
        }
        if(relatedIdentity==row.Identity)return;
        relatedIdentity=row.Identity;relatedCancellation?.Cancel();relatedCancellation?.Dispose();relatedCancellation=new();var token=relatedCancellation.Token;var generation=++relatedGeneration;
        RelatedList.ItemsSource=null;RelatedStatus.Text="Loading relationships…";
        try
        {
            if(!ReferenceEquals(relationshipSession,current)){audioRelationships?.ClearBankGraphCache();audioRelationships=new(current.Snapshot,current.Catalog);relationshipSession=current;}
            var resolver=audioRelationships!;
            var links=await Task.Run(()=>resolver.ResolveForMediaAsync(row.Identity,token),token);
            token.ThrowIfCancellationRequested();if(generation!=relatedGeneration||!ReferenceEquals(current,session))return;
            if(generation!=relatedGeneration||!ReferenceEquals(current,session))return;
            var rows=links.Select(link=>new RelatedRow(RelatedLabel(current,link),link.NavigationTarget is null?link.Resolution.Status.ToString():"",link)).ToArray();
            RelatedList.ItemsSource=rows;RelatedStatus.Text=rows.Length==0?"No recorded relationships":$"{rows.Length} relationships";
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(generation==relatedGeneration)RelatedStatus.Text=error.Message;}
    }
    private static string RelatedLabel(AudioExplorerSession current,AssetRelationship link)
    {
        if (AudioRelationshipPresentation.EventLabel(link, current.Catalog.Events) is {} eventLabel) return eventLabel;
        var target=link.NavigationTarget;
        if(target is not null)
        {
            var media = current.ResolveSelection([target]);
            if (media.Count == 1) return media[0].Label;
            if (media.Count > 1) return string.Join(" / ", media.Select(row => row.Label).Distinct()) + " · Ambiguous";
        }
        if(target is not null&&current.Catalog.Banks.FirstOrDefault(bank=>bank.Identity==target) is{}bank)
            return current.Banks.FirstOrDefault(choice=>choice.Key==bank.Key)?.Label??"Bank";
        return link.Kind switch{AssetRelationshipKind.Container=>"Container",AssetRelationshipKind.Member=>"Bank",AssetRelationshipKind.Media=>"Audio",AssetRelationshipKind.Event=>"Event",AssetRelationshipKind.Dependency=>"Playback reference",_=>link.Kind.ToString()};
    }
    private async void Related_DoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(RelatedList.SelectedItem is not RelatedRow row||row.Relationship.NavigationTarget is not{}target||session is not{}current)return;
        try
        {
            var navigation=BeginNavigation();
            if(current.ResolveSelection([target]).Count>0){await NavigateDiscoveryAsync("audio",target,navigation.Generation,navigation.Token);return;}
            var controller=new BrowserController(new WpfUiDispatcher(Dispatcher),cache.Location);
            var window=new MainWindow(controller){Owner=this};advancedWindows.Add((window,controller));window.Closed+=(_,_)=>advancedWindows.RemoveAll(item=>ReferenceEquals(item.Window,window));window.Show();
            AttachInspectors(window);
            await controller.OpenAsync(current.Snapshot.IndexPath,navigation.Token);
            if(target.Ordinal<0||target.Ordinal>=current.Snapshot.Records.Count)return;
            var record=current.Snapshot.Records[(int)target.Ordinal];await controller.QueryAsync(new(Type:record.Type,Id:record.Id,Package:record.Package),navigation.Token);controller.Focus(record);
        }
        catch(OperationCanceledException){}catch(Exception error){NavigationError(error);}
    }
    private sealed record RelatedRow(string Label,string Status,AssetRelationship Relationship);
}
