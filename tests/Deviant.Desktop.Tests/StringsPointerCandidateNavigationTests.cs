using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class StringsPointerCandidateNavigationTests
{
    [Fact]
    public void DuplicateStoredBlockPointersRemainExplicitAndOtherContextsCanBeOpened()
    {
        using var generated = new DiscoveryFixture(0);
        var localized = LocalizationResource(
            ("ENG", TextAndPointers("SHARED_KEY", 2).Append(TextRecord("OTHER_KEY", "Other line")).ToArray()),
            ("FRE", TextAndPointers("SHARED_KEY", 1)));
        DiscoveryFixture.WriteIndex(generated.Index, [(1016u, Container(localized, 3))]);

        WorkspaceCommitTests.Run((window, _) =>
        {
            Pump(window.OpenArchiveAsync(generated.Index));
            var view = window.StringsBrowser;
            Pump(view.LoadAsync(window.CurrentSession!.Snapshot, generated.Cache));
            Pump(view.CurrentSession!.EnrichmentTask);
            Pump(view.RefreshAsync());
            Pump(WaitAsync(() => view.CurrentRecords.Any(record => record.Kind == LocalizationRecordKind.Text &&
                record.Key == "SHARED_KEY" && record.LanguageIndex == 0) &&
                view.CurrentRecords.Any(record => record.Kind == LocalizationRecordKind.Text && record.Key == "OTHER_KEY")));

            Assert.True(view.CurrentRecords.Any(record => record.Kind == LocalizationRecordKind.Text &&
                record.Key == "SHARED_KEY" && record.LanguageIndex == 0),
                $"Expected text absent after refresh. Complete={view.CurrentSession.IsEnrichmentComplete}; " +
                $"Status={((TextBlock)view.FindName("Status")).Text}; " +
                $"Records={string.Join(" | ", view.CurrentRecords.Select(record => $"{record.Kind}:{record.Key}:{record.LanguageIndex}"))}");

            var grid = (DataGrid)view.FindName("Rows");
            var text = view.CurrentRecords.Single(record => record.Kind == LocalizationRecordKind.Text &&
                record.Key == "SHARED_KEY" && record.LanguageIndex == 0);
            var otherTextIdentity = view.CurrentRecords.Single(record => record.Kind == LocalizationRecordKind.Text &&
                record.Key == "OTHER_KEY").Identity;
            grid.SelectedItem = text;
            Pump(WaitAsync(() => view.CurrentPointerCandidates.Count == 3 &&
                ((ListBox)view.FindName("PointerCandidateRows")).Items.Count == 2));

            Assert.Equal(3, view.CurrentPointerCandidates.Count);
            Assert.Equal(2, view.CurrentPointerCandidates.Count(candidate => candidate.IsSelectedStoredBlock));
            Assert.Contains("Ambiguous", ((TextBlock)view.FindName("RelatedStatus")).Text, StringComparison.OrdinalIgnoreCase);
            var candidatesExpander = (Expander)view.FindName("PointerCandidatesExpander");
            Assert.Equal(Visibility.Visible, candidatesExpander.Visibility);
            Assert.True(candidatesExpander.IsExpanded);
            Assert.All(((ListBox)view.FindName("PointerCandidateRows")).Items.Cast<LocalizationPointerCandidateRow>(),
                candidate => Assert.Contains("current language", candidate.Label, StringComparison.OrdinalIgnoreCase));

            var contextFilter = (ComboBox)view.FindName("PointerContextFilter");
            Assert.Equal(3, contextFilter.Items.Count);
            contextFilter.SelectedIndex = FindChoiceIndex(contextFilter, label => label == "All languages");
            var pointerRows = (ListBox)view.FindName("PointerCandidateRows");
            Assert.Equal(3, pointerRows.Items.Count);
            contextFilter.SelectedIndex = FindChoiceIndex(contextFilter, label => label.StartsWith("FRE · context ", StringComparison.Ordinal));
            Assert.Single(pointerRows.Items.Cast<LocalizationPointerCandidateRow>());
            var frenchCandidate = Assert.Single(pointerRows.Items.Cast<LocalizationPointerCandidateRow>());
            Assert.Equal("FRE", frenchCandidate.Candidate.RawLanguageCode);
            Assert.False(frenchCandidate.Candidate.IsSelectedStoredBlock);
            Assert.Contains(frenchCandidate.Candidate.Identity.Key,
                ((TextBlock)view.FindName("PointerCandidateEvidence")).Text, StringComparison.Ordinal);

            pointerRows.SelectedItem = frenchCandidate;
            var open = (Button)view.FindName("OpenPointerCandidate");
            Assert.True(open.IsEnabled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(WaitAsync(() => view.SelectedRecord?.Identity == frenchCandidate.Candidate.Identity));
            Assert.Equal(LocalizationRecordKind.Pointer, view.SelectedRecord!.Kind);
            Assert.Equal(1, view.SelectedRecord.LanguageIndex);
            Assert.Equal("FRE", view.SelectedRecord.RawLanguageCode);
            Assert.Equal(frenchCandidate.Candidate.Identity, view.SelectedRecord.Identity);
            Pump(WaitAsync(() => view.CurrentPointerCandidates.Count == 1 &&
                view.CurrentPointerCandidates[0].Identity == frenchCandidate.Candidate.Identity));
            var drain = typeof(StringsView).GetMethod("DrainRelatedAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Pump((Task)drain.Invoke(view, null)!);
            Assert.Empty(view.CurrentPointerCandidates);

            Pump(view.SelectRecordAsync(otherTextIdentity));
            Pump(WaitAsync(() => view.CurrentPointerCandidates.Count == 0));
            Assert.Empty(view.CurrentPointerCandidates);
        });
    }

    private static byte[][] TextAndPointers(string key, int pointerCount) =>
    [TextRecord(key, "Stored line"), PointerRecords(key, pointerCount)];

    private static byte[] TextRecord(string key, string text)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1);
        var keyBytes = Encoding.ASCII.GetBytes(key);
        WriteUInt32(stream, (uint)keyBytes.Length); stream.Write(keyBytes);
        var textBytes = Encoding.Unicode.GetBytes(text);
        WriteUInt32(stream, (uint)textBytes.Length); stream.Write(textBytes);
        return stream.ToArray();
    }

    private static byte[] PointerRecords(string key, int count)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1); WriteUInt32(stream, (uint)count);
        var keyBytes = Encoding.ASCII.GetBytes(key);
        for (var index = 0; index < count; index++)
        {
            WriteUInt32(stream, (uint)keyBytes.Length); stream.Write(keyBytes);
            stream.WriteByte(1); stream.Write(new byte[9]);
        }
        return stream.ToArray();
    }

    private static byte[] LocalizationResource(params (string Language, byte[][] Records)[] languages)
    {
        using var body = new MemoryStream();
        foreach (var (language, records) in languages)
        {
            WriteUInt32(body, 0x301); body.WriteByte(0); body.Write(Encoding.ASCII.GetBytes(language));
            foreach (var record in records) body.Write(record);
        }
        using var payload = new MemoryStream();
        payload.Write("LOCALIZ_"u8); WriteUInt32(payload, 6); WriteUInt32(payload, (uint)body.Length + 4);
        WriteUInt32(payload, (uint)languages.Length); body.Position = 0; body.CopyTo(payload);
        return payload.ToArray();
    }

    private static byte[] Container(byte[] payload, int references)
    {
        using var stream = new MemoryStream();
        stream.Write("COM_CONT"u8); WriteUInt32(stream, 6); WriteUInt32(stream, (uint)(4 + references * 9));
        WriteUInt32(stream, (uint)references);
        for (var index = 0; index < references; index++)
        {
            WriteUInt32(stream, 1022); WriteUInt32(stream, (uint)(index + 1)); stream.WriteByte(1);
        }
        stream.Write(payload);
        return stream.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static int FindChoiceIndex(ComboBox choices,Func<string,bool> matches)
    {
        for(var index=0;index<choices.Items.Count;index++)
        {
            var label=choices.Items[index].GetType().GetProperty("Label")!.GetValue(choices.Items[index]) as string;
            if(label is not null&&matches(label))return index;
        }
        throw new InvalidOperationException("The expected language filter is unavailable.");
    }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
