using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private async Task DecodePreviewOutputAsync(MediaEntry media, VolumeSource volume, SafeFileHandle sourceHandle,
        string payloadHash, Stream output, WavFormat format, bool partial, IProgress<AudioProgress>? progress, CancellationToken token)
    {
        var extension=media.IsRiff ? ".wem" : Path.GetExtension(volume.FilePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) extension=".bin";
        var revision=SourceIdentity.DerivedCacheKey(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp);
        var key=CacheManager.PreparedKey("audio-input","audio-source-slice-v1",revision,payloadHash+"/"+extension);
        using var input=await cache.AcquirePreparedOutputAsync("audio-input",key,extension,async(destination,cancellation)=>
        {
            SourceIdentity.EnsureUnchanged(new FileRevisionProvider(),sourceHandle,volume.FilePath,volume.Stamp);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[128*1024];long copied=0;
            while(copied<media.Slice.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int count=await RandomAccess.ReadAsync(sourceHandle,buffer.AsMemory(0,(int)Math.Min(buffer.Length,media.Slice.Length-copied)),
                    checked(media.Slice.Offset+copied),cancellation).ConfigureAwait(false);
                if(count==0)throw new ArchiveException("SourceChanged","The audio source ended while preparing its decoder input.");
                hash.AppendData(buffer.AsSpan(0,count));
                await destination.WriteAsync(buffer.AsMemory(0,count),cancellation).ConfigureAwait(false);
                copied+=count;
            }
            if(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()!=payloadHash)
                throw new ArchiveException("SourceChanged","The audio source changed while preparing its decoder input.");
            SourceIdentity.EnsureUnchanged(new FileRevisionProvider(),sourceHandle,volume.FilePath,volume.Stamp);
        },async(path,cancellation)=>
        {
            await using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous);
            return source.Length==media.Slice.Length &&
                Convert.ToHexString(await SHA256.HashDataAsync(source,cancellation).ConfigureAwait(false)).ToLowerInvariant()==payloadHash;
        },token).ConfigureAwait(false);
        await audioService.DecodeMaterializedToWavAsync(media,input.Path,output,format,progress,token,
            partial?DecodePurpose.PartialPreview:DecodePurpose.Preview).ConfigureAwait(false);
    }
}
