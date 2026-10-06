using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Desktop;

public sealed record LocalizationRelatedResource(string Label,ResourceIdentity Identity,IReadOnlyList<AssetRelationship> Evidence);

public partial class StringsView
{
    private LocalizationRelationshipCoordinator? relationshipCoordinator;
    private readonly HashSet<Task> relationshipTasks=[];
    public Func<ResourceIdentity,CancellationToken,Task>? RelatedNavigator { get; set; }
    public Func<ResourceIdentity,string?>? RelatedLabelResolver { get; set; }
    public IReadOnlyList<LocalizationRelatedResource> CurrentRelatedResources=>RelatedResources.ItemsSource as LocalizationRelatedResource[]??[];

    private void UpdateRelatedSelection(LocalizationRecord? row,LocalizationSession? current,long generation,CancellationToken token)
    {
        RelatedResources.ItemsSource=null;RelatedEvidence.Text="";OpenRelated.IsEnabled=false;
        RelatedStatus.Text=row is null?"Select a string":"Loading relationships…";
        if(row is null||current is null)return;
        relationshipCoordinator??=new();
        var coordinator=relationshipCoordinator;
        var task=LoadRelatedAsync(coordinator,current,row,generation,token);
        relationshipTasks.Add(task);_=DrainCompletedRelatedAsync(task);
    }
    private async Task DrainCompletedRelatedAsync(Task task)
    {try{await task;}catch{}finally{relationshipTasks.Remove(task);}}
    private async Task DrainRelatedAsync(){try{await Task.WhenAll(relationshipTasks.ToArray());}catch{}}
    private async Task LoadRelatedAsync(LocalizationRelationshipCoordinator coordinator,LocalizationSession current,LocalizationRecord row,long generation,CancellationToken token)
    {
        try
        {
            var result=await Task.Run(()=>coordinator.ResolveSelectedAsync(current,row.Identity,token),token);
            token.ThrowIfCancellationRequested();if(generation!=detailGeneration||!ReferenceEquals(current,session))return;
            var rows=new List<LocalizationRelatedResource>();
            foreach(var relationship in result.Relationships)
            {
                if(relationship.EvidencePath.Count==0||relationship.EvidencePath.Any(edge=>!edge.Evidence.Verified||edge.NavigationTarget is null))continue;
                void Add(string label,ResourceIdentity? target){if(target is not null&&!rows.Any(item=>item.Identity==target))rows.Add(new(RelatedLabelResolver?.Invoke(target)??label,target,relationship.EvidencePath));}
                Add("Audio",relationship.AudioResourceIdentity);Add("Dialogue event",relationship.DialogueEventIdentity);Add("Dialogue header",relationship.DialogueHeaderIdentity);
            }
            RelatedResources.ItemsSource=rows.ToArray();
            RelatedStatus.Text=rows.Count>0?$"{rows.Count} related assets":result.Diagnostics.Any(item=>item.Code=="LocalizationPending")?"Indexing relationships…":result.Diagnostics.Any(item=>item.Status==LocalizationReferenceLinkStatus.Ambiguous)?"Ambiguous reference":result.Diagnostics.Any(item=>item.Code.Contains("Missing",StringComparison.Ordinal))?"Missing reference":"No recorded links";
            RelatedEvidence.Text=string.Join("\n",result.Diagnostics.Select(item=>$"{item.Code}: {item.Message}"))+
                string.Join("\n",rows.SelectMany(item=>item.Evidence).Distinct().Select(edge=>$"\n{edge.Kind} · {edge.Reference.Namespace} · {edge.Reference.Id}\n{edge.Evidence.Source}\n{edge.Evidence.PhysicalExtent}"));
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(generation==detailGeneration&&ReferenceEquals(current,session))RelatedStatus.Text=error is ArchiveException{Code:"SourceChanged"}?"Source changed. Reopen the archive.":error.Message;}
    }
    private void RelatedSelectionChanged(object sender,SelectionChangedEventArgs e)=>OpenRelated.IsEnabled=RelatedResources.SelectedItem is LocalizationRelatedResource&&RelatedNavigator is not null;
    private async void OpenRelated_Click(object sender,RoutedEventArgs e)=>await OpenRelatedAsync();
    private async void Related_DoubleClick(object sender,MouseButtonEventArgs e)=>await OpenRelatedAsync();
    private async Task OpenRelatedAsync()
    {
        if(RelatedResources.SelectedItem is not LocalizationRelatedResource row||RelatedNavigator is not{}navigate)return;
        try{await navigate(row.Identity,detailCancellation?.Token??CancellationToken.None);}
        catch(OperationCanceledException){}catch(Exception error){RelatedStatus.Text=error.Message;}
    }
}
