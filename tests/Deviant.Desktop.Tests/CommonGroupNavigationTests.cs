using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CommonGroupNavigationTests
{
    private const uint NativeType = 4241;
    private const uint ContainerId = 731;

    [Fact]
    public void MultipleFramesRequireAnExactChoiceAndBookmarkTheMemberIdentity() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload()), (42, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            var parent = new ResourceIdentity(snapshot.IndexContentId, container.Ordinal);

            Pump(Inspect(window, parent));
            Assert.Null(CommonGroupIdentity(window));

            var member = Member(snapshot, container, 1, 42);
            Pump(SelectFrame(window, member));
            Assert.Equal(member, CommonGroupIdentity(window));
            Assert.Equal(parent, window.BufferBrowser.CurrentInspection!.Identity);
            Assert.Equal("buffer", Field<string>(window, "currentView"));
            Assert.True(((Button)window.FindName("BookmarkButton")!).IsEnabled);

            ((Button)window.FindName("BookmarkButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var library = new LibraryStore(fixture.Library);
            WaitUntil(() => library.ReadAsync().GetAwaiter().GetResult().Bookmarks.Any(bookmark =>
                bookmark.View == "gmk" && bookmark.Identity == member));
            var bookmark = Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Bookmarks,
                value => value.View == "gmk" && value.Identity == member);
            Assert.Equal(new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), bookmark.Revision);
        });

    [Fact]
    public void DuplicateStoredNamesRemainIndexDistinctAndSwitchTheDisplayedItem() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            var parent = new ResourceIdentity(snapshot.IndexContentId, container.Ordinal);
            var member = Member(snapshot, container, 0, 41);

            Pump(Inspect(window, parent));
            Assert.Equal(member, CommonGroupIdentity(window));
            Assert.Equal("First generated item", Field<TextBox>(window, "commonGroupText").Text);
            Assert.Contains(window.BufferBrowser.CurrentInspection!.Value!.Sections,
                section => section.Bytes.Span.SequenceEqual("First generated item"u8));

            Pump(SelectItem(window, 1));

            Assert.Equal(member, CommonGroupIdentity(window));
            Assert.Equal("Second generated item", Field<TextBox>(window, "commonGroupText").Text);
            Assert.Equal(parent, window.BufferBrowser.CurrentInspection!.Identity);
            Assert.Contains(window.BufferBrowser.CurrentInspection!.Value!.Sections,
                section => section.Bytes.Span.SequenceEqual("Second generated item"u8));
            Assert.DoesNotContain(window.BufferBrowser.CurrentInspection!.Value!.Sections,
                section => section.Bytes.Span.SequenceEqual("First generated item"u8));
        });

    [Theory]
    [InlineData("cancelled")]
    [InlineData("invalid")]
    public void RejectedItemSelectionPreservesTheCurrentInspectionAndLease(string attempt) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            var member = Member(snapshot, container, 0, 41);
            Pump(Inspect(window, new ResourceIdentity(snapshot.IndexContentId, container.Ordinal)));

            var inspection = window.BufferBrowser.CurrentInspection;
            var budget = Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget);
            var retainedBytes = budget.UsedBytes;
            if (attempt == "cancelled")
            {
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                var error = Assert.Throws<TargetInvocationException>(() => { _ = SelectItem(window, 1, canceled.Token); });
                Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            }
            else
            {
                var error = Assert.Throws<TargetInvocationException>(() => { _ = SelectItem(window, 2); });
                Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
            }

            Assert.Equal(member, CommonGroupIdentity(window));
            Assert.Same(inspection, window.BufferBrowser.CurrentInspection);
            Assert.Equal("First generated item", Field<TextBox>(window, "commonGroupText").Text);
            Assert.Equal(retainedBytes, budget.UsedBytes);
        });

    [Fact]
    public void OpeningAnotherBufferAfterAGroupReleasesTheGroupLeaseAndBudget() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var entries = new List<(uint Type, byte[] Bytes)> { (29u, Container((41, GroupPayload()))) };
            for (var index = 0; index < 22; index++) entries.Add((2229u, [(byte)index, 0x5a, 0xa5]));
            DiscoveryFixture.WriteIndex(fixture.Index, entries);
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            Pump(Inspect(window, new ResourceIdentity(snapshot.IndexContentId, container.Ordinal)));
            Assert.NotNull(CommonGroupIdentity(window));
            Assert.True(Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes > 0);

            var buffer = Assert.Single(snapshot.Records.Where(row => row.Type == 2229).Take(1));
            Pump(window.InspectTextureBufferAsync(new(snapshot.IndexContentId, buffer.Ordinal)));

            Assert.Null(CommonGroupIdentity(window));
            Assert.Null(window.BufferBrowser.CurrentInspection);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
        });

    [Fact]
    public void MalformedGroupDoesNotRetainItsInspectionLeaseAndClearReturnsBudgetToZero() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, MalformedGroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);

            Pump(Inspect(window, new ResourceIdentity(snapshot.IndexContentId, container.Ordinal)));

            Assert.Null(Field<object?>(window, "commonGroupLease"));
            Assert.Null(CommonGroupIdentity(window));
            Invoke(window, "ShowBrowse", "audio");
            Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
        });

    [Fact]
    public void MissingExactMemberDoesNotRetainTheContainerLeaseOrChoiceReservation() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            var missingMember = new ResourceIdentity(snapshot.IndexContentId, container.Ordinal,
                $"dc-object/8/{NativeType}/99");

            Assert.Throws<InvalidOperationException>(() => Pump(Inspect(window, missingMember)));

            var budget = Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget);
            Assert.Null(CommonGroupIdentity(window));
            Assert.Equal(0, budget.UsedBytes);
            Invoke(window, "ShowLibrary", "bookmarks", (Guid?)null);
            Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
            Assert.Null(CommonGroupIdentity(window));
            Assert.Equal(0, budget.UsedBytes);
        });

    [Fact]
    public void LeavingWhileAGroupReadIsPendingDrainsItAndRejectsTheLateResult() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var gate = new DelayedRead();
            typeof(AudioWindow).GetProperty("CommonGroupNavigationGateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, (Func<string, CancellationToken, Task>)gate.Invoke);

            var opening = Inspect(window, new ResourceIdentity(snapshot.IndexContentId, container.Ordinal));
            Pump(gate.Entered.Task);
            Invoke(window, "ShowBrowse", "audio");
            Assert.False(opening.IsCompleted);
            Assert.Null(CommonGroupIdentity(window));
            var drain = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            Assert.False(drain.IsCompleted);

            gate.Release();
            Pump(Task.WhenAll(IgnoreCancellation(opening), drain));
            Assert.Null(CommonGroupIdentity(window));
            Assert.Null(window.BufferBrowser.CurrentInspection);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
        });

    [Fact]
    public void BufferCancelCancelsPendingGroupReadAndDrainRejectsTheLateResult() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WriteArchive(fixture.Index, Container((41, GroupPayload())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var gate = new DelayedRead();
            typeof(AudioWindow).GetProperty("CommonGroupNavigationGateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, (Func<string, CancellationToken, Task>)gate.Invoke);

            var opening = Inspect(window, new ResourceIdentity(snapshot.IndexContentId, container.Ordinal));
            Pump(gate.Entered.Task);
            var readCancellation = Field<CancellationTokenSource>(window, "bufferReadCancellation");
            window.BufferBrowser.CancelActivity();
            Assert.True(readCancellation.IsCancellationRequested);
            var drain = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            Assert.False(drain.IsCompleted);

            gate.Release();
            Pump(Task.WhenAll(IgnoreCancellation(opening), drain));
            Assert.Null(CommonGroupIdentity(window));
            Assert.Null(window.BufferBrowser.CurrentInspection);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
        });

    private static Task Inspect(AudioWindow window, ResourceIdentity identity) =>
        (Task)RequiredMethod("InspectCommonGroupsAsync").Invoke(window, [identity, CancellationToken.None])!;

    private static Task SelectFrame(AudioWindow window, ResourceIdentity identity) =>
        (Task)RequiredMethod("SelectCommonGroupFrameAsync").Invoke(window, [identity, CancellationToken.None])!;

    private static Task SelectItem(AudioWindow window, int index, CancellationToken token = default) =>
        (Task)RequiredMethod("SelectCommonGroupItemAsync").Invoke(window, [index, token])!;

    private static MethodInfo RequiredMethod(string name) =>
        typeof(AudioWindow).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new Xunit.Sdk.XunitException($"AudioWindow.{name} is required for Common-group navigation.");

    private static ResourceIdentity? CommonGroupIdentity(AudioWindow window) =>
        (ResourceIdentity?)typeof(AudioWindow).GetProperty("CommonGroupSelectedIdentity", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(window);

    private static ResourceIdentity Member(ArchiveSnapshot snapshot, ResourceRecord row, int directoryIndex, uint id) =>
        new(snapshot.IndexContentId, row.Ordinal, $"dc-object/{directoryIndex}/{NativeType}/{id}");

    private static void WriteArchive(string path, params byte[][] containers) =>
        DiscoveryFixture.WriteIndex(path, containers.Select(bytes => (29u, bytes)).ToArray());

    private static byte[] Container(params (uint Id, byte[] Payload)[] objects)
    {
        using var bytes = new MemoryStream();
        bytes.Write("QZIP\0"u8);
        bytes.Write("DC_INFO "u8);
        WriteUInt32(bytes, 3);
        WriteUInt32(bytes, checked((uint)(8 + objects.Length * 8)));
        WriteUInt32(bytes, 0);
        WriteUInt32(bytes, checked((uint)objects.Length));
        foreach (var item in objects) { WriteUInt32(bytes, NativeType); WriteUInt32(bytes, item.Id); }

        using var body = new MemoryStream();
        foreach (var item in objects)
        {
            WriteUInt32(body, checked((uint)item.Payload.Length));
            body.WriteByte(0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            body.Write(item.Payload);
        }
        bytes.Write("DC_DATA "u8);
        WriteUInt32(bytes, 23);
        WriteUInt32(bytes, checked((uint)body.Length));
        body.Position = 0;
        body.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static byte[] GroupPayload()
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1);
        stream.Write("GMK_COMG"u8);
        WriteUInt32(stream, 22);
        WriteUInt32(stream, 3);
        WriteUInt32(stream, 2);
        WriteItem(stream, "Duplicate", "First generated item", [11, 22], 0, null, 101);
        WriteItem(stream, "Duplicate", "Second generated item", [33], 1, (uint.MaxValue, 7), 202);
        return stream.ToArray();
    }

    private static byte[] MalformedGroupPayload()
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, 1);
        stream.Write("GMK_COMG"u8);
        WriteUInt32(stream, 22);
        WriteUInt32(stream, 3);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 8);
        stream.Write("Short"u8);
        return stream.ToArray();
    }

    private static void WriteItem(Stream stream, string name, string text, uint[] words, byte flag,
        (uint Word0, uint Word1)? pair, uint finalWord)
    {
        WriteText(stream, name);
        WriteText(stream, text);
        WriteUInt32(stream, checked((uint)words.Length));
        foreach (var word in words) WriteUInt32(stream, word);
        stream.WriteByte(flag);
        if (pair is { } reference) { WriteUInt32(stream, reference.Word0); WriteUInt32(stream, reference.Word1); }
        WriteUInt32(stream, finalWord);
    }

    private static void WriteText(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments);

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The Common-group navigation state did not settle.");
            Pump(Task.Delay(10));
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class DelayedRead : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int used;
        public TaskCompletionSource Entered => entered;
        public void Release() => release.TrySetResult();
        public Task Invoke(string _, CancellationToken __)
        {
            if (Interlocked.Exchange(ref used, 1) != 0) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        }
        public void Dispose() => Release();
    }
}

