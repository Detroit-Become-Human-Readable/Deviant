using Deviant.Assets;
using Deviant.Browser;
using Deviant.Desktop;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class AudioMediaPresentationTests
{
    [Theory]
    [InlineData(480, "480 PPQN")]
    [InlineData(0xe850, "SMPTE 24 fps · 80 ticks/frame")]
    [InlineData(0xe750, "SMPTE 25 fps · 80 ticks/frame")]
    [InlineData(0xe350, "SMPTE 29.97 fps · 80 ticks/frame")]
    [InlineData(0xe250, "SMPTE 30 fps · 80 ticks/frame")]
    public void MidiDetailsShowFormatTracksAndDivision(int division, string expected)
    {
        var row = Row(new MediaEntry { Codec = "MIDI", Midi = new(1, 2, (ushort)division) });

        var details = AudioMediaPresentation.Details(row);

        Assert.Equal("Standalone · SFX · MIDI\nFormat 1 · 2 tracks · " + expected, details);
    }

    [Fact]
    public void SingleMidiTrackUsesTheSingularLabel()
    {
        var row = Row(new MediaEntry { Codec = "MIDI", Midi = new(0, 1, 96) });

        Assert.Contains("Format 0 · 1 track · 96 PPQN", AudioMediaPresentation.Details(row));
    }

    [Fact]
    public void AudioDetailsKeepDurationAndChannels()
    {
        var row = Row(new MediaEntry { Codec = "PCM", Duration = 1.25, Channels = 2 });

        Assert.Equal("Standalone · SFX · PCM · 1.25 s · 2 ch\n", AudioMediaPresentation.Details(row));
    }

    private static AudioExplorerRow Row(MediaEntry media)
    {
        var identity = new ResourceIdentity("generated-index", 0);
        media.Identity = identity;
        media.Completeness = MediaCompleteness.Complete;
        return new(new(identity, new("generated-index", "revision"), "audio.media", "Generated media", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Original, [], []), media, null);
    }
}
