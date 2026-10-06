using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class DdsDocumentPreviewTests
{
    [Fact]
    public void ValidatedArchiveDocumentUsesSharedMipLayerChannelAndZoomPreviewWithoutFileActions() => Run(root =>
    {
        var identity = new ResourceIdentity("fixture-index", 7); var revision = new SourceRevision(identity.IndexContentId, "fixture-revision");
        var document = new DdsTextureReader().Read(Fixture(4, 4, 2, 2), identity, revision); var view = new DdsImageView();
        var pendingChanges = 0; var readyChanges = 0; view.SurfaceChanged += (_, _) => { if (view.CurrentDisplaySurface is null) pendingChanges++; else readyChanges++; };
        var window = new Window { Content = view, Width = 1000, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") });
        try
        {
            window.Show(); Pump(view.LoadDocumentAsync(document, "Texture sample")); window.UpdateLayout();
            Assert.True(view.IsDocumentBacked); Assert.Same(document, view.CurrentDocument); Assert.Equal(identity, view.CurrentDisplaySurface!.Identity); Assert.Equal(revision, view.CurrentDisplaySurface.Revision);
            Assert.Equal("Texture sample", ((TextBlock)view.FindName("ImageName")).Text); Assert.Equal(new byte[] { 30, 20, 10, 40 }, Pixel(view)); AssertFileActionsHidden(view);
            ((ComboBox)view.FindName("Layers")).SelectedIndex = 1; ((ComboBox)view.FindName("Mips")).SelectedIndex = 1; Pump(view.WaitForRenderAsync());
            Assert.Equal(new TextureSubresourceSelection(1, null, 1), view.CurrentDisplaySurface!.Subresource); Assert.Equal(2, view.CurrentDisplaySurface.Width); Assert.Equal(new byte[] { 70, 80, 90, 60 }, Pixel(view));
            ((ComboBox)view.FindName("Channels")).SelectedIndex = 2; Pump(view.WaitForRenderAsync()); Assert.Equal(new byte[] { 90, 90, 90, 255 }, Pixel(view)); Assert.Equal(new byte[] { 90, 80, 70, 60 }, view.CurrentDisplaySurface!.Rgba8.Span[..4].ToArray());
            ((Slider)view.FindName("Zoom")).Value = 2; Assert.Equal(2, ((ScaleTransform)((Image)view.FindName("Preview")).LayoutTransform).ScaleX); AssertFileActionsHidden(view);
            Assert.Throws<InvalidOperationException>(() => { _ = view.ExportToAsync(AssetExportFormat.Dds, Path.Combine(root, "preview.dds")); });
            Assert.Throws<InvalidOperationException>(() => { _ = view.ExportToAsync(AssetExportFormat.Png, Path.Combine(root, "preview.png")); }); Assert.Empty(Directory.GetFiles(root)); Assert.True(pendingChanges >= 2); Assert.True(readyChanges >= 2);
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void LatestExternalOrDocumentSourceWinsAndExternalWorkflowRestoresActions() => Run(root =>
    {
        var view = new DdsImageView(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source); var file = Path.Combine(source, "external.dds"); File.WriteAllBytes(file, Fixture(4, 4)); var hosted = new DdsTextureReader().Read(Fixture(512, 512));
        try
        {
            var fileLoad = view.OpenAsync(file); var hostedLoad = view.LoadDocumentAsync(hosted, "Hosted sample"); Pump(Task.WhenAll(fileLoad, hostedLoad));
            Assert.Same(hosted, view.CurrentDocument); Assert.True(view.IsDocumentBacked); AssertFileActionsHidden(view);
            var oldHosted = view.LoadDocumentAsync(hosted, "Old hosted sample"); var latestFile = view.OpenAsync(file); Pump(Task.WhenAll(oldHosted, latestFile));
            Assert.False(view.IsDocumentBacked); Assert.Equal("external.dds", ((TextBlock)view.FindName("ImageName")).Text); Assert.Equal(4, view.CurrentDisplaySurface!.Width);
            foreach (var name in new[] { "OpenDds", "ExportDds", "ExportPng" }) { var button = (Button)view.FindName(name); Assert.Equal(Visibility.Visible, button.Visibility); Assert.True(button.IsEnabled); }
            var export = Path.Combine(root, "output", "image.dds"); Pump(view.ExportToAsync(AssetExportFormat.Dds, export)); Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(export));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void RapidHostedChangesCancellationAndDisposalCannotPublishEarlierDocuments() => Run(root =>
    {
        var view = new DdsImageView(); var first = new DdsTextureReader().Read(Fixture(512, 512, 2, 2)); var last = new DdsTextureReader().Read(Fixture(4, 4));
        try
        {
            var old = view.LoadDocumentAsync(first, "First"); var current = view.LoadDocumentAsync(last, "Last"); Pump(Task.WhenAll(old, current)); Assert.Same(last, view.CurrentDocument);
            Pump(view.LoadDocumentAsync(first, "First", new(true))); Assert.Null(view.CurrentDocument); Assert.Null(((Image)view.FindName("Preview")).Source); AssertFileActionsHidden(view);
            Pump(view.LoadDocumentAsync(first, "First"));
            for (var index = 0; index < 20; index++) { ((ComboBox)view.FindName("Layers")).SelectedIndex = index % 2; ((ComboBox)view.FindName("Mips")).SelectedIndex = index % 2; ((ComboBox)view.FindName("Channels")).SelectedIndex = index % 6; }
            ((ComboBox)view.FindName("Layers")).SelectedIndex = 1; ((ComboBox)view.FindName("Mips")).SelectedIndex = 1; Pump(view.WaitForRenderAsync()); Assert.Equal(new TextureSubresourceSelection(1, null, 1), view.CurrentDisplaySurface!.Subresource); AssertFileActionsHidden(view);
            var clearing = view.LoadDocumentAsync(first, "Clearing"); Pump(view.ClearAsync()); Pump(clearing); Assert.Null(view.CurrentDocument); Assert.Null(view.CurrentDisplaySurface); AssertFileActionsHidden(view);
            Pump(view.LoadDocumentAsync(last, "Reused")); Assert.Same(last, view.CurrentDocument); AssertFileActionsHidden(view);
            var pending = view.LoadDocumentAsync(first, "Pending"); Pump(view.DisposeAsync().AsTask()); Pump(pending); Assert.Null(view.CurrentDocument); Assert.Null(view.CurrentDisplaySurface); Assert.Null(((Image)view.FindName("Preview")).Source);
            Pump(Task.Delay(50)); Assert.Null(((Image)view.FindName("Preview")).Source);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void HostedIdentityRequiresAPairedMatchingRevision() => Run(root =>
    {
        var view = new DdsImageView(); var reader = new DdsTextureReader(); var bytes = Fixture(4, 4); var identityOnly = reader.Read(bytes, new ResourceIdentity("fixture-index", 1));
        try
        {
            Assert.Throws<ArgumentException>(() => { _ = view.LoadDocumentAsync(identityOnly, "Incomplete source"); }); Assert.Null(view.CurrentDocument);
            Assert.Throws<ArgumentException>(() => reader.Read(bytes, new("fixture-index", 1), new("other-index", "revision")));
            var complete = reader.Read(bytes, new("fixture-index", 1), new("fixture-index", "revision")); Pump(view.LoadDocumentAsync(complete, "Complete source")); Assert.Same(complete, view.CurrentDocument); AssertFileActionsHidden(view);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    private static void AssertFileActionsHidden(DdsImageView view)
    { foreach (var name in new[] { "OpenDds", "ExportDds", "ExportPng" }) { var button = (Button)view.FindName(name); Assert.Equal(Visibility.Collapsed, button.Visibility); Assert.False(button.IsEnabled); } }
    private static byte[] Pixel(DdsImageView view)
    { var source = (BitmapSource)((Image)view.FindName("Preview")).Source; var pixel = new byte[4]; source.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0); return pixel; }
    private static byte[] Fixture(int width, int height, int layers = 1, int mipCount = 1)
    {
        using var payload = new MemoryStream();
        for (var layer = 0; layer < layers; layer++) for (var mip = 0; mip < mipCount; mip++)
        {
            byte[] pixel = layer == 1 && mip == 1 ? [90, 80, 70, 60] : [10, 20, 30, 40];
            for (var index = 0; index < (width >> mip) * (height >> mip); index++) payload.Write(pixel);
        }
        var bytes = new byte[148 + payload.Length]; "DDS "u8.CopyTo(bytes); Put(4, 124); Put(8, mipCount > 1 ? 0x21007u : 0x1007u); Put(12, (uint)height); Put(16, (uint)width); Put(28, (uint)mipCount);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108, layers > 1 || mipCount > 1 ? 0x401008u : 0x1000u); Put(128, 28); Put(132, 3); Put(140, (uint)layers); payload.ToArray().CopyTo(bytes, 148); return bytes;
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
    private static void Run(Action<string> action)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-dds-document-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Document preview check timed out."); if (failure is not null) throw new InvalidOperationException("Document preview check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task) { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
}
