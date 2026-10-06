using System.IO;
using System.Text;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization.Model;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class LocalizationWarmHeadTests
{
    [Fact]
    public async Task UnreliableStampReusesVerifiedBytesThenReindexesAndExportsChangedText()
    {
        using var fixture=new DiscoveryFixture(1);var archive=await new ArchiveReader().OpenAsync(fixture.Index);
        var volumes=archive.Volumes.Select(volume=>volume with{Stamp=volume.Stamp with{Reliable=false}}).ToArray();
        var snapshot=archive with{IndexStamp=archive.IndexStamp with{Reliable=false},Volumes=volumes,PhysicalSourceStamp=SourceIdentity.PhysicalStamp(volumes)};var revisions=new FixedUnreliableRevisions(snapshot);
        string metadata;Deviant.Assets.ResourceIdentity identity;
        await using(var cold=await LocalizationSession.OpenAsync(snapshot,fixture.Cache,sourceRevisionProvider:revisions))
        {await cold.EnrichmentTask;Assert.True(cold.IsEnrichmentComplete);metadata=Assert.IsType<string>(cold.MetadataContentId);var row=Assert.Single((await cold.QueryAsync(new LocalizationQuery(Language:"ENG"))).Records);Assert.Equal("ENG text 0",row.Text);identity=row.Identity;}
        await using(var warm=await LocalizationSession.OpenAsync(snapshot,fixture.Cache,sourceRevisionProvider:revisions))
        {Assert.True(warm.EnrichmentTask.IsCompletedSuccessfully);Assert.True(warm.IsEnrichmentComplete);Assert.Equal(metadata,warm.MetadataContentId);Assert.Equal(identity,Assert.Single((await warm.QueryAsync(new LocalizationQuery(Language:"ENG"))).Records).Identity);}
        var path=volumes.Single().FilePath;var bytes=await File.ReadAllBytesAsync(path);var needle=Encoding.Unicode.GetBytes("ENG text 0");var position=bytes.AsSpan().IndexOf(needle);Assert.True(position>=0);Encoding.Unicode.GetBytes("NEW text 0").CopyTo(bytes,position);var stamp=File.GetLastWriteTimeUtc(path);await File.WriteAllBytesAsync(path,bytes);File.SetLastWriteTimeUtc(path,stamp);
        await using var changed=await LocalizationSession.OpenAsync(snapshot,fixture.Cache,sourceRevisionProvider:revisions);await changed.EnrichmentTask;Assert.True(changed.IsEnrichmentComplete);Assert.NotEqual(metadata,changed.MetadataContentId);
        var selected=Assert.Single((await changed.QueryAsync(new LocalizationQuery(Language:"ENG"))).Records);Assert.Equal(identity,selected.Identity);Assert.Equal("NEW text 0",selected.Text);
        var output=Path.Combine(fixture.Root,"exports","strings.json");var report=await changed.ExportAsync(LocalizationExportSelection.Selected([identity]),LocalizationExportFormat.Json,output);Assert.Equal(1,report.Manifest.CompletedCount);Assert.Equal(0,report.Manifest.FailedCount);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(output));Assert.Equal("NEW text 0",json.RootElement.GetProperty("entries")[0].GetProperty("record").GetProperty("text").GetString());
        await using var freshWarm=await LocalizationSession.OpenAsync(snapshot,fixture.Cache,sourceRevisionProvider:revisions);Assert.True(freshWarm.EnrichmentTask.IsCompletedSuccessfully);Assert.Equal(changed.MetadataContentId,freshWarm.MetadataContentId);
    }
    private sealed class FixedUnreliableRevisions:ISourceRevisionProvider
    {
        private readonly Dictionary<string,FileStamp> stamps;
        public FixedUnreliableRevisions(ArchiveSnapshot source){stamps=new(StringComparer.OrdinalIgnoreCase){[source.IndexPath]=source.IndexStamp};foreach(var volume in source.Volumes)stamps[volume.FilePath]=volume.Stamp;}
        public FileStamp Capture(string path)=>stamps[path];
        public FileStamp Capture(SafeFileHandle handle,string path)=>Capture(path);
    }
}
