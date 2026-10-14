using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Export.Audio;
using Deviant.Formats.Audio.Model;
using NAudio.Wave;
using Xunit;
using Xunit.Sdk;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AudioWindowPlaybackTests
{
    [Fact]
    public void IdlePlayerControlsStayDisabledUntilPlaybackStarts()
    {
        using var fixture = new GeneratedArchive(1);
        RunSta(() =>
        {
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, new FakeBackendFactory(), new(null, null, null, null, null, null, null));
                Named<ListView>(window, "MediaList").SelectedItem = Assert.Single(window.CurrentRows);
                Assert.False(Named<Button>(window, "PauseButton").IsEnabled);
                Assert.False(Named<Slider>(window, "SeekSlider").IsEnabled);
                Pump(window.StopPreviewAudioAsync());
                Assert.Equal(0, Named<Slider>(window, "SeekSlider").Value);
            }
            finally { if (window is not null) Pump(WindowTestLifetime.CloseAsync(window)); }
        });
    }

    [Fact]
    public void SelectedAudioDetailsAndPlayStayAvailableWhenQueriesMoveTheRowOffPage()
    {
        using var fixture = new GeneratedArchive(120);
        using var replacement = new GeneratedArchive(1);
        RunSta(() =>
        {
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, new FakeBackendFactory(),
                    new(null, null, null, null, null, null, null), expectedVisibleRows: 100);
                var list = Named<ListView>(window, "MediaList");
                var selected = window.CurrentRows[0];
                list.SelectedItem = selected;

                var queryField = typeof(AudioWindow).GetField("currentQuery", BindingFlags.Instance | BindingFlags.NonPublic)!;
                queryField.SetValue(window, ((AudioExplorerQuery)queryField.GetValue(window)!) with { Offset = 100 });
                Pump((Task)Invoke(window, "QueryAsync", false)!);

                Assert.Equal(100, window.CurrentRows[0].Identity.Ordinal);
                Assert.Null(list.SelectedItem);
                Assert.Equal(selected.Label, Named<TextBlock>(window, "DetailName").Text);
                Assert.True(Named<Button>(window, "PlayButton").IsEnabled);

                Named<TextBox>(window, "SearchBox").Text = "Generated dialogue 120";
                Pump((Task)Invoke(window, "QueryAsync", true)!);
                Assert.Single(window.CurrentRows);
                Assert.NotEqual(selected.Identity, window.CurrentRows[0].Identity);
                Assert.Equal(selected.Label, Named<TextBlock>(window, "DetailName").Text);
                Assert.True(Named<Button>(window, "PlayButton").IsEnabled);
                var selectedKeys = (HashSet<string>)typeof(AudioWindow).GetField("selectedIdentities",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Contains(selected.Identity.Key, selectedKeys);

                Named<TextBox>(window, "SearchBox").Clear();
                Pump((Task)Invoke(window, "QueryAsync", true)!);
                var currentSelected = Assert.Single(window.CurrentRows, row => row.Identity == selected.Identity);
                list.SelectedItem = currentSelected;
                list.SelectedItem = null;
                Assert.Empty(selectedKeys);
                Assert.Equal("Select a clip", Named<TextBlock>(window, "DetailName").Text);
                Assert.False(Named<Button>(window, "PlayButton").IsEnabled);

                list.SelectedItem = currentSelected;
                Assert.Equal(selected.Label, Named<TextBlock>(window, "DetailName").Text);
                Pump(window.OpenArchiveAsync(replacement.Index));
                Pump(window.CurrentSession!.EnrichmentTask);
                Assert.Empty(selectedKeys);
                Assert.Equal("Select a clip", Named<TextBlock>(window, "DetailName").Text);
            }
            finally { if (window is not null) Pump(WindowTestLifetime.CloseAsync(window)); }
        });
    }

    [Fact]
    public void SelectingDoesNotPlayAndExplicitPlayDecodesIntoTheInjectedPlayer()
    {
        using var fixture = new GeneratedArchive(2);
        var tools = RequireVgmstreamTools();
        RunSta(() =>
        {
            var factory = new FakeBackendFactory();
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, factory, tools);
                var row = window.CurrentRows[0];
                var mediaList = Named<ListView>(window, "MediaList");
                mediaList.SelectedItem = row;
                Assert.Equal(0, factory.CreateCount);
                Assert.Equal(0, factory.PlayCount);

                Assert.True(Named<Button>(window, "PlayButton").IsEnabled);
                Named<Button>(window, "PlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var activeJob = ActiveJob(window);
                if (activeJob is not null) PumpBeforeDeadline(activeJob, window, factory, "Explicit play");

                Assert.True(window.PlayingContext?.Identity == row.Identity && factory.PlayCount == 1,
                    $"Explicit play did not reach the player. Status='{Status(window)}'; creates={factory.CreateCount}; plays={factory.PlayCount}.");
                var backend = Assert.Single(factory.CreatedBackends);
                Assert.True(File.Exists(backend.Path));
                var header = File.ReadAllBytes(backend.Path).AsSpan(0, 4);
                Assert.True(header.SequenceEqual("RIFF"u8), "The player should receive the decoded WAV produced by the preview path.");
                Assert.Equal(48000, backend.SampleRate);
                Assert.Equal(2, backend.Channels);
                Assert.Equal(GeneratedArchive.SampleFrames, backend.SampleFrames);
                Assert.Equal((double)GeneratedArchive.SampleFrames / backend.SampleRate, backend.Length, 6);
                Assert.True(backend.HasNonzeroSamples, "The decoded generated PCM should contain the fixture's nonzero samples.");
                Assert.Equal(PlaybackState.Playing, window.PlaybackPlayer.State);
            }
            finally
            {
                if (window is not null) Pump(WindowTestLifetime.CloseAsync(window));
            }
        }, () => fixture.PreserveOnFailure = true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingPreviewCannotPublishAfterStopOrSourceReplacement(bool replaceSource)
    {
        using var fixture = new GeneratedArchive(1);
        using var replacement = replaceSource ? new GeneratedArchive(2) : null;
        var tools = RequireVgmstreamTools();
        RunSta(() =>
        {
            var factory = new FakeBackendFactory(blockFirstCreate: true);
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, factory, tools);
                var oldRow = Assert.Single(window.CurrentRows);
                var oldSession = window.CurrentSession!;
                var pending = window.StartPreviewAudioAsync(oldRow);
                AwaitFirstCreateOrPreviewCompletion(factory, pending, window);
                Assert.True(factory.FirstCreateIsBlocked);

                if (replaceSource)
                {
                    Pump(window.OpenArchiveAsync(replacement!.Index));
                    Pump(window.CurrentSession!.EnrichmentTask);
                    Assert.NotSame(oldSession, window.CurrentSession);
                    Assert.Equal(Path.GetFullPath(replacement.Index), window.CurrentSession.Snapshot.IndexPath);
                }
                else
                {
                    Pump(window.StopPreviewAudioAsync());
                }

                factory.ReleaseFirstCreate();
                Pump(pending);

                Assert.Null(window.PlayingContext);
                Assert.Equal(0, factory.PlayCount);
                Assert.All(factory.CreatedBackends, backend => Assert.True(backend.IsDisposed));
            }
            finally
            {
                factory.ReleaseFirstCreate();
                if (window is not null) Pump(WindowTestLifetime.CloseAsync(window));
            }
        }, () => { fixture.PreserveOnFailure = true; if (replacement is not null) replacement.PreserveOnFailure = true; });
    }

    [Fact]
    public void RapidExplicitPlayRequestsLeaveTheLatestSelectedRowPlaying()
    {
        using var fixture = new GeneratedArchive(2);
        var tools = RequireVgmstreamTools();
        RunSta(() =>
        {
            var factory = new FakeBackendFactory(blockFirstCreate: true);
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, factory, tools);
                var rows = window.CurrentRows;
                var oldRow = rows[0];
                var latestRow = rows[1];
                var list = Named<ListView>(window, "MediaList");
                list.SelectedItem = oldRow;
                var oldRequest = window.StartPreviewAudioAsync(oldRow);
                AwaitFirstCreateOrPreviewCompletion(factory, oldRequest, window);

                list.SelectedItem = latestRow;
                var latestRequest = window.StartPreviewAudioAsync(latestRow);
                Assert.False(latestRequest.IsCompleted, "A newer request should wait for the cancelled preview to drain before starting.");
                factory.ReleaseFirstCreate();
                Pump(Task.WhenAll(oldRequest, latestRequest));

                Assert.Equal(latestRow.Identity, window.PlayingContext?.Identity);
                var played = Assert.Single(factory.CreatedBackends, backend => backend.PlayCount == 1);
                Assert.NotEqual(factory.FirstCreatePath, played.Path);
                Assert.Equal(PlaybackState.Playing, window.PlaybackPlayer.State);
            }
            finally
            {
                factory.ReleaseFirstCreate();
                if (window is not null) Pump(WindowTestLifetime.CloseAsync(window));
            }
        }, () => fixture.PreserveOnFailure = true);
    }

    [Fact]
    public void FailedOutputRestoreClearsTheActivePlaybackContext()
    {
        using var fixture = new GeneratedArchive(1);
        var tools = RequireVgmstreamTools();
        RunSta(() =>
        {
            var factory = new FakeBackendFactory(failDeviceId: "synthetic-unavailable");
            AudioWindow? window = null;
            try
            {
                window = OpenAudioWindow(fixture, factory, tools);
                var row = Assert.Single(window.CurrentRows);
                Pump(window.StartPreviewAudioAsync(row));
                Assert.Equal(row.Identity, window.PlayingContext?.Identity);

                var deviceBox = Named<ComboBox>(window, "DeviceBox");
                deviceBox.SelectedItem = Assert.Single(deviceBox.Items.Cast<AudioOutputDevice>(), device => device.Id == "synthetic-unavailable");
                PumpBeforeDeadline(WaitUntilAsync(() => window.PlayingContext is null &&
                    Status(window).Contains("Synthetic output restore failure", StringComparison.Ordinal)),
                    window, factory, "Output-device restore");

                Assert.Null(window.PlayingContext);
                Assert.Equal(PlaybackState.Stopped, window.PlaybackPlayer.State);
                Assert.All(factory.CreatedBackends, backend => Assert.True(backend.IsDisposed));
            }
            finally
            {
                if (window is not null) Pump(WindowTestLifetime.CloseAsync(window));
            }
        }, () => fixture.PreserveOnFailure = true);
    }

    [Fact]
    public async Task ExportReviewRejectsPendingRowsThenExportsAnEnrichedGeneratedRow()
    {
        using var fixture = new GeneratedArchive(1);
        var cache = new CacheManager(Path.Combine(fixture.Root, "settings"), new(Path.Combine(fixture.Root, "cache")));
        var aliases = new AliasStore(Path.Combine(fixture.Root, "library"));
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.Index, cache, aliases,
            tools, null, CancellationToken.None, startEnrichment: false);

        var identity = new ResourceIdentity(session.Snapshot.IndexContentId, 0);
        var pendingRow = PendingRow(identity, session.Snapshot.PhysicalSourceStamp);
        var pendingError = Assert.Throws<ArchiveException>(() => session.Review([pendingRow]));
        Assert.Equal("ClassificationPending", pendingError.Code);

        await session.EnrichmentTask;
        var page = await session.QueryAsync(new AudioExplorerQuery(Limit: 10));
        var row = Assert.Single(page.Rows);
        var review = session.Review(page.Rows);
        var outputDirectory = Path.Combine(fixture.Root, "exports");
        var report = await session.ExportAsync(review,
            new AudioExportOptions(outputDirectory, AudioExportFormat.Original));

        var entry = Assert.Single(report.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Completed, entry.Status);
        var exportedBytes = await File.ReadAllBytesAsync(Path.Combine(outputDirectory, entry.RelativeOutput));
        Assert.Equal(fixture.ExpectedWems[0], exportedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(exportedBytes)).ToLowerInvariant(), entry.Sha256);
        Assert.Equal(row.Identity.Key, entry.Identity);
    }

    private static AudioWindow OpenAudioWindow(GeneratedArchive fixture, FakeBackendFactory factory, AudioToolPaths tools,
        int? expectedVisibleRows = null)
    {
        var window = new AudioWindow(
            new CacheManager(Path.Combine(fixture.Root, "settings"), new(Path.Combine(fixture.Root, "cache"))),
            new AliasStore(Path.Combine(fixture.Root, "library")), tools, new Player(factory), new FakeDeviceProvider())
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        WindowTestLifetime.Observe(window);
        window.Show();
        Pump(window.DevicesReady);
        Invoke(window, "ShowBrowse", "audio");
        Pump(window.OpenArchiveAsync(fixture.Index));
        Pump(window.CurrentSession!.EnrichmentTask);
        Pump((Task)Invoke(window, "QueryAsync", true)!);
        Pump(WaitUntilAsync(() => window.CurrentRows.Count == (expectedVisibleRows ?? fixture.Count)));
        return window;
    }

    private static AudioExplorerRow PendingRow(ResourceIdentity identity, string revision)
    {
        var descriptor = new AssetDescriptor(identity, new SourceRevision(identity.IndexContentId, revision),
            "audio.media", "Generated pending row", null, AssetAvailability.Available,
            AssetCompleteness.Unverified, AssetCapabilities.Original, [], []);
        return new AudioExplorerRow(descriptor, new MediaEntry { Identity = identity }, null);
    }

    private static AudioToolPaths RequireVgmstreamTools()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(AudioWindow).Assembly.Location),
            Environment.CurrentDirectory
        };
        var directories = roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => Ancestors(root!, maximumDepth: 8))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = directories.Select(root => AudioToolPaths.Discover(applicationDirectory: root)).ToList();
        foreach (var root in directories)
        {
            var artifacts = Path.Combine(root, "artifacts");
            if (!Directory.Exists(artifacts)) continue;
            foreach (var package in Directory.EnumerateDirectories(artifacts).Take(32))
            {
                var toolsDirectory = Path.Combine(package, "tools");
                if (File.Exists(Path.Combine(toolsDirectory, "vgmstream", "vgmstream-cli.exe")) ||
                    File.Exists(Path.Combine(toolsDirectory, "vgmstream", "vgmstream-cli")))
                    candidates.Add(AudioToolPaths.Discover(configuredDirectory: toolsDirectory, applicationDirectory: package));
            }
        }
        var tools = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.VgmstreamCli));
        if (tools is null) throw SkipException.ForSkip("The generated playback integration requires a bundled vgmstream-cli tool.");
        return tools;
    }

    private static IEnumerable<string> Ancestors(string path, int maximumDepth)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        for (var depth = 0; current is not null && depth < maximumDepth; depth++, current = current.Parent!)
            yield return current.FullName;
    }

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static Task WaitUntilAsync(Func<bool> predicate)
    {
        return WaitUntilCoreAsync(predicate);
        static async Task WaitUntilCoreAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!ready()) await Task.Delay(10, timeout.Token);
        }
    }

    private static Task? ActiveJob(AudioWindow window) =>
        (Task?)typeof(AudioWindow).GetField("activeJob", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);

    private static string Status(AudioWindow window) => Named<TextBlock>(window, "StatusText").Text;

    private static void AwaitFirstCreateOrPreviewCompletion(FakeBackendFactory factory, Task previewTask, AudioWindow window)
    {
        var winnerTask = Task.WhenAny(factory.FirstCreateEntered, previewTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Pump(winnerTask);
        if (!ReferenceEquals(winnerTask.GetAwaiter().GetResult(), factory.FirstCreateEntered))
        {
            var result = previewTask.IsFaulted ? previewTask.Exception?.GetBaseException().Message : "completed before player creation";
            throw new XunitException($"Preview did not reach the fake backend. Status='{Status(window)}'; creates={factory.CreateCount}; preview={result ?? "still pending"}.");
        }
    }

    private static void PumpBeforeDeadline(Task task, AudioWindow window, FakeBackendFactory factory, string operation)
    {
        var winnerTask = Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25)));
        Pump(winnerTask);
        if (!ReferenceEquals(winnerTask.GetAwaiter().GetResult(), task))
            throw new XunitException($"{operation} did not finish. Status='{Status(window)}'; creates={factory.CreateCount}; plays={factory.PlayCount}.");
        try { Pump(task); }
        catch (Exception error)
        {
            throw new XunitException($"{operation} failed. Status='{Status(window)}'; creates={factory.CreateCount}; plays={factory.PlayCount}; error={error.Message}");
        }
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action, Action? onFailure = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The generated playback check timed out.");
        if (failure is not null)
        {
            onFailure?.Invoke();
            throw new InvalidOperationException("The generated playback check failed.", failure);
        }
    }

    private sealed class GeneratedArchive : IDisposable
    {
        public const int SampleFrames = 2048;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "deviant-playback-" + Guid.NewGuid().ToString("N"));
        public string Index { get; }
        public int Count { get; }
        public IReadOnlyList<byte[]> ExpectedWems { get; }
        public bool PreserveOnFailure { get; set; }

        public GeneratedArchive(int count)
        {
            Count = count;
            Index = Path.Combine(Root, "source", "generated.idx");
            ExpectedWems = Array.AsReadOnly(Enumerable.Range(0, count)
                .Select(index => CreatePcmWem(SampleFrames, (short)(index + 1))).ToArray());
            var entries = ExpectedWems.Select((wem, index) => (1033u, WrapDialogue(index + 1, wem))).ToArray();
            DiscoveryFixture.WriteIndex(Index, entries);
        }

        private static byte[] WrapDialogue(int ordinal, byte[] wem)
        {
            var name = Encoding.UTF8.GetBytes($"Generated dialogue {ordinal}_ENG");
            using var stream = new MemoryStream();
            stream.Write(new byte[49]);
            "QZIP\0CSNDDATA"u8.CopyTo(stream.GetBuffer());
            stream.Position = 49;
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((uint)name.Length);
                writer.Write(name);
                writer.Write((uint)wem.Length);
                writer.Write(wem);
                writer.Write(new byte[40]);
            }
            var result = stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(17), (uint)(result.Length - 21));
            return result;
        }

        private static byte[] CreatePcmWem(int sampleFrames, short amplitude)
        {
            var sampleData = new byte[checked(sampleFrames * 4)];
            for (var frame = 0; frame < sampleFrames; frame++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(sampleData.AsSpan(frame * 4, 2), amplitude);
                BinaryPrimitives.WriteInt16LittleEndian(sampleData.AsSpan(frame * 4 + 2, 2), amplitude);
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write("RIFF"u8);
                writer.Write(0u);
                writer.Write("WAVEfmt "u8);
                writer.Write(16u);
                writer.Write((ushort)1);
                writer.Write((ushort)2);
                writer.Write(48000u);
                writer.Write(192000u);
                writer.Write((ushort)4);
                writer.Write((ushort)16);
                writer.Write("data"u8);
                writer.Write((uint)sampleData.Length);
                writer.Write(sampleData);
            }
            var result = stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
            return result;
        }

        public void Dispose()
        {
            if (PreserveOnFailure) return;
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeDeviceProvider : IAudioOutputDeviceProvider
    {
        public Task<IReadOnlyList<AudioOutputDevice>> EnumerateAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<AudioOutputDevice>>(
                [new("", "Default output"), new("synthetic-good", "Generated output"), new("synthetic-unavailable", "Unavailable output")]);
    }

    private sealed class FakeBackendFactory(bool blockFirstCreate = false, string? failDeviceId = null) : IPlaybackBackendFactory
    {
        private readonly ConcurrentQueue<FakeBackend> backends = new();
        private readonly ManualResetEventSlim firstCreateRelease = new(false);
        private readonly TaskCompletionSource firstCreateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int createCount;

        public int CreateCount => Volatile.Read(ref createCount);
        public int PlayCount => backends.Sum(backend => backend.PlayCount);
        public IReadOnlyList<FakeBackend> CreatedBackends => backends.ToArray();
        public bool FirstCreateIsBlocked => blockFirstCreate && !firstCreateRelease.IsSet;
        public string? FirstCreatePath { get; private set; }
        public Task FirstCreateEntered => firstCreateEntered.Task;

        public IPlaybackBackend Create(string path, float volume, double positionSeconds = 0) =>
            Create(path, volume, positionSeconds, null);

        public IPlaybackBackend Create(string path, float volume, double positionSeconds, string? outputDeviceId)
        {
            var ordinal = Interlocked.Increment(ref createCount);
            if (ordinal == 1)
            {
                FirstCreatePath = path;
                firstCreateEntered.TrySetResult();
                if (blockFirstCreate && !firstCreateRelease.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("The synthetic first output was not released.");
            }
            if (failDeviceId is not null && outputDeviceId == failDeviceId)
                throw new InvalidOperationException("Synthetic output restore failure.");
            var backend = new FakeBackend(path, volume, positionSeconds);
            backends.Enqueue(backend);
            return backend;
        }

        public void ReleaseFirstCreate() => firstCreateRelease.Set();
    }

    private sealed class FakeBackend : IPlaybackBackend
    {
        private int state;
        private int playCount;
        private int disposed;
        private readonly WaveFileReader reader;

        public FakeBackend(string path, float initialVolume, double initialPosition)
        {
            Path = path;
            Volume = initialVolume;
            reader = new WaveFileReader(path);
            SampleRate = reader.WaveFormat.SampleRate;
            Channels = reader.WaveFormat.Channels;

            var buffer = new byte[checked((int)reader.Length)];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = reader.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            SampleFrames = read / reader.WaveFormat.BlockAlign;
            HasNonzeroSamples = buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0;
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(initialPosition, 0, reader.TotalTime.TotalSeconds));
        }

        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public string Path { get; }
        public PlaybackState State => (PlaybackState)Volatile.Read(ref state);
        public double Position => reader.CurrentTime.TotalSeconds;
        public double Length => reader.TotalTime.TotalSeconds;
        public int Channels { get; }
        public int SampleRate { get; }
        public long SampleFrames { get; }
        public bool HasNonzeroSamples { get; }
        public float Volume { get; set; }
        public int PlayCount => Volatile.Read(ref playCount);
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public void Play()
        {
            Interlocked.Increment(ref playCount);
            Volatile.Write(ref state, (int)PlaybackState.Playing);
        }

        public void Pause() => Volatile.Write(ref state, (int)PlaybackState.Paused);
        public void Stop() => Volatile.Write(ref state, (int)PlaybackState.Stopped);
        public void Dispose()
        {
            Volatile.Write(ref disposed, 1);
            Stop();
            reader.Dispose();
        }
    }
}
