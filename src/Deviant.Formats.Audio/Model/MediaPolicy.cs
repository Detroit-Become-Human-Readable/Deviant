namespace Deviant.Formats.Audio.Model;

public static class MediaPolicy
{
    public static bool HasBytes(MediaEntry media) => media.Availability == SourceAvailability.Available && media.Slice.Length > 0 && !string.IsNullOrWhiteSpace(media.Slice.FilePath);
    public static bool IsComplete(MediaEntry media) => HasBytes(media) && media.Completeness == MediaCompleteness.Complete &&
        media.ContainerValidity == ContainerValidity.Valid && media.State is MediaState.CompleteEmbedded or MediaState.CompleteExternal;
    public static bool CanConvert(MediaEntry media) => CanPreview(media) && media.Completeness != MediaCompleteness.Fragment &&
        media.ContainerValidity == ContainerValidity.Valid && media.State != MediaState.PrefetchOnly;
    public static bool CanPreview(MediaEntry media) => media.Midi is null && media.Codec != "MIDI" && HasBytes(media) && media.IsRiff &&
        media.State is MediaState.CompleteEmbedded or MediaState.CompleteExternal or MediaState.PrefetchOnly &&
        media.ContainerValidity is ContainerValidity.Valid or ContainerValidity.Truncated;
    public static string GetUnavailableReason(MediaEntry media)
    {
        if (CanPreview(media)) return "";
        if (media.Midi is not null || media.Codec == "MIDI") return "MIDI playback and conversion are unavailable.";
        return media.Availability switch
        {
            SourceAvailability.Missing => "Source file is missing.",
            SourceAvailability.Ambiguous => "Multiple source files match this entry.",
            _ => media.State switch
            {
                MediaState.MissingExternal => "The external stream could not be located.",
                MediaState.Ambiguous => "Multiple streams match this entry.",
                MediaState.Malformed => "The audio data is malformed.",
                MediaState.Unsupported => "This audio format cannot be previewed.",
                _ => "No playable audio data is available."
            }
        };
    }
    public static void RequireConversion(MediaEntry media)
    {
        if (CanConvert(media)) return;
        throw new InvalidOperationException(media.Completeness == MediaCompleteness.Fragment || media.State == MediaState.PrefetchOnly || media.ContainerValidity == ContainerValidity.Truncated
            ? "The full stream is required for OGG or WAV export." : GetUnavailableReason(media));
    }
}
