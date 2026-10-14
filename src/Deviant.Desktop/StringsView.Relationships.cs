using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Desktop;

public sealed record LocalizationRelatedResource(string Label,ResourceIdentity Identity,IReadOnlyList<AssetRelationship> Evidence);
public sealed record LocalizationPointerCandidateRow(LocalizationPointerCandidate Candidate,LocalizationRecord Record,int Occurrence)
{
    public string Label=>$"{Candidate.RawLanguageCode} · occurrence {Occurrence}"+(Candidate.IsSelectedStoredBlock?" · current language":"");
}

public partial class StringsView
{
    private sealed record PointerContextChoice(string Label,bool ShowAll,bool CurrentOnly,int LanguageIndex,string RawLanguageCode)
    {
        public bool Includes(LocalizationPointerCandidate candidate)=>ShowAll||CurrentOnly&&candidate.IsSelectedStoredBlock||
            !CurrentOnly&&!ShowAll&&candidate.LanguageIndex==LanguageIndex&&string.Equals(candidate.RawLanguageCode,RawLanguageCode,StringComparison.Ordinal);
    }

    private LocalizationRelationshipCoordinator? relationshipCoordinator;
    private readonly HashSet<Task> relationshipTasks=[];
    private readonly HashSet<Task> pointerNavigationTasks=[];
    private CancellationTokenSource? pointerNavigationCancellation;
    private long pointerNavigationGeneration;
    private ResourceIdentity? pointerNavigationOrigin,pointerNavigationTarget;
    private bool pointerNavigationSelectionHooked;
    private IReadOnlyList<LocalizationPointerCandidate> currentPointerCandidates=Array.Empty<LocalizationPointerCandidate>();
    private IReadOnlyList<LocalizationPointerCandidateRow> pointerCandidateRows=Array.Empty<LocalizationPointerCandidateRow>();
    public Func<ResourceIdentity,CancellationToken,Task>? RelatedNavigator { get; set; }
    public Func<ResourceIdentity,string?>? RelatedLabelResolver { get; set; }
    public IReadOnlyList<LocalizationRelatedResource> CurrentRelatedResources=>RelatedResources.ItemsSource as LocalizationRelatedResource[]??[];
    public IReadOnlyList<LocalizationPointerCandidate> CurrentPointerCandidates=>currentPointerCandidates;

    private void UpdateRelatedSelection(LocalizationRecord? row,LocalizationSession? current,long generation,CancellationToken token)
    {
        RelatedResources.ItemsSource=null;RelatedEvidence.Text="";OpenRelated.IsEnabled=false;
        ClearPointerCandidates();
        RelatedStatus.Text=row is null?"Select a string":"Loading relationships…";
        if(row is null||current is null)return;
        relationshipCoordinator??=new();
        var coordinator=relationshipCoordinator;
        var task=LoadRelatedAsync(coordinator,current,row,generation,token);
        relationshipTasks.Add(task);_=DrainCompletedRelatedAsync(task);
    }
    private async Task DrainCompletedRelatedAsync(Task task)
    {try{await task;}catch{}finally{relationshipTasks.Remove(task);}}
    private async Task DrainRelatedAsync()
    {
        CancelPointerNavigation();ClearPointerCandidates();
        try{await Task.WhenAll(relationshipTasks.Concat(pointerNavigationTasks).ToArray());}catch{}
    }

    internal void CancelPointerNavigation()
    {
        pointerNavigationGeneration++;
        pointerNavigationOrigin=pointerNavigationTarget=null;
        pointerNavigationCancellation?.Cancel();
    }
    private void ClearPointerCandidates()
    {
        currentPointerCandidates=Array.Empty<LocalizationPointerCandidate>();
        pointerCandidateRows=Array.Empty<LocalizationPointerCandidateRow>();
        PointerCandidateRows.ItemsSource=Array.Empty<LocalizationPointerCandidateRow>();
        PointerCandidatesExpander.IsExpanded=false;PointerCandidatesExpander.Visibility=Visibility.Collapsed;
        PointerContextFilter.ItemsSource=new[]{new PointerContextChoice("All languages",true,false,-1,""),new PointerContextChoice("Current language",false,true,-1,"")};
        PointerContextFilter.SelectedIndex=1;PointerCandidateSummary.Text="Same-key occurrences";
        PointerCandidateEvidence.Text="";OpenPointerCandidate.IsEnabled=false;
    }

