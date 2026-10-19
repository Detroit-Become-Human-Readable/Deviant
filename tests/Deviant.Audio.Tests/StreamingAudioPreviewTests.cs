using Deviant.Archives;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class StreamingAudioPreviewTests
{
    [AudioToolsFact(requireDecoder:true,requireFfmpeg:false)]
    public async Task BrowserPreviewUsesAdmittedFilesAndAWarmWaveDoesNotNeedItsDecoderInput()
    {
        using var fixture=NativePcmFixture();
        using var workspace=new Workspace();
        await using var session=await AudioExplorerSession.OpenAsync(fixture.IndexPath,workspace.Cache,workspace.Aliases,
            AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
        var media=Assert.Single(session.Catalog.Media);
        await using var preview=await session.PreviewAsync(media.Identity!);
        Assert.StartsWith(workspace.Cache.Location+Path.DirectorySeparatorChar,preview.Path);
        Assert.Equal(2205,AudioService.ReadWavMetadata(preview.Path).SampleFrames);
        var input=Assert.Single(Directory.EnumerateFiles(workspace.Cache.Location,"*.wem",SearchOption.AllDirectories));
        Assert.Equal(media.Slice.Length,new FileInfo(input).Length);
        using var blockedInput=new FileStream(input,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        await using var warm=await session.PreviewAsync(media.Identity!);
        Assert.Equal(preview.Path,warm.Path);
        Assert.Equal(await File.ReadAllBytesAsync(preview.Path),await File.ReadAllBytesAsync(warm.Path));
        Assert.True(workspace.Cache.UsageBytes<=workspace.Cache.LimitBytes);
    }

    [InlineData(WavFormat.Pcm16)]
    [InlineData(WavFormat.Pcm24)]
    [InlineData(WavFormat.Float32)]
    [AudioToolsTheory(requireDecoder:true,requireFfmpeg:false)]
    public async Task PipedDecodeMatchesTheExistingDecoderWithoutClosingItsOutput(WavFormat format)
    {
        using var fixture=NativePcmFixture();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var catalog=(await new DetroitAudioSnapshotReader().ReadSnapshotAsync(snapshot)).Catalog;
        var media=Assert.Single(catalog.Media);
        var bytes=new byte[checked((int)media.Slice.Length)];
        using(var source=File.OpenRead(media.Slice.FilePath))
        { source.Position=media.Slice.Offset;await source.ReadExactlyAsync(bytes); }
        var materialized=Path.Combine(fixture.DirectoryPath,"selected.wem");
        await File.WriteAllBytesAsync(materialized,bytes);
        var service=new AudioService(AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
        var baseline=Path.Combine(fixture.DirectoryPath,"reference.wav");
        await service.DecodeToWavAsync(catalog,media,baseline,format,purpose:DecodePurpose.Preview);
        using var output=new MemoryStream();

        var metadata=await service.DecodeMaterializedToWavAsync(media,materialized,output,format);

        Assert.True(output.CanRead&&output.CanWrite&&output.CanSeek);
        Assert.Equal(await File.ReadAllBytesAsync(baseline),output.ToArray());
        Assert.Equal(AudioService.ReadWavMetadata(baseline),metadata);
    }

    [InlineData(0)]
    [InlineData(256)]
    [AudioToolsTheory(requireDecoder:true,requireFfmpeg:false)]
    public async Task PreviewCannotCreateUnadmittedInputOrDecodedFiles(long limit)
    {
        using var fixture=NativePcmFixture();
        using var workspace=new Workspace();
        var cache=workspace.Cache;
        await using var session=await AudioExplorerSession.OpenAsync(fixture.IndexPath,cache,workspace.Aliases,
            AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
        var media=Assert.Single(session.Catalog.Media);
        await cache.SetLimitAsync(limit);
        var before=Directory.EnumerateFiles(cache.Location,"*",SearchOption.AllDirectories).Sum(path=>new FileInfo(path).Length);

        var error=await Assert.ThrowsAsync<IOException>(async()=>
        { await using var preview=await session.PreviewAsync(media.Identity!); });

        Assert.Equal("CacheStorageLimit",error.Data["ErrorCode"]);
        Assert.Equal(0,cache.ActivePreviewLeases);
        Assert.Equal(before,Directory.EnumerateFiles(cache.Location,"*",SearchOption.AllDirectories).Sum(path=>new FileInfo(path).Length));
        Assert.Empty(Directory.EnumerateFiles(cache.Location,"*.wav",SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(cache.Location,"*.wem",SearchOption.AllDirectories));
    }

    private static FormatsFixtureBuilder NativePcmFixture()
    {
        var fixture=FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033,1,FormatsFixtureBuilder.MakeDialogueMedia("Generated_Sample_ENG",
            FormatsFixtureBuilder.MakePcmWave(22050,2205)));
        fixture.WriteIndex();return fixture;
    }

    private sealed class Workspace:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"deviant-streaming-preview-"+Guid.NewGuid().ToString("N"));
        public CacheManager Cache{get;}
        public AliasStore Aliases{get;}
        public Workspace()
        { Cache=new(Path.Combine(root,"settings"),new(Path.Combine(root,"cache")));Aliases=new(Path.Combine(root,"library")); }
        public void Dispose(){Cache.Dispose();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
