using Deviant.Assets;
using Deviant.Formats.Audio.Model;
namespace Deviant.Desktop;

public sealed record PlaybackContext(ResourceIdentity Identity,string Label,MediaCompleteness Completeness,long Generation)
{
    public string Badge => Completeness == MediaCompleteness.Fragment ? "Partial" : "";
}
public sealed class AliasDraftStore
{
    private readonly Dictionary<string,(string Text,bool Dirty)> drafts=[];
    public string Read(ResourceIdentity identity,string? saved)
    {
        if(drafts.TryGetValue(identity.Key,out var draft)&&draft.Dirty) return draft.Text;
        drafts[identity.Key]=(saved??"",false); return saved??"";
    }
    public void Edit(ResourceIdentity identity,string text) => drafts[identity.Key]=(text,true);
    public void Saved(ResourceIdentity identity,string? text) => drafts[identity.Key]=(text??"",false);
    public void Clear() => drafts.Clear();
}
public interface IAudioOutputDeviceProvider
{ Task<IReadOnlyList<AudioOutputDevice>> EnumerateAsync(CancellationToken token=default); }
public sealed class AudioOutputDeviceProvider : IAudioOutputDeviceProvider
{ public Task<IReadOnlyList<AudioOutputDevice>> EnumerateAsync(CancellationToken token=default)=>Task.Run(WasapiBackendFactory.EnumerateOutputDevices,token); }
