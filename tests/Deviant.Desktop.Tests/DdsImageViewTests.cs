using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class DdsImageViewTests
{
    [Fact]
    public void NativePreviewChoosesASmallerMipAndRetainsTheHigherMipForExplicitSelection()
    {
        var bytes = new byte[148 + 2048 * 4 * 4 + 1024 * 2 * 4 + 512 * 1 * 4];
        "DDS "u8.CopyTo(bytes);
        Put(4, 124); Put(8, 0x2100F); Put(12, 4); Put(16, 2048); Put(20, 2048 * 4); Put(28, 3);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108, 0x401008);
        Put(128, 28); Put(132, 3); Put(140, 1);
        var document = new DdsTextureReader().Read(bytes);
        Assert.Equal(1, NativeTextureView.SelectPreviewMip(document));
        Assert.Equal(3, document.Info.MipCount);
        Assert.Equal(2048, document.MipLevels[0].Width);
        Assert.Equal(0, NativeTextureView.SelectPreviewMip(document, maximumEdge: 2048));
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    [Fact]
    public void RendersRgbaAndRejectsChangedBytesWithRetainedLengthAndWriteTime()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"deviant-dds-view-"+Guid.NewGuid().ToString("N"));
            var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
            var view=new DdsImageView();var window=new Window{Content=view,Left=-10000,Top=-10000,Width=800,Height=600,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var path=Path.Combine(source,"image.dds");var bytes=Fixture();File.WriteAllBytes(path,bytes);
                window.Show();Pump(view.OpenAsync(path));
                var bitmap=(BitmapSource)((Image)view.FindName("Preview")).Source;
                var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);
                Assert.Equal(new byte[]{30,20,10,128},pixel);
                var timestamp=File.GetLastWriteTimeUtc(path);bytes[^1]^=1;File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,timestamp);
                var destination=Path.Combine(root,"output","image.dds");
                var error=Pump(Assert.ThrowsAsync<ArchiveException>(()=>view.ExportToAsync(AssetExportFormat.Dds,destination)));
                Assert.Equal("SourceChanged",error.Code);Assert.False(File.Exists(destination));
            }
            catch(Exception error){failure=error;}
            finally{Pump(view.DisposeAsync().AsTask());window.Close();Directory.Delete(root,true);}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(15)),"DDS view check timed out.");
        if(failure is not null)throw new InvalidOperationException("DDS view check failed.",failure);
    }
    private static byte[] Fixture()
    {
        var bytes=new byte[152];"DDS "u8.CopyTo(bytes);Put(4,124);Put(8,0x1007);Put(12,1);Put(16,1);
        Put(76,32);Put(80,4);"DX10"u8.CopyTo(bytes.AsSpan(84));Put(108,0x1000);Put(128,28);Put(132,3);Put(140,1);
        new byte[]{10,20,30,128}.CopyTo(bytes,148);return bytes;
        void Put(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
    }
    private static T Pump<T>(Task<T> task){Pump((Task)task);return task.GetAwaiter().GetResult();}
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
        task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
}
