using Deviant.Archives;
using Deviant.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;
namespace Deviant.Audio.Tests;

public sealed class RendererToolTests
{
    [AudioToolsTheory(true,true)]
    [InlineData(-1200,4d)] [InlineData(1200,1d)]
    public async Task GeneratedTonePitchUsesTheOutputTimeline(float pitch,double expected)
    {
        using var fixture=FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033,1,FormatsFixtureBuilder.MakePcmWave(48000,96000)); fixture.WriteIndex();
        var catalog=await CatalogAsync(fixture);
        var item=new PlaybackItem("tone","Tone",1,0,expected,1,0,pitch,[],SourceDurationSeconds:2,PlaybackSpeed:Math.Pow(2,pitch/1200d));
        var plan=new PlaybackPlan("event",1,"Tone",1,[item],[],expected,"",FadeOutSeconds:.1);
        var result=await Service().RenderAsync(catalog,plan,Path.Combine(fixture.DirectoryPath,"rendered.wav"));
        Assert.InRange(result.Duration.TotalSeconds,expected-.002,expected+.002); Assert.Equal(1,result.Channels);
    }
    [AudioToolsFact(true,true)]
    public async Task LoopsPitchTrimsCapAndFadeShareTheOutputTimeline()
    {
        using var fixture=FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(1033,1,FormatsFixtureBuilder.MakePcmWave(48000,96000)); fixture.WriteIndex();
        var catalog=await CatalogAsync(fixture);
        var item=new PlaybackItem("tone","Tone",1,0,3,2,0,-1200,[],TrimBeginSeconds:.25,TrimEndSeconds:-.5,SourceDurationSeconds:1.25,PlaybackSpeed:.5);
        var plan=new PlaybackPlan("event",1,"Tone",1,[item],[],3,"",FadeOutSeconds:.5,MaximumDurationSeconds:3);
        var result=await Service().RenderAsync(catalog,plan,Path.Combine(fixture.DirectoryPath,"rendered.wav"));
        Assert.InRange(result.Duration.TotalSeconds,2.998,3.002);
        var metadata=AudioService.ReadWavMetadata(result.OutputPath); Assert.Equal(144000,metadata.SampleFrames);
    }
    private static AudioService Service()=>new(AudioToolPaths.Discover(Environment.GetEnvironmentVariable("DEVIANT_AUDIO_TOOLS")));
    private static async Task<AudioCatalog> CatalogAsync(FormatsFixtureBuilder fixture)
    {
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath); var row=Assert.Single(snapshot.Records); var volume=Assert.Single(snapshot.Volumes);
        var catalog=new AudioCatalog {SourcePath=snapshot.IndexPath,IndexContentId=snapshot.IndexContentId,Fingerprint=snapshot.IndexContentId,
            PhysicalSourceStamp=snapshot.PhysicalSourceStamp,IndexStamp=snapshot.IndexStamp,SourceVolumes=snapshot.Volumes.ToList()};
        catalog.Media.Add(new MediaEntry { Key="tone",Identity=new(snapshot.IndexContentId,row.Ordinal),Id=1,
            Slice=new(volume.FilePath,row.Offset,row.StoredSize),State=MediaState.CompleteEmbedded,Completeness=MediaCompleteness.Unverified,
            ContainerValidity=ContainerValidity.Valid,Availability=SourceAvailability.Available,IsRiff=true,Codec="PCM",Channels=1,SampleRate=48000,Duration=2 });
        return catalog;
    }
}
