using System.IO;
using System.Buffers.Binary;
using System.Text;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Audio;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class AudioMetadataExportGuardTests
{
    [Fact]
    public async Task BatchLeaseRetainsCompletedFilesAndPreviousOutputsWhenTheNextVolumeChanges()
    {
        using var fixture=new DiscoveryFixture(2);
        var volume=Path.ChangeExtension(fixture.Index,".dat");
        var nextVolume=Path.Combine(Path.GetDirectoryName(fixture.Index)!,IndexLayout.VolumeName("archive",1));
        File.Copy(volume,nextVolume);
        var index=await File.ReadAllBytesAsync(fixture.Index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength+IndexLayout.RecordLength+24),1);
        await File.WriteAllBytesAsync(fixture.Index,index);
        var cache=new CacheManager(Path.Combine(fixture.Root,"settings"),new(fixture.Cache));
        await using var session=await AudioExplorerSession.OpenAsync(fixture.Index,cache,new AliasStore(fixture.Library));await session.EnrichmentTask;
        var media=session.Catalog.Media.OrderBy(item=>item.Identity!.Ordinal).ToArray();Assert.Equal(2,media.Length);
        var volumes=session.Snapshot.Volumes.Select(item=>item with{Stamp=item.Stamp with{Reliable=false}}).ToArray();
        var snapshot=session.Snapshot with{IndexStamp=session.Snapshot.IndexStamp with{Reliable=false},Volumes=volumes,PhysicalSourceStamp=SourceIdentity.PhysicalStamp(volumes)};
        var exporter=new AudioExportService(new Deviant.Audio.AudioService(Deviant.Audio.AudioToolPaths.Discover()),revisionProvider:new FixedUnreliableRevisions(snapshot));
        var request=new AudioExportRequest(snapshot,media.Select(item=>new AudioExportSource(item.Identity!,item)).ToArray(),true);
        var options=new AudioExportOptions(Path.Combine(fixture.Root,"exports"),AudioExportFormat.Original,Overwrite:true,ConversionWorkers:1);
        var initial=await exporter.ExportAsync(request,options);Assert.All(initial.Manifest.Entries,item=>Assert.Equal(AudioExportStatus.Completed,item.Status));
        var secondOutput=Path.Combine(options.OutputDirectory,initial.Manifest.Entries[1].RelativeOutput);var previous=await File.ReadAllBytesAsync(secondOutput);
        var changed=false;
        var progress=new InlineProgress(value=>
        {
            if(changed||value.Detail!=media[0].DisplayLabel)return;
            var bytes=File.ReadAllBytes(nextVolume);var position=bytes.AsSpan().IndexOf("Clip 2_ENG"u8);Assert.True(position>=0);
            var timestamp=File.GetLastWriteTimeUtc(nextVolume);bytes[position]=(byte)'X';File.WriteAllBytes(nextVolume,bytes);File.SetLastWriteTimeUtc(nextVolume,timestamp);changed=true;
        });
        var report=await exporter.ExportAsync(request,options,progress);
        Assert.True(changed);Assert.Equal(AudioExportStatus.Completed,report.Manifest.Entries[0].Status);
        Assert.Equal(AudioExportStatus.Failed,report.Manifest.Entries[1].Status);Assert.Equal("SourceChanged",report.Manifest.Entries[1].ErrorCode);
        Assert.Equal(previous,await File.ReadAllBytesAsync(secondOutput));Assert.Empty(Directory.EnumerateFiles(options.OutputDirectory,"*.tmp",SearchOption.AllDirectories));
        using var unlocked=File.Open(nextVolume,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }

    private sealed class InlineProgress(Action<Deviant.Audio.AudioProgress> action):IProgress<Deviant.Audio.AudioProgress>
    {public void Report(Deviant.Audio.AudioProgress value)=>action(value);}

    [Fact]
    public async Task OriginalPerFileExportRejectsChangedClassificationBytesOutsideTheCopiedPayload()
    {
        using var fixture=new DiscoveryFixture(1);
        var cache=new CacheManager(Path.Combine(fixture.Root,"settings"),new(fixture.Cache));
        await using var session=await AudioExplorerSession.OpenAsync(fixture.Index,cache,new AliasStore(fixture.Library));
        await session.EnrichmentTask;
        var row=Assert.Single(session.ResolveSelection([Assert.Single(session.Catalog.Media).Identity!]));
        Assert.True(row.Media.MetadataReadCoverageComplete);Assert.NotEmpty(row.Media.MetadataReadDigests);
        var volumes=session.Snapshot.Volumes.Select(volume=>volume with{Stamp=volume.Stamp with{Reliable=false}}).ToArray();
        var snapshot=session.Snapshot with{IndexStamp=session.Snapshot.IndexStamp with{Reliable=false},Volumes=volumes,PhysicalSourceStamp=SourceIdentity.PhysicalStamp(volumes)};
        var exporter=new AudioExportService(new Deviant.Audio.AudioService(Deviant.Audio.AudioToolPaths.Discover()),revisionProvider:new FixedUnreliableRevisions(snapshot));
        var request=new AudioExportRequest(snapshot,[new(row.Identity,row.Media)],true);
        var options=new AudioExportOptions(Path.Combine(fixture.Root,"exports"),AudioExportFormat.Original,Overwrite:true);
        var initial=await exporter.ExportAsync(request,options);var first=Assert.Single(initial.Manifest.Entries);Assert.Equal(AudioExportStatus.Completed,first.Status);
        var output=Path.Combine(options.OutputDirectory,first.RelativeOutput);var previous=await File.ReadAllBytesAsync(output);
        var source=volumes.Single().FilePath;var bytes=await File.ReadAllBytesAsync(source);var position=bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Clip 1_ENG"));Assert.True(position>=0);
        Assert.True(position<row.Media.Slice.Offset||position>=row.Media.Slice.Offset+row.Media.Slice.Length,"Mutation must be outside exported media bytes.");
        var mediaBefore=bytes.AsSpan((int)row.Media.Slice.Offset,(int)row.Media.Slice.Length).ToArray();bytes[position]=(byte)'X';var timestamp=File.GetLastWriteTimeUtc(source);await File.WriteAllBytesAsync(source,bytes);File.SetLastWriteTimeUtc(source,timestamp);
        Assert.Equal(mediaBefore,bytes.AsSpan((int)row.Media.Slice.Offset,(int)row.Media.Slice.Length).ToArray());
        var changed=await exporter.ExportAsync(request,options);var failed=Assert.Single(changed.Manifest.Entries);
        Assert.Equal(AudioExportStatus.Failed,failed.Status);Assert.Equal("SourceChanged",failed.ErrorCode);Assert.Contains("source bytes",failed.Diagnostic,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previous,await File.ReadAllBytesAsync(output));Assert.Empty(Directory.GetFiles(options.OutputDirectory,"*.tmp"));
    }
    private sealed class FixedUnreliableRevisions:ISourceRevisionProvider
    {
        private readonly Dictionary<string,FileStamp> stamps;
        public FixedUnreliableRevisions(ArchiveSnapshot source){stamps=new(StringComparer.OrdinalIgnoreCase){[source.IndexPath]=source.IndexStamp};foreach(var volume in source.Volumes)stamps[volume.FilePath]=volume.Stamp;}
        public FileStamp Capture(string path)=>stamps.TryGetValue(path,out var stamp)?stamp:new FileRevisionProvider().Capture(path);
        public FileStamp Capture(SafeFileHandle handle,string path)=>stamps.TryGetValue(path,out var stamp)?stamp:new FileRevisionProvider().Capture(handle,path);
    }
}
