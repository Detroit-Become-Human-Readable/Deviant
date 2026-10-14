using System.Buffers.Binary;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class PhysicsNavigationTests
{
    private const uint PhysicsNativeId = 2201;

    [Fact]
    public void RapidContainerAndMemberSelectionsKeepLatestResultAndReleaseBudget() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WritePhysicsArchive(fixture.Index,
                Container((37, EmbeddedPhysics()), (38, EmbeddedPhysics())),
                Container((51, EmbeddedPhysics())),
                Container((63, EmbeddedPhysics())));
            Pump(window.OpenArchiveAsync(fixture.Index));

            var snapshot = window.CurrentSession!.Snapshot;
            var containers = snapshot.Records.Where(row => row.Type == 29).ToArray();
            var staleMember = Member(snapshot, containers[0], 0, 37);
            var middleMember = Member(snapshot, containers[1], 0, 51);
            var newestMember = Member(snapshot, containers[2], 0, 63);
            using var delayedContainer = new DelayedRead("container");
            SetGate(window, delayedContainer);

            var staleOpen = window.InspectPhysicsAsync(staleMember);
            Pump(delayedContainer.Entered.Task);
            var middleOpen = window.InspectPhysicsAsync(middleMember);
            var newestOpen = window.InspectPhysicsAsync(newestMember);
            Assert.False(middleOpen.IsCompleted);
            Assert.False(newestOpen.IsCompleted);
            delayedContainer.Release();
            Pump(Task.WhenAll(IgnoreCancellation(staleOpen), middleOpen, newestOpen));
            Assert.Equal(newestMember, window.PhysicsSelectedIdentity);
            var latestItems = NamedList(window.PhysicsBrowser, "Items");
            Assert.NotNull(latestItems.ItemsSource);
            Assert.NotEmpty(latestItems.Items);
            Assert.True(Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes > 0);

            using var delayedMember = new DelayedRead("member");
            SetGate(window, delayedMember);
            var containerIdentity = new ResourceIdentity(snapshot.IndexContentId, containers[0].Ordinal);
            Pump(window.InspectPhysicsAsync(containerIdentity));
            var frames = Field<ComboBox>(window, "physicsFrames");
            Assert.Equal(2, frames.Items.Count);
            frames.SelectedIndex = 0;
            Pump(delayedMember.Entered.Task);
            frames.SelectedIndex = 1;
            WaitUntil(() => window.PhysicsSelectedIdentity == Member(snapshot, containers[0], 1, 38));
            var selected = Member(snapshot, containers[0], 1, 38);
            delayedMember.Release();
            WaitUntil(() => ReadSetCount(window, "physicsMemberReads") == 0);
            Assert.Equal(selected, window.PhysicsSelectedIdentity);

            Pump((Task)Invoke(window, "ClearPhysicsAsync", false)!);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
            Assert.Null(window.PhysicsSelectedIdentity);
        });

    [Theory]
    [InlineData("home")]
    [InlineData("audio")]
    public void HomeOrHidingPhysicsWhileReadIsPendingRejectsLateResult(string destination) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WritePhysicsArchive(fixture.Index, Container((37, EmbeddedPhysics())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var gate = new DelayedRead("container");
            SetGate(window, gate);

            var opening = window.InspectPhysicsAsync(Member(snapshot, container, 0, 37));
            Pump(gate.Entered.Task);
            if (destination == "home")
                Invoke(window, "Home_Click", window, new RoutedEventArgs());
            else
                Invoke(window, "ShowBrowse", "audio");

            Assert.False(opening.IsCompleted);
            Assert.Null(window.PhysicsSelectedIdentity);
            gate.Release();
            Pump(IgnoreCancellation(opening));
            WaitUntil(() => ReadSetCount(window, "physicsContainerReads") == 0);

            Assert.Null(window.PhysicsSelectedIdentity);
            Assert.Null(NamedList(window.PhysicsBrowser, "Items").ItemsSource);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
            Assert.Equal(destination == "home" ? Visibility.Visible : Visibility.Collapsed,
                ((FrameworkElement)window.FindName("HomeHost")).Visibility);
        });

    [Fact]
    public void WorkspaceAndCacheDrainsWaitForPhysicsReadsAndReleaseBudget() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WritePhysicsArchive(fixture.Index, Container((37, EmbeddedPhysics())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var firstSnapshot = window.CurrentSession!.Snapshot;
            var firstContainer = Assert.Single(firstSnapshot.Records, row => row.Type == 29);
            using var gate = new DelayedRead("container");
            SetGate(window, gate);
            var opening = window.InspectPhysicsAsync(Member(firstSnapshot, firstContainer, 0, 37));
            Pump(gate.Entered.Task);

            var replacementPath = Path.Combine(fixture.Root, "replacement", "archive.idx");
            WritePhysicsArchive(replacementPath, Container((63, EmbeddedPhysics())));
            var replacement = window.OpenArchiveAsync(replacementPath);
            WaitUntil(() => Field<int>(window, "physicsWorkspaceDrainDepth") > 0);
            Assert.False(replacement.IsCompleted);
            gate.Release();
            Pump(Task.WhenAll(IgnoreCancellation(opening), replacement));

            Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            Assert.Null(window.PhysicsSelectedIdentity);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);

            var secondSnapshot = window.CurrentSession.Snapshot;
            var secondContainer = Assert.Single(secondSnapshot.Records, row => row.Type == 29);
            using var cacheGate = new DelayedRead("container");
            SetGate(window, cacheGate);
            var cachePending = window.InspectPhysicsAsync(Member(secondSnapshot, secondContainer, 0, 63));
            Pump(cacheGate.Entered.Task);
            var cacheDrain = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            WaitUntil(() => Field<int>(window, "physicsWorkspaceDrainDepth") > 0);
            Assert.False(cacheDrain.IsCompleted);
            cacheGate.Release();
            Pump(Task.WhenAll(IgnoreCancellation(cachePending), cacheDrain));

            Assert.Null(window.CurrentSession);
            Assert.Null(window.PhysicsSelectedIdentity);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
            Assert.Null(NamedList(window.PhysicsBrowser, "Items").ItemsSource);
        });

    [Fact]
    public void ClosingWaitsForPendingPhysicsReadAndReturnsBudgetToZero() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WritePhysicsArchive(fixture.Index, Container((37, EmbeddedPhysics())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            using var gate = new DelayedRead("container");
            SetGate(window, gate);
            var opening = window.InspectPhysicsAsync(Member(snapshot, container, 0, 37));
            Pump(gate.Entered.Task);

            window.Close();
            Assert.False(WindowTestLifetime.IsClosed(window));
            var closed = WindowTestLifetime.WaitClosedAsync(window);
            Assert.False(closed.IsCompleted);
            gate.Release();
            Pump(Task.WhenAll(IgnoreCancellation(opening), closed));

            Assert.Empty(Field<HashSet<Task<NativeContainerLease>>>(window, "physicsContainerReads"));
            Assert.Empty(Field<HashSet<Task<PhysicsInspectionLease>>>(window, "physicsMemberReads"));
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);
        });

    [Fact]
    public void PendingReadShowsBusyCancelReportsCompletionAndAudioQueryKeepsPhysicsStatus() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            WritePhysicsArchive(fixture.Index, Container((37, EmbeddedPhysics())));
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var container = Assert.Single(snapshot.Records, row => row.Type == 29);
            var member = Member(snapshot, container, 0, 37);
            using var gate = new DelayedRead("container");
            SetGate(window, gate);

            var opening = window.InspectPhysicsAsync(member);
            Pump(gate.Entered.Task);
            var processing = (ProcessingAnimation)window.FindName("WindowProcessing");
            Assert.True(processing.IsActive);
            Invoke(window, "Cancel_Click", window, new RoutedEventArgs());
            Assert.True(processing.IsActive);
            gate.Release();
            Pump(IgnoreCancellation(opening));
            WaitUntil(() => Field<TextBlock>(window, "physicsStatus").Text == "Cancelled" && !processing.IsActive);
            Assert.False(processing.IsActive);
            Assert.Equal(0, Assert.IsType<AssetMemoryBudget>(window.RenderMemoryBudget).UsedBytes);

            ClearGate(window);
            Pump(window.InspectPhysicsAsync(member));
            Assert.Equal("Ready", ((TextBlock)window.FindName("StatusText")).Text);
            Pump((Task)Invoke(window, "QueryAsync", true)!);
            Assert.Equal("Ready", ((TextBlock)window.FindName("StatusText")).Text);
        });

    private static void WritePhysicsArchive(string indexPath, params byte[][] containers) =>
        DiscoveryFixture.WriteIndex(indexPath, containers.Select((bytes, ordinal) => (29u, bytes)).ToArray());

    private static ResourceIdentity Member(ArchiveSnapshot snapshot, ResourceRecord container, int directoryIndex, uint id) =>
        new(snapshot.IndexContentId, container.Ordinal,
            $"dc-object/{directoryIndex}/{PhysicsNativeId}/{id}");

    private static byte[] Container(params (uint Id, byte[] Payload)[] objects)
    {
        using var bytes = new MemoryStream();
        bytes.Write("QZIP\0"u8);
        bytes.Write("DC_INFO "u8);
        WriteUInt32(bytes, 3);
        WriteUInt32(bytes, checked((uint)(8 + objects.Length * 8)));
        WriteUInt32(bytes, 0);
        WriteUInt32(bytes, checked((uint)objects.Length));
        foreach (var item in objects)
        {
            WriteUInt32(bytes, PhysicsNativeId);
            WriteUInt32(bytes, item.Id);
        }

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

    private static byte[] EmbeddedPhysics()
    {
        var embedded = CompleteEmbedded();
        var payload = new byte[20 + embedded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        "PHYSYS__"u8.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), checked((uint)embedded.Length));
        embedded.CopyTo(payload, 20);
        return payload;
    }

    private static byte[] CompleteEmbedded()
    {
        var type = Chunk("TYPE", 0, Join(
            Chunk("TSTR", 1, Encoding.ASCII.GetBytes("Root\0Child\0Leaf\0")),
            Chunk("TNA1", 1, [4, 0, 0, 1, 0, 2, 0]),
            Chunk("FSTR", 1, Encoding.ASCII.GetBytes("member\0")),
            Chunk("TBDY", 1, [1, 0, 0, 2, 0, 0, 3, 0, 0])));
        var index = Chunk("INDX", 0, Join(
            Chunk("ITEM", 1, Join(Row(0x2a000003, 17, 1), Row(0x1b000001, 12, uint.MaxValue),
                Row(0x20000002, 99, 0), Row(0x0f000005, 64, 1))),
            Chunk("PTCH", 1, [2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0])));
        return Chunk("TAG0", 0, Join(Chunk("DATA", 1, new byte[128]), type, index));
    }

    private static byte[] Chunk(string tag, byte flags, byte[] body)
    {
        var bytes = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)flags << 30) | (uint)bytes.Length);
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 4);
        body.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Row(uint word0, uint word1, uint word2)
    {
        var row = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(row, word0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), word1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), word2);
        return row;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void SetGate(AudioWindow window, DelayedRead gate)
    {
        Func<string, CancellationToken, Task> callback = gate.Invoke;
        typeof(AudioWindow).GetProperty("PhysicsNavigationGateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, callback);
    }

    private static void ClearGate(AudioWindow window) =>
        typeof(AudioWindow).GetProperty("PhysicsNavigationGateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, null);

    private static int ReadSetCount(AudioWindow window, string name) =>
        (int)typeof(AudioWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!.GetType().GetProperty("Count")!.GetValue(
                typeof(AudioWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window))!;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static ListView NamedList(PhysicsInspectionView view, string name) =>
        (ListView)view.FindName(name)!;

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
            if (DateTime.UtcNow >= until) throw new TimeoutException("The physics navigation state did not settle.");
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

    private sealed class DelayedRead(string stage) : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int used;
        public TaskCompletionSource Entered => entered;
        public void Release() => release.TrySetResult();
        // Deliberately ignores cancellation so the host must reject a result that arrives late.
        public Task Invoke(string requestedStage, CancellationToken _)
        {
            if (requestedStage != stage || Interlocked.Exchange(ref used, 1) != 0) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        }
        public void Dispose() => Release();
    }
}
