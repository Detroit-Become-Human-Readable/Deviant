using System.IO;
using System.Reflection;
using System.Text.Json;
using Deviant.Browser;
using Deviant.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Catalog;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class StringsRelatedNavigationTests
{
    [Fact]
    public void VerifiedTextLinkOpensConcreteAudioIdentityAndRechecksChangedSource()
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var fixture=new DiscoveryFixture(1);var linked=Path.Combine(fixture.Root,"linked","archive.idx");DiscoveryFixture.WriteLinkedStrings(linked);AudioWindow? window=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window=new(new CacheManager(Path.Combine(fixture.Root,"settings"),new(fixture.Cache)),new AliasStore(fixture.Library)){Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};WindowTestLifetime.Observe(window);window.Show();
                Pump(window.OpenArchiveAsync(linked));var audio=window.CurrentSession!;Pump(audio.EnrichmentTask);Pump(window.ShowStringsAsync());var view=window.StringsBrowser;Pump(view.CurrentSession!.EnrichmentTask);Pump(view.RefreshAsync());Pump(WaitAsync(()=>view.CurrentRecords.Count==2));
                var text=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Text);var pointer=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Pointer);
                var grid=(DataGrid)view.FindName("Rows");grid.SelectedItem=pointer;grid.SelectedItem=text;
                Pump(WaitAsync(()=>view.CurrentRelatedResources.Any(row=>row.Identity.Ordinal==2)));
                var target=view.CurrentRelatedResources.Single(row=>row.Identity.Ordinal==2);Assert.NotEqual(text.Identity,target.Identity);Assert.Null(target.Identity.MemberPath);Assert.All(target.Evidence,edge=>Assert.True(edge.Evidence.Verified));
                ((ListBox)view.FindName("RelatedResources")).SelectedItem=target;Pump((Task)typeof(StringsView).GetMethod("OpenRelatedAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null)!);
                var opened=Assert.Single(window.CurrentRows);Assert.Equal(2,opened.Identity.Ordinal);Assert.Equal("wem",opened.Identity.MemberPath);Assert.NotEqual(text.Identity,opened.Identity);
                var originalName=opened.Media.Name;Pump(audio.SetAliasAsync(opened.Identity,"User label"));Assert.Equal(originalName,opened.Media.Name);Assert.Equal("User label",Assert.Single(audio.ResolveSelection([opened.Identity])).Label);
                Pump(window.ShowStringsAsync());Pump(view.RefreshAsync());grid.SelectedItem=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Pointer);grid.SelectedItem=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Text);Pump(WaitAsync(()=>view.CurrentRelatedResources.Any(row=>row.Identity.Ordinal==2)));
                var volume=audio.Snapshot.Volumes[0].FilePath;var timestamp=File.GetLastWriteTimeUtc(volume);var bytes=File.ReadAllBytes(volume);bytes[28]^=1;File.WriteAllBytes(volume,bytes);File.SetLastWriteTimeUtc(volume,timestamp);
                grid.SelectedItem=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Pointer);grid.SelectedItem=view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Text);try{Pump(WaitAsync(()=>view.CurrentRelatedResources.Count==0&&((TextBlock)view.FindName("RelatedStatus")).Text.Contains("changed",StringComparison.OrdinalIgnoreCase)));}
                catch(Exception waitError)
                {
                    string? code=null;string? message=null;object? diagnostics=null;
                    var coordinator=(LocalizationRelationshipCoordinator?)typeof(StringsView).GetField("relationshipCoordinator",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view);
                    try{if(coordinator is not null){var result=Pump(coordinator.ResolveSelectedAsync(view.CurrentSession!,view.CurrentRecords.Single(row=>row.Kind==LocalizationRecordKind.Text).Identity));diagnostics=result.Diagnostics;}}
                    catch(ArchiveException error){code=error.Code;message=error.Message;}
                    catch(Exception error){code=error.GetType().Name;message=error.Message;}
                    var actual=new{Status=((TextBlock)view.FindName("RelatedStatus")).Text,Evidence=((TextBox)view.FindName("RelatedEvidence")).Text,Rows=view.CurrentRelatedResources.Select(row=>row.Identity),ServiceCode=code,ServiceMessage=message,Diagnostics=diagnostics,SessionComplete=view.CurrentSession?.IsEnrichmentComplete,MetadataContentId=view.CurrentSession?.MetadataContentId,Selected=(grid.SelectedItem as LocalizationRecord)?.Identity};
                    throw new InvalidOperationException("Changed-source Related result: "+JsonSerializer.Serialize(actual),waitError);
                }
                Assert.Empty(view.CurrentRelatedResources);
            }
            catch(Exception error){failure=error;}
            finally
            {
                try{if(window is not null)Pump(WindowTestLifetime.CloseAsync(window));}
                catch(Exception error){failure=failure is null?error:new AggregateException(failure,error);}
                if(window is null||WindowTestLifetime.IsClosed(window))
                {try{fixture.Dispose();}catch(Exception error){failure=failure is null?error:new AggregateException(failure,error);}}
                else failure=new InvalidOperationException("Window did not close; synthetic fixture retained at "+fixture.Root,failure);
            }
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(25)),"Strings related check timed out.");if(failure is not null)throw new InvalidOperationException("Strings related check failed.",failure);
    }
    private static async Task WaitAsync(Func<bool> ready){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!ready())await Task.Delay(20,timeout.Token);}
    private static T Pump<T>(Task<T> task){Pump((Task)task);return task.GetAwaiter().GetResult();}
    private static void Pump(Task task){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
