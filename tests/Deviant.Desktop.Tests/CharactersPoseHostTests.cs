using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Export.Models;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersPoseHostTests
{
    [Fact]
    public void CancelledPosePickerDoesNotReadPoseOrStartModelBackend()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            SelectCharacter(window, descriptor);
            SetPosePathPicker(window, () => null);
            window.CharactersBrowser.PoseImportProvider = PoseImportProvider(window);
            var budget = window.RenderMemoryBudget!;
            var baseline = budget.UsedBytes;
            var pickerCalls = 0;
            SetPosePathPicker(window, () => { pickerCalls++; return null; });

            Named<Button>(window.CharactersBrowser, "LoadPoseButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => PreviewTasks(window.CharactersBrowser).Count == 0);

            Assert.Equal(1, pickerCalls);
            Assert.Equal("", Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text);
            Assert.Null(PoseOwner(window));
            Assert.Null(window.CharactersBrowser.ExportProvider);
            Assert.Null(window.ModelSession);
            Assert.Null(window.ViewportService);
            Assert.Equal(baseline, budget.UsedBytes);
        });
    }

    [Fact]
    public void ValidPosePackageForAnotherMeshIsRejectedBeforeRendererAndReleasesFileAndBudget()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            SelectCharacter(window, descriptor);
            var binding = window.CharactersBrowser.ModelBinding!;
            var lod = window.CharactersBrowser.SelectedModelLod!;
            var path = Path.Combine(fixture.Root, "generated-pose-package.json");
            File.WriteAllText(path, JsonSerializer.Serialize(ValidPoseForOtherMesh(descriptor, binding, lod)));
            SetPosePathPicker(window, () => path);
            window.CharactersBrowser.PoseImportProvider = PoseImportProvider(window);
            var budget = window.RenderMemoryBudget!;
            var baseline = budget.UsedBytes;
            var validationTask = NativeModelPosePackageReader.ReadAsync(path, budget);
            Pump(validationTask);
            using (var validated = validationTask.GetAwaiter().GetResult())
            using (var consumer = validated.AcquireLease())
            {
                Assert.NotEqual(lod.MeshDataIdentity, consumer.Package.PoseSource.MeshIdentity);
                Assert.Equal(binding.SelectedIdentity, consumer.Package.PoseSource.RigIdentity);
                Assert.Equal(descriptor.Revision, consumer.Package.PoseSource.Revision);
            }
            Assert.Equal(baseline, budget.UsedBytes);

            Named<Button>(window.CharactersBrowser, "LoadPoseButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => PreviewTasks(window.CharactersBrowser).Count == 0);

            Assert.Contains("another model or detail level",
                Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text, StringComparison.Ordinal);
            Assert.Null(PoseOwner(window));
            Assert.Null(window.CharactersBrowser.ExportProvider);
            Assert.Null(window.ModelSession);
            Assert.Null(window.ViewportService);
            Assert.Equal(baseline, budget.UsedBytes);
            using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        });
    }

    private static AssetDescriptor OpenCharacters(AudioWindow window, DiscoveryFixture fixture)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var snapshot = window.CurrentSession!.Snapshot;
        var descriptor = new AssetDescriptor(new(snapshot.IndexContentId, 0, "generated/model-entry"),
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), "character-model", "Generated model",
            "generated-group", AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.Preview, [new NameEvidence("Generated model", NameOrigin.Stored, "generated descriptor")], []);
        window.CharacterCatalogProvider = (source, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(snapshot, source);
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
        };
        Pump(window.ShowCharactersAsync());
        return descriptor;
    }

    private static void ConfigureBindingProvider(AudioWindow window) =>
        window.CharacterBindingsProvider = (selected, budget, token) =>
        {
            token.ThrowIfCancellationRequested();
            var retention = budget.Reserve(512, "generated binding metadata");
            return Task.FromResult<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>(
                ([Binding(selected)], retention));
        };

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor)
    {
        var owner = descriptor.Identity with { MemberPath = "generated/container" };
        var catalog = descriptor.Identity with { MemberPath = "generated/catalog" };
        var mesh = new ResourceIdentity(descriptor.Identity.IndexContentId, descriptor.Identity.Ordinal + 1,
            "generated/mesh");
        var lod = new CharacterMeshLodBinding(0, 0.5f,
            new(IdentifierNamespace.Native, "generated-mesh", owner),
            new(ReferenceStatus.Unique, [mesh]), mesh, 0, 1, null,
            new NativeModelSelection(mesh, 0), new(owner, 0, 16), null, null, null);
        return new(descriptor.Identity, catalog, owner, descriptor.Revision, 2013, 0, null, 0,
            null, null, new(ReferenceStatus.Unique, [owner]), [], [], [lod], null, null);
    }

    private static Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding,
        CancellationToken, Task> PoseImportProvider(AudioWindow window) => (descriptor, binding, lod, token) =>
        (Task)Invoke(window, "ImportProductionCharacterPoseAsync", descriptor, binding, lod, token)!;

    private static NativeModelPosePackage ValidPoseForOtherMesh(AssetDescriptor descriptor,
        CharacterModelBindingResult binding, CharacterMeshLodBinding detail)
    {
        byte[] rows;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var value in new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }) writer.Write(value);
            rows = stream.ToArray();
        }
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        var bind = new FbxBindMatrix(identity.Select(value => (double)value).ToArray());
        var selectedMesh = detail.MeshDataIdentity ?? throw new InvalidOperationException("Generated binding has no mesh identity.");
        var otherMesh = selectedMesh with
        {
            Ordinal = selectedMesh.Ordinal + 42,
            MemberPath = "generated/other-mesh"
        };
        var source = new NativeSkinningPoseSource(otherMesh, binding.SelectedIdentity,
            descriptor.Revision, checked((int)detail.GroupIndex!.Value), 1, new string('a', 64),
            Convert.ToHexString(SHA256.HashData(rows)).ToLowerInvariant(),
            new(binding.SelectedIdentity, 0, rows.Length));
        var joint = new FbxJointExportInput("generated-joint", null, null, bind,
            new(identity, MatrixLayout.RowMajor, VectorMultiplicationSide.Row));
        return new(1, source, detail.LodOrdinal, rows, [joint], bind, new Dictionary<uint, int> { [0] = 0 });
    }

    private static void SelectCharacter(AudioWindow window, AssetDescriptor descriptor)
    {
        ConfigureBindingProvider(window);
        Named<ListView>(window.CharactersBrowser, "Entries").SelectedIndex = 0;
        WaitUntil(() => window.CharactersBrowser.ModelBinding?.SelectedIdentity == descriptor.Identity);
    }

    private static void SetPosePathPicker(AudioWindow window, Func<string?> picker) =>
        typeof(AudioWindow).GetProperty("CharacterPosePathPicker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, picker);

    private static NativeModelPosePackageLease? PoseOwner(AudioWindow window) =>
        (NativeModelPosePackageLease?)Field<NativeModelPosePackageLease?>(window, "characterPoseOwner");

    private static HashSet<Task> PreviewTasks(CharactersView view) =>
        (HashSet<Task>)typeof(CharactersView).GetField("previewTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The pose host did not settle.");
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
}
