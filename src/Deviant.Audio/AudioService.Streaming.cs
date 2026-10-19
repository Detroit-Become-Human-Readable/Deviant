using Deviant.Formats.Audio.Model;

namespace Deviant.Audio;

public sealed partial class AudioService
{
    public async Task<AudioWavMetadata> DecodeMaterializedToWavAsync(MediaEntry media, string sourceFile, Stream destination,
        WavFormat format = WavFormat.Pcm16, IProgress<AudioProgress>? progress = null,
        CancellationToken cancellationToken = default, DecodePurpose purpose = DecodePurpose.Preview)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek || destination.Position!=0 || destination.Length!=0)
            throw new ArgumentException("Decoded output requires an empty readable, writable, seekable stream.",nameof(destination));
        if (purpose==DecodePurpose.Conversion) MediaPolicy.RequireConversion(media);
        else if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
        else if (media.Completeness==MediaCompleteness.Fragment && purpose!=DecodePurpose.PartialPreview)
            throw new AudioToolException("Use Partial preview for this fragment.");
        EnsureExpectedWavSize(media,format,allowFragment:purpose==DecodePurpose.PartialPreview);
        RequireTool(paths.VgmstreamCli,"vgmstream-cli");
        using var input=new FileStream(Path.GetFullPath(sourceFile),FileMode.Open,FileAccess.Read,FileShare.Read);
        if (input.Length<=0 || input.Length>_limits.MaximumMaterializedInputBytes || input.Length!=media.Slice.Length)
            throw new AudioToolException("The materialized audio source does not match its bounded media extent.");
        var formatCode=format switch { WavFormat.Pcm16=>"1",WavFormat.Pcm24=>"2",WavFormat.Float32=>"4",_=>throw new ArgumentOutOfRangeException(nameof(format)) };
        progress?.Report(new("Decoding",Detail:"vgmstream"));
        var decoded=await _processes.RunBinaryAsync(new(paths.VgmstreamCli!,["-i","-W",formatCode,"-p",Path.GetFullPath(sourceFile)],
            Timeout:TimeSpan.FromHours(2)),destination,_limits.MaximumWavFileBytes,cancellationToken).ConfigureAwait(false);
        EnsureSuccess(decoded,"vgmstream decode failed");
        cancellationToken.ThrowIfCancellationRequested();
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new("Validating WAV"));
        var metadata=ReadWavMetadata(destination);
        EnsureWav(metadata);EnsureWavFormat(metadata,format);
        cancellationToken.ThrowIfCancellationRequested();
        return metadata;
    }
}