    private void SetPointerCandidates(IReadOnlyList<LocalizationPointerCandidate> candidates,IReadOnlyList<LocalizationRecord> records,bool expandChoices)
    {
        currentPointerCandidates=candidates.ToArray();
        var byIdentity=records.ToDictionary(record=>record.Identity.Key,StringComparer.Ordinal);
        var grouped=candidates.Select(candidate=>byIdentity.TryGetValue(candidate.Identity.Key,out var record)?(Candidate:candidate,Record:record):(Candidate:candidate,Record:(LocalizationRecord?)null))
            .Where(item=>item.Record is not null).GroupBy(item=>(item.Candidate.LanguageIndex,item.Candidate.RawLanguageCode,item.Candidate.IsSelectedStoredBlock));
        var rows=grouped.SelectMany(group=>group.OrderBy(item=>item.Record!.RecordOffset).ThenBy(item=>item.Record!.KeyIndex)
                .Select((item,index)=>new LocalizationPointerCandidateRow(item.Candidate,item.Record!,index+1)))
            .OrderByDescending(row=>row.Candidate.IsSelectedStoredBlock).ThenBy(row=>row.Candidate.LanguageIndex)
            .ThenBy(row=>row.Record.RecordOffset).ToArray();
        pointerCandidateRows=rows;
        var otherContexts=rows.Where(row=>!row.Candidate.IsSelectedStoredBlock)
            .Select(row=>(row.Candidate.LanguageIndex,row.Candidate.RawLanguageCode)).Distinct()
            .OrderBy(context=>context.LanguageIndex).ThenBy(context=>context.RawLanguageCode,StringComparer.Ordinal).ToArray();
        var choices=new List<PointerContextChoice>
        {
            new("All languages",true,false,-1,""),
            new("Current language",false,true,-1,"")
        };
        var ordinals=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var context in otherContexts)
        {
            var ordinal=ordinals.GetValueOrDefault(context.RawLanguageCode)+1;ordinals[context.RawLanguageCode]=ordinal;
            choices.Add(new($"{context.RawLanguageCode} · context {ordinal}",false,false,context.LanguageIndex,context.RawLanguageCode));
        }
        PointerContextFilter.ItemsSource=choices;
        var selectedRows=rows.Where(row=>row.Candidate.IsSelectedStoredBlock).ToArray();
        var hasSelectedContext=selectedRows.Length>0;
        PointerContextFilter.SelectedItem=choices[hasSelectedContext?1:0];
        PointerCandidateRows.ItemsSource=hasSelectedContext?selectedRows:rows;
        OpenPointerCandidate.IsEnabled=false;
        PointerCandidatesExpander.Visibility=rows.Length==0?Visibility.Collapsed:Visibility.Visible;
        PointerCandidatesExpander.IsExpanded=rows.Length>0&&expandChoices;
        var otherCount=otherContexts.Length;
        PointerCandidateSummary.Text=rows.Length==0?"Same-key occurrences":$"{rows.Length:N0} pointer occurrences · {otherCount:N0} other language {(otherCount==1?"context":"contexts")}";
        PointerCandidateEvidence.Text=string.Join("\n",rows.Select(row=>$"{row.Label}\nIdentity {row.Candidate.Identity.Key}\nLanguage index {row.Candidate.LanguageIndex} · raw code {row.Candidate.RawLanguageCode}\n{row.Record.RecordExtent.RelativeSource} · {row.Record.RecordExtent.Offset} + {row.Record.RecordExtent.Length}"));
    }

    private void PointerContextFilter_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(PointerContextFilter.SelectedItem is not PointerContextChoice choice||PointerCandidateRows is null)return;
        PointerCandidateRows.ItemsSource=pointerCandidateRows.Where(row=>choice.Includes(row.Candidate)).ToArray();
        PointerCandidateRows.SelectedItem=null;OpenPointerCandidate.IsEnabled=false;
    }

    private void PointerCandidateSelectionChanged(object sender,SelectionChangedEventArgs e)=>
        OpenPointerCandidate.IsEnabled=PointerCandidateRows.SelectedItem is LocalizationPointerCandidateRow;

    private async void OpenPointerCandidate_Click(object sender,RoutedEventArgs e)=>await OpenPointerCandidateAsync();
    private async void PointerCandidate_DoubleClick(object sender,MouseButtonEventArgs e)=>await OpenPointerCandidateAsync();

    private async Task OpenPointerCandidateAsync()
    {
        if(PointerCandidateRows.SelectedItem is not LocalizationPointerCandidateRow row||session is not{}current)return;
        pointerNavigationCancellation?.Cancel();
        var cancellation=new CancellationTokenSource();pointerNavigationCancellation=cancellation;
        var generation=++pointerNavigationGeneration;
        pointerNavigationOrigin=SelectedRecord?.Identity;pointerNavigationTarget=row.Candidate.Identity;
        if(!pointerNavigationSelectionHooked){Rows.SelectionChanged+=PointerNavigationSelectionChanged;pointerNavigationSelectionHooked=true;}
        var task=NavigatePointerCandidateAsync(row,current,generation,cancellation.Token);
        pointerNavigationTasks.Add(task);
        try{await task;}
        finally
        {
            pointerNavigationTasks.Remove(task);
            if(ReferenceEquals(pointerNavigationCancellation,cancellation))
            {
                pointerNavigationCancellation=null;pointerNavigationOrigin=pointerNavigationTarget=null;
            }
            if(pointerNavigationTasks.Count==0&&pointerNavigationSelectionHooked)
            {
                Rows.SelectionChanged-=PointerNavigationSelectionChanged;pointerNavigationSelectionHooked=false;
            }
            cancellation.Dispose();
        }
    }

    private void PointerNavigationSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(pointerNavigationCancellation is null||Rows.SelectedItem is not LocalizationRecord selected)return;
        if(selected.Identity!=pointerNavigationOrigin&&selected.Identity!=pointerNavigationTarget)CancelPointerNavigation();
    }

    private async Task NavigatePointerCandidateAsync(LocalizationPointerCandidateRow row,LocalizationSession current,long generation,CancellationToken token)
    {
        try
        {
            await SelectRecordAsync(row.Candidate.Identity,token);
            token.ThrowIfCancellationRequested();
            if(generation!=pointerNavigationGeneration||!ReferenceEquals(current,session)||SelectedRecord?.Identity!=row.Candidate.Identity)return;
        }
        catch(OperationCanceledException){}
        catch(Exception error)
        {
            if(generation==pointerNavigationGeneration&&ReferenceEquals(current,session)&&!token.IsCancellationRequested)RelatedStatus.Text=error.Message;
        }
    }

    private async Task LoadRelatedAsync(LocalizationRelationshipCoordinator coordinator,LocalizationSession current,LocalizationRecord row,long generation,CancellationToken token)
    {
        var activity=BeginActivity("Finding relationships",30);
        string? terminal=null;
        try
        {
            var result=await Task.Run(()=>coordinator.ResolveSelectedAsync(current,row.Identity,token),token);
            token.ThrowIfCancellationRequested();if(generation!=detailGeneration||!ReferenceEquals(current,session))return;
            var candidateRecords=result.PointerCandidates.Count==0?Array.Empty<LocalizationRecord>()
                :(await current.GetRecordsAsync(result.PointerCandidates.Select(candidate=>candidate.Identity).ToArray(),token)).ToArray();
            token.ThrowIfCancellationRequested();if(generation!=detailGeneration||!ReferenceEquals(current,session))return;
            var rows=new List<LocalizationRelatedResource>();
            foreach(var relationship in result.Relationships)
            {
                if(relationship.EvidencePath.Count==0||relationship.EvidencePath.Any(edge=>!edge.Evidence.Verified||edge.NavigationTarget is null))continue;
                void Add(string label,ResourceIdentity? target){if(target is not null&&!rows.Any(item=>item.Identity==target))rows.Add(new(RelatedLabelResolver?.Invoke(target)??label,target,relationship.EvidencePath));}
                Add("Audio",relationship.AudioResourceIdentity);Add("Dialogue event",relationship.DialogueEventIdentity);Add("Dialogue header",relationship.DialogueHeaderIdentity);
            }
            SetPointerCandidates(result.PointerCandidates,candidateRecords,rows.Count==0||result.PointerCandidates.Count(candidate=>candidate.IsSelectedStoredBlock)>1);
            RelatedResources.ItemsSource=rows.ToArray();
            RelatedStatus.Text=rows.Count>0?$"{rows.Count} related assets":result.Diagnostics.Any(item=>item.Code=="LocalizationPending")?"Indexing relationships…":result.Diagnostics.Any(item=>item.Status==LocalizationReferenceLinkStatus.Ambiguous)?"Ambiguous reference":result.Diagnostics.Any(item=>item.Code.Contains("Missing",StringComparison.Ordinal))?"Missing reference":"No recorded links";
            RelatedEvidence.Text=string.Join("\n",result.Diagnostics.Select(item=>$"{item.Code}: {item.Message}"))+
                string.Join("\n",rows.SelectMany(item=>item.Evidence).Distinct().Select(edge=>$"\n{edge.Kind} · {edge.Reference.Namespace} · {edge.Reference.Id}\n{edge.Evidence.Source}\n{edge.Evidence.PhysicalExtent}"));
        }
        catch(OperationCanceledException){if(generation==detailGeneration&&ReferenceEquals(current,session))terminal="Cancelled";}
        catch(Exception error){if(generation==detailGeneration&&ReferenceEquals(current,session)){terminal="Failed";RelatedStatus.Text=error is ArchiveException{Code:"SourceChanged"}?"Source changed. Reopen the archive.":error.Message;}}
        finally{EndActivity(activity,terminal);}
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
