using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization.Model;
using Microsoft.Win32;
namespace Deviant.Desktop;

public partial class StringsView:UserControl,IAsyncDisposable
{
 private LocalizationSession? session;
 private readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(250)};
 private CancellationTokenSource? queryCancellation,detailCancellation,jobCancellation,loadCancellation;
 private readonly HashSet<ResourceIdentity> selected=[];
 private readonly SemaphoreSlim loading=new(1,1);
 private long loadGeneration;
 private long queryGeneration,detailGeneration;
 private long offset;
 private bool refreshing,choicesPending;
 private Task? activeJob;
 internal Func<ArchiveSnapshot,string,IProgress<LocalizationReadProgress>?,CancellationToken,ISourceRevisionProvider?,Task<LocalizationSession>>? LocalizationSessionFactoryOverride { get; set; }
 public StringsView(){InitializeComponent();refreshing=true;Kinds.ItemsSource=new[]{"All records","Text","Pointer","Opaque"};Kinds.SelectedIndex=0;Statuses.ItemsSource=new[]{"All statuses","Complete","Unresolved","Malformed","Unsupported"};Statuses.SelectedIndex=0;SortBox.ItemsSource=new[]{new AssetSortChoice("Language",AssetSort.Default),new("Key",AssetSort.Name),new("Status",AssetSort.Availability)};SortBox.SelectedIndex=0;refreshing=false;debounce.Tick+=async(_,_)=>{debounce.Stop();if(choicesPending){choicesPending=false;UpdateChoices();}await RefreshAsync();};}
 public LocalizationSession? CurrentSession=>session;
 public LocalizationRecord? SelectedRecord=>Rows.SelectedItem as LocalizationRecord;
 public event EventHandler? SelectedRecordChanged;
 public event EventHandler? ChoicesChanged;
 public event EventHandler? EnrichmentFinished;
 public IReadOnlyDictionary<string,string> CaptureFilter()=>new Dictionary<string,string>{{"search",Search.Text},{"language",Languages.SelectedItem as string??"All languages"},{"kind",Kinds.SelectedIndex.ToString()},{"status",Statuses.SelectedIndex.ToString()},{"source",Sources.SelectedItem is LocalizationSourceChoice{Identity.Ordinal:>=0} source?source.Identity.Key:""},{"sort",((SortBox.SelectedItem as AssetSortChoice)?.Sort??AssetSort.Default).ToString()},{"descending",(Descending.IsChecked==true).ToString()}};
 public async Task ApplyFilterAsync(IReadOnlyDictionary<string,string> values,CancellationToken token=default)
 {
  var current=session??throw new InvalidOperationException("Open a workspace first.");var language=values.GetValueOrDefault("language","All languages");
  if(language!="All languages"&&!current.Languages.Contains(language))await current.EnrichmentTask.WaitAsync(token);
  token.ThrowIfCancellationRequested();if(!ReferenceEquals(current,session))throw new OperationCanceledException();UpdateChoices();
  if(!Languages.Items.Cast<string>().Contains(language))throw new InvalidOperationException("The saved language is unavailable.");
  var key=values.GetValueOrDefault("source","");var source=key.Length==0?Sources.Items.Cast<LocalizationSourceChoice>().First():Sources.Items.Cast<LocalizationSourceChoice>().FirstOrDefault(item=>item.Identity.Key==key)??throw new InvalidOperationException("The saved source is unavailable.");
  var sort=Enum.TryParse<AssetSort>(values.GetValueOrDefault("sort","Default"),out var parsed)?parsed:throw new InvalidOperationException("The saved sort is unavailable.");
  var choice=SortBox.Items.Cast<AssetSortChoice>().FirstOrDefault(item=>item.Sort==sort)??throw new InvalidOperationException("The saved sort is unavailable.");
  refreshing=true;try{Search.Text=values.GetValueOrDefault("search","");Languages.SelectedItem=language;Sources.SelectedItem=source;Kinds.SelectedIndex=int.TryParse(values.GetValueOrDefault("kind"),out var kind)?Math.Clamp(kind,0,3):0;Statuses.SelectedIndex=int.TryParse(values.GetValueOrDefault("status"),out var status)?Math.Clamp(status,0,4):0;SortBox.SelectedItem=choice;Descending.IsChecked=bool.TryParse(values.GetValueOrDefault("descending"),out var descending)&&descending;}finally{refreshing=false;}offset=0;await RefreshAsync();
 }
 public async Task SelectRecordAsync(ResourceIdentity identity,CancellationToken token=default)
 {
  var current=session;if(current is null)return;var records=await current.GetRecordsAsync([identity],token);if(records.Count==0&&!current.IsEnrichmentComplete){await current.EnrichmentTask.WaitAsync(token);records=await current.GetRecordsAsync([identity],token);}if(!ReferenceEquals(current,session))throw new OperationCanceledException();if(records.Count==0)throw new InvalidOperationException("The saved string is unavailable.");var row=records[0];
  await ApplyFilterAsync(new Dictionary<string,string>{{"search",row.Key??""},{"language",row.Language},{"source",row.ResourceIdentity.Key}},token);
  var visible=CurrentRecords.FirstOrDefault(item=>item.Identity==identity);if(visible is null){refreshing=true;try{Rows.ItemsSource=records;}finally{refreshing=false;}visible=row;}
  Rows.SelectedItem=visible;Rows.ScrollIntoView(visible);
 }
 public IReadOnlyList<LocalizationRecord> CurrentRecords=>Rows.ItemsSource is IEnumerable<LocalizationRecord> records?records.ToArray():[];
 public async Task LoadAsync(ArchiveSnapshot snapshot,string cacheLocation,CancellationToken token=default)
 {
  var activity=BeginActivity("Opening strings",100);string? terminal=null;
  var generation=++loadGeneration;loadCancellation?.Cancel();
  try
  {
   await loading.WaitAsync(token);
   try
   {
   if(generation!=loadGeneration)return;
   await DisposeSessionCoreAsync();loadCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
   Status.Text="Reading strings…";
   var opened=LocalizationSessionFactoryOverride is{}factory
    ?await factory(snapshot,cacheLocation,null,loadCancellation.Token,null)
    :await LocalizationSession.OpenAsync(snapshot,cacheLocation,cancellationToken:loadCancellation.Token);
   if(generation!=loadGeneration||loadCancellation.IsCancellationRequested){await opened.DisposeAsync();return;}
   session=opened;session.Updated+=Updated;UpdateChoices();_=ObserveAsync(opened);await RefreshAsync();
   }
   finally{loading.Release();}
  }
  catch(OperationCanceledException){terminal=generation==loadGeneration?"Cancelled":null;throw;}
  catch{terminal=generation==loadGeneration?"Failed":null;throw;}
  finally{EndActivity(activity,terminal);}
 }
 private async Task ObserveAsync(LocalizationSession current)
 {
  var activity=0L;
  if(!current.EnrichmentTask.IsCompleted)
  {
   var initial=current.CompletedResources;var total=current.Resources.Count;
   activity=BeginActivity("Indexing strings",80,initial,total);
   if(ReferenceEquals(current,session))enrichmentActivityId=activity;
  }
  try
  {
   await current.EnrichmentTask;
   EndActivity(activity);
   await Dispatcher.InvokeAsync(async()=>{if(!ReferenceEquals(current,session))return;UpdateChoices();await RefreshAsync();}).Task.Unwrap();
  }
  catch(OperationCanceledException){EndActivity(activity,ReferenceEquals(current,session)?"Cancelled":null);}
  catch(Exception e)
  {
   Deviant.Assets.WorkflowDiagnostics.Failure("Strings.Index",e);
   EndActivity(activity,ReferenceEquals(current,session)?"Failed":null);
   await Dispatcher.InvokeAsync(()=>{if(ReferenceEquals(current,session))Status.Text=e.Message;});
  }
  finally{if(enrichmentActivityId==activity)enrichmentActivityId=0;if(ReferenceEquals(current,session))EnrichmentFinished?.Invoke(this,EventArgs.Empty);}
 }
 private void Updated(object? sender,LocalizationSessionUpdate update)
 {_=Dispatcher.InvokeAsync(()=>{if(!ReferenceEquals(sender,session))return;choicesPending=true;Status.Text=$"{update.RecordCount:N0} strings · {update.CompletedResources:N0} / {update.TotalResources:N0} sources";if(enrichmentActivityId!=0)UpdateActivity(enrichmentActivityId,"Indexing strings",update.CompletedResources,update.TotalResources);if(!debounce.IsEnabled)debounce.Start();});}
 private void UpdateChoices()
 {
  if(session is null)return;refreshing=true;
  try{var language=Languages.SelectedItem as string;var languages=new[]{"All languages"}.Concat(session.Languages).ToArray();if(!Languages.Items.Cast<string>().SequenceEqual(languages)){Languages.ItemsSource=languages;Languages.SelectedItem=languages.Contains(language)?language:"All languages";}
   var source=(Sources.SelectedItem as LocalizationSourceChoice)?.Identity;
   if(Sources.Items.Count!=session.Sources.Count+1){Sources.ItemsSource=new[]{new LocalizationSourceChoice(new(session.Snapshot.IndexContentId,-1),"All sources","",0,0)}.Concat(session.Sources).ToArray();
   Sources.SelectedItem=Sources.Items.Cast<LocalizationSourceChoice>().FirstOrDefault(s=>s.Identity==source)??Sources.Items[0];}
   ExportSelected.IsEnabled=session.IsEnrichmentComplete&&selected.Count>0;ExportMatches.IsEnabled=session.IsEnrichmentComplete;}
  finally{refreshing=false;}ChoicesChanged?.Invoke(this,EventArgs.Empty);
 }
 private LocalizationQuery Query()=>new(Search.Text,string.Equals(Languages.SelectedItem as string,"All languages",StringComparison.Ordinal)?null:Languages.SelectedItem as string,
  Sources.SelectedItem is LocalizationSourceChoice{Identity.Ordinal:>=0} source?source.Identity:null,Kinds.SelectedIndex switch{1=>LocalizationRecordKind.Text,2=>LocalizationRecordKind.Pointer,3=>LocalizationRecordKind.Opaque,_=>null},
  Availability:Statuses.SelectedIndex switch{1=>AssetAvailability.Available,2=>AssetAvailability.Missing,3=>AssetAvailability.Malformed,4=>AssetAvailability.Unsupported,_=>null},Offset:offset,Limit:100,Sort:(SortBox.SelectedItem as AssetSortChoice)?.Sort??AssetSort.Default,Descending:Descending.IsChecked==true);
 public async Task RefreshAsync()
 {
  if(session is not{}current)return;queryCancellation?.Cancel();queryCancellation?.Dispose();queryCancellation=new();var token=queryCancellation.Token;var generation=++queryGeneration;var activity=BeginActivity("Searching strings",60);
  string? terminal=null;
  try{var page=await current.QueryAsync(Query(),token);if(generation!=queryGeneration||!ReferenceEquals(current,session))return;
   if(offset>0&&offset>=page.Total){offset=Math.Max(0,(page.Total-1)/100*100);await RefreshAsync();return;}
   var previous=(Rows.SelectedItem as LocalizationRecord)?.Identity;
   refreshing=true;try{Rows.ItemsSource=page.Records;foreach(var row in page.Records.Where(r=>selected.Contains(r.Identity)))Rows.SelectedItems.Add(row);if(previous is not null&&page.Records.FirstOrDefault(r=>r.Identity==previous) is{}restored)Rows.SelectedItem=restored;}finally{refreshing=false;}
   Page.Text=$"{(page.Total==0?0:offset+1):N0}–{Math.Min(offset+page.Records.Count,page.Total):N0} / {page.Total:N0}";Status.Text=current.IsEnrichmentComplete?$"{page.Total:N0} strings":$"{page.Total:N0} strings · indexing";UpdateChoices();await UpdateDetailsAsync();}
  catch(OperationCanceledException){if(generation==queryGeneration)terminal="Cancelled";}catch(Exception e){Deviant.Assets.WorkflowDiagnostics.Failure("Strings.Operation",e);if(generation==queryGeneration){Status.Text=e.Message;terminal="Failed";}}
  finally{EndActivity(activity,terminal);}
 }
 private void FiltersChanged(object sender,RoutedEventArgs e){if(refreshing)return;queryCancellation?.Cancel();offset=0;debounce.Stop();debounce.Start();}
 private void FiltersChanged(object sender,SelectionChangedEventArgs e)=>FiltersChanged(sender,new RoutedEventArgs());
 private async void SelectionChanged(object sender,SelectionChangedEventArgs e)
 {
  if(refreshing)return;foreach(LocalizationRecord removed in e.RemovedItems)selected.Remove(removed.Identity);foreach(LocalizationRecord added in e.AddedItems)selected.Add(added.Identity);
  UpdateChoices();await UpdateDetailsAsync();
 }
 private async Task UpdateDetailsAsync()
 {
  detailCancellation?.Cancel();detailCancellation?.Dispose();detailCancellation=new();var token=detailCancellation.Token;
  var row=Rows.SelectedItem as LocalizationRecord;var generation=++detailGeneration;var current=session;
  SelectedKey.Text=row?.Key??row?.Kind.ToString()??"Select a string";FullText.Text=row?.Text??row?.Diagnostic??"";SelectedRecordChanged?.Invoke(this,EventArgs.Empty);
  var placeholders=row is null?Array.Empty<LocalizationPlaceholderOccurrence>():LocalizationDiscovery.InspectPlaceholders(row).ToArray();
  PlaceholderSummary.Text=placeholders.Length==0?"":"Placeholders: "+string.Join(" · ",placeholders.Select(item=>item.Token).Distinct());
  Advanced.Text=row is null?"":$"{row.Status} · v{row.Version}\nResource {row.ResourceOrdinal}\n{row.RecordExtent.RelativeSource} · {row.RecordExtent.Offset} + {row.RecordExtent.Length}\nLanguage {row.RawLanguageCode}\nEncoding {row.Encoding}\n"+
   string.Join("\n",row.FieldExtents.Select(f=>$"{f.Field}: {f.Offset} + {f.Length}"))+"\n"+string.Join("\n",row.UnknownFields.Select(p=>$"{p.Key}: {p.Value}"));
  if(placeholders.Length>0)Advanced.Text+="\n"+string.Join("\n",placeholders.Select(item=>$"{item.Syntax} · {item.Token} · UTF-16 offset {item.Utf16Offset}"));
  Variants.ItemsSource=OtherOccurrences.ItemsSource=null;OccurrenceSummary.Text="Language occurrences";
  UpdateRelatedSelection(row,current,generation,token);if(row is null||current is null)return;
  var activity=BeginActivity("Loading details",40);
  string? terminal=null;
  try{var variants=await current.GetVariantsAsync(row.Identity,token);var others=await current.GetOtherOccurrencesAsync(row.Identity,token);if(generation==detailGeneration&&ReferenceEquals(current,session)){Variants.ItemsSource=variants;var sourceLabels=current.Sources.ToDictionary(source=>source.Identity.Ordinal,source=>source.Label);OtherOccurrences.ItemsSource=others.Select(record=>new{Source=sourceLabels.GetValueOrDefault(record.ResourceOrdinal,"Source"),record.Language,record.Text});var duplicates=variants.GroupBy(r=>r.RawLanguageCode).Sum(g=>Math.Max(0,g.Count()-1));OccurrenceSummary.Text=$"{variants.Count:N0} language occurrences · {duplicates:N0} duplicates";}}
  catch(OperationCanceledException){if(generation==detailGeneration&&ReferenceEquals(current,session))terminal="Cancelled";}
  catch(Exception error){Deviant.Assets.WorkflowDiagnostics.Failure("Strings.Operation",error);if(generation==detailGeneration&&ReferenceEquals(current,session)){Status.Text=error.Message;terminal="Failed";}}
  finally{EndActivity(activity,terminal);}
 }
 private async void Previous_Click(object sender,RoutedEventArgs e){if(offset>=100){offset-=100;await RefreshAsync();}}
 private async void Next_Click(object sender,RoutedEventArgs e){offset+=100;await RefreshAsync();}
 private async void ExportSelected_Click(object sender,RoutedEventArgs e)=>await ExportAsync(LocalizationExportSelection.Selected(selected));
 private async void ExportMatches_Click(object sender,RoutedEventArgs e)=>await ExportAsync(LocalizationExportSelection.Matching(Query() with{Offset=0}));
 private async Task ExportAsync(LocalizationExportSelection selection)
 {
  if(session is not{}current||activeJob is{IsCompleted:false})return;
  var dialog=new SaveFileDialog{Title="Export strings",Filter="JSON (*.json)|*.json|CSV (*.csv)|*.csv",FileName="strings.json",AddExtension=true,OverwritePrompt=true};if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
  jobCancellation=new();var token=jobCancellation.Token;Status.Text="Exporting strings…";
  var activity=BeginActivity("Exporting strings",90);
  var task=current.ExportAsync(selection,dialog.FilterIndex==2?LocalizationExportFormat.Csv:LocalizationExportFormat.Json,dialog.FileName,cancellationToken:token);activeJob=task;
  try{var report=await task;Status.Text=$"{report.Manifest.CompletedCount:N0} completed · {report.Manifest.FailedCount:N0} failed · {report.Manifest.CancelledCount:N0} cancelled";EndActivity(activity,report.Manifest.CancelledCount>0?"Cancelled":null);}
  catch(OperationCanceledException){Status.Text="Export cancelled";EndActivity(activity,"Cancelled");}
  catch(Exception e){Deviant.Assets.WorkflowDiagnostics.Failure("Strings.Operation",e);Status.Text=e.Message;EndActivity(activity,"Failed");}
  finally{activeJob=null;jobCancellation.Dispose();jobCancellation=null;}
 }
 private void Cancel_Click(object sender,RoutedEventArgs e)=>CancelActivity();
 private async Task DisposeSessionCoreAsync(){debounce.Stop();choicesPending=false;++queryGeneration;++detailGeneration;queryCancellation?.Cancel();detailCancellation?.Cancel();jobCancellation?.Cancel();loadCancellation?.Cancel();await DrainLocalizationRetryAsync();if(activeJob is not null)try{await activeJob;}catch{}await DrainRelatedAsync();var old=session;session=null;relationshipCoordinator=null;if(old is not null){old.Updated-=Updated;await old.DisposeAsync();}loadCancellation?.Dispose();loadCancellation=null;selected.Clear();offset=0;refreshing=true;try{Rows.ItemsSource=null;Sources.ItemsSource=null;Languages.ItemsSource=null;}finally{refreshing=false;}await UpdateDetailsAsync();}
 public async ValueTask DisposeAsync(){++loadGeneration;loadCancellation?.Cancel();await loading.WaitAsync();try{await DisposeSessionCoreAsync();}finally{loading.Release();}}
}
