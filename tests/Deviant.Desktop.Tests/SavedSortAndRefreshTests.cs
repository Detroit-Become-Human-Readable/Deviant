using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SavedSortAndRefreshTests
{
    [Fact]
    public void AudioPagingClampsStaleOffsetsAndStopsAtTheLastResult()
    {
        WorkspaceCommitTests.Run((window,fixture)=>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));Pump(window.CurrentSession!.EnrichmentTask);
            Pump((Task)Invoke(window,"QueryAsync",true)!);
            var field=typeof(AudioWindow).GetField("currentQuery",BindingFlags.NonPublic|BindingFlags.Instance)!;
            field.SetValue(window,((AudioExplorerQuery)field.GetValue(window)!) with { Offset=200 });
            Pump((Task)Invoke(window,"QueryAsync",false)!);
            Assert.Equal(0,((AudioExplorerQuery)field.GetValue(window)!).Offset);
            Assert.Equal(2,window.CurrentRows.Count);
            Assert.Equal("1–2 of 2",((TextBlock)window.FindName("PageText")).Text);
            Assert.False(((Button)window.FindName("AudioPrevious")).IsEnabled);
            Assert.False(((Button)window.FindName("AudioNext")).IsEnabled);
            Invoke(window,"Next_Click",window,new RoutedEventArgs());
            Assert.Equal(0,((AudioExplorerQuery)field.GetValue(window)!).Offset);
            ((TextBox)window.FindName("SearchBox")).Text="No matching clip";
            field.SetValue(window,((AudioExplorerQuery)field.GetValue(window)!) with { Offset=100 });
            Pump((Task)Invoke(window,"QueryAsync",false)!);
            Assert.Empty(window.CurrentRows);
            Assert.Equal("0–0 of 0",((TextBlock)window.FindName("PageText")).Text);
            Assert.Equal(0,((AudioExplorerQuery)field.GetValue(window)!).Offset);
            Assert.False(((Button)window.FindName("AudioNext")).IsEnabled);
        });
    }

    [Fact]
    public void SameArchiveOpenKeepsFocusedConcreteIdentityAndUnsavedAliasDraft()
    {
        WorkspaceCommitTests.Run((window,fixture)=>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));Pump(window.CurrentSession!.EnrichmentTask);Pump(Task.Delay(100));
            var list=(ListView)window.FindName("MediaList");list.SelectedItem=window.CurrentRows.Last();var identity=((AudioExplorerRow)list.SelectedItem).Identity;
            var alias=(TextBox)window.FindName("AliasBox");alias.Text="Unsaved draft";
            Pump(window.OpenArchiveAsync(fixture.Index));Pump(Task.Delay(100));
            Assert.Equal(identity,((AudioExplorerRow)list.SelectedItem).Identity);Assert.Equal("Unsaved draft",alias.Text);
        });
    }

    [Fact]
    public void AudioSavedSortRestoresControlsAndFullResultOrderAfterProfileReadback()
    {
        WorkspaceCommitTests.Run((window,fixture)=>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));Invoke(window,"ShowBrowse","audio");Pump(window.CurrentSession!.EnrichmentTask);Pump(Task.Delay(100));
            window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Deviant;component/ControlStyles.xaml",UriKind.Relative)});
            var sort=(ComboBox)window.FindName("AudioSortBox");sort.SelectedItem=sort.Items.Cast<object>().Single(item=>(AssetSort)item.GetType().GetProperty("Sort")!.GetValue(item)! ==AssetSort.Duration);
            AssertChoiceLabels(sort,"Duration");
            ((CheckBox)window.FindName("AudioDescending")).IsChecked=true;Pump((Task)Invoke(window,"QueryAsync",true)!);
            Assert.Equal(1,window.CurrentRows[0].Identity.Ordinal);
            var values=(IReadOnlyDictionary<string,string>)Invoke(window,"CaptureAudioFilter")!;var library=new LibraryStore(fixture.Library);var workspace=Pump(library.ReadAsync()).Workspaces.Single();
            var saved=Pump(library.SaveFilterAsync(workspace.Id,"audio","Sorted audio",values));var readback=Pump(new LibraryStore(fixture.Library).ReadAsync());Assert.Equal("Duration",readback.Filters.Single(item=>item.Id==saved.Id).Values["sort"]);Assert.Equal("True",readback.Filters.Single(item=>item.Id==saved.Id).Values["descending"]);
            sort.SelectedIndex=0;((CheckBox)window.FindName("AudioDescending")).IsChecked=false;
            Pump((Task)Invoke(window,"RefreshLibraryAsync")!);Invoke(window,"ShowLibrary","filters",null);var rows=(ListView)window.FindName("LibraryList");rows.SelectedItem=rows.Items.Cast<object>().Single(item=>((SavedFilter?)item.GetType().GetProperty("Filter")!.GetValue(item))?.Id==saved.Id);Pump((Task)Invoke(window,"OpenLibrarySelectionAsync")!);
            Assert.Equal(AssetSort.Duration,(AssetSort)sort.SelectedItem.GetType().GetProperty("Sort")!.GetValue(sort.SelectedItem)!);Assert.True(((CheckBox)window.FindName("AudioDescending")).IsChecked);Assert.Equal(1,window.CurrentRows[0].Identity.Ordinal);
            Pump(window.ShowStringsAsync());Pump(window.StringsBrowser.CurrentSession!.EnrichmentTask);Pump(window.StringsBrowser.RefreshAsync());
            var stringSort=(ComboBox)window.StringsBrowser.FindName("SortBox");AssertChoiceLabels(stringSort,(string)stringSort.SelectedItem.GetType().GetProperty("Label")!.GetValue(stringSort.SelectedItem)!);
        });
    }
    private static void AssertChoiceLabels(ComboBox combo,string expected)
    {
        combo.UpdateLayout();Pump(Task.Delay(75));
        Assert.Contains(expected,Descendants(combo).OfType<TextBlock>().Where(block=>block.IsVisible).Select(block=>block.Text));
        combo.IsDropDownOpen=true;combo.UpdateLayout();Pump(Task.Delay(75));
        try
        {
            for(var index=0;index<combo.Items.Count;index++)
            {
                var item=combo.Items[index];var label=(string)item.GetType().GetProperty("Label")!.GetValue(item)!;
                var container=(ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(index);container.ApplyTemplate();container.UpdateLayout();
                var texts=Descendants(container).OfType<TextBlock>().Select(block=>block.Text).ToArray();
                Assert.Contains(label,texts);Assert.DoesNotContain(texts,text=>text.Contains("AssetSortChoice",StringComparison.Ordinal));
            }
        }
        finally{combo.IsDropDownOpen=false;}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++){var child=VisualTreeHelper.GetChild(parent,index);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,args);
    private static T Pump<T>(Task<T> task){Pump((Task)task);return task.GetAwaiter().GetResult();}
    private static void Pump(Task task){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
