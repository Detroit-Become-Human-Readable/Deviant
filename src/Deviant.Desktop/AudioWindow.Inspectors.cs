using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private void AttachInspectors(MainWindow window)=>window.InspectionRequested=InspectFromAdvancedAsync;
    private async Task InspectFromAdvancedAsync(ArchiveSnapshot source,ResourceRecord row,string view)
    {
        if(view=="buffer")EnsureBufferRequestAllowed();
        if(session is null||session.Snapshot.IndexPath!=source.IndexPath||session.Snapshot.IndexContentId!=source.IndexContentId||session.Snapshot.PhysicalSourceStamp!=source.PhysicalSourceStamp)
            await OpenArchiveAsync(source.IndexPath);
        if(session is null||session.Snapshot.IndexContentId!=source.IndexContentId||session.Snapshot.PhysicalSourceStamp!=source.PhysicalSourceStamp)
            throw new InvalidOperationException("The resource changed. Reopen the archive.");
        if(view=="buffer")EnsureBufferRequestAllowed();
        var navigation=BeginNavigation();
        await NavigateDiscoveryAsync(view,new(source.IndexContentId,row.Ordinal),navigation.Generation,navigation.Token);
        Activate();
    }
}
