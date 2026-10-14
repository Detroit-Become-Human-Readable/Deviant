using Deviant.Assets;
using Deviant.Formats.Audio.Model;
using Xunit;
using NameEvidence = Deviant.Formats.Audio.Model.NameEvidence;

namespace Deviant.Audio.Tests;

public sealed class AudioMediaNamingTests
{
    [Fact]
    public void LinkedEventsAddNamesAndPreferStateThenSortedLabel()
    {
        var evidence = new NameEvidence("Alpha", "WwiseEvent", 10, NameKind.Verified,
            "event metadata", 42, "High");
        var media = NumericMedia(42, "bank", "media", ["play", "state-z", "state-a", "control"]);
        var catalog = new AudioCatalog
        {
            Media = [media],
            Events =
            [
                Event("play", "Play", EventBehavior.Playback),
                Event("state-z", "Zulu", EventBehavior.StateChange),
                Event("state-a", "Alpha", EventBehavior.StateChange, [evidence]),
                Event("control", "Controls", EventBehavior.Control)
            ]
        };

        AudioMediaNaming.Apply(catalog);

        Assert.Equal("Alpha", media.DisplayLabel);
        Assert.Equal("42", media.Name);
        Assert.Equal(42u, media.Id);
        Assert.Equal(new[] { "Alpha", "Play", "Zulu" }, media.Aliases.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(media.Names, name => name.Text == "Alpha" && name.Namespace == "MediaLabel" &&
            name.Kind == NameKind.Description && name.Id == 42 && name.Source == evidence.Source &&
            name.Offset == evidence.Offset && name.Confidence == evidence.Confidence);
        Assert.DoesNotContain(media.Names, name => name.Text == "Controls");
    }

    [Fact]
    public void NumericEventNameDoesNotPromoteCandidateEvidence()
    {
        var media = NumericMedia(42, "bank", "media", ["event"]);
        var catalog = new AudioCatalog
        {
            Media = [media],
            Events = [Event("event", "42", EventBehavior.Playback,
                [new NameEvidence("PossibleName", "WwiseEvent", 42, NameKind.Candidate, "strings")])]
        };

        AudioMediaNaming.Apply(catalog);

        Assert.Equal("", media.DisplayLabel);
        Assert.Empty(media.Aliases);
        Assert.Empty(media.Names);
    }

    [Fact]
    public void MeaningfulStoredMediaNameWinsWhileEventNamesRemainAliases()
    {
        var identity = new ResourceIdentity("fixture", 7, "media/42");
        var media = new MediaEntry
        {
            Key = "media", Identity = identity, Id = 42, BankKey = "bank", Name = "Stored Clip",
            NameKind = NameKind.Stored, EventKeys = ["event"]
        };
        var catalog = new AudioCatalog
        {
            Media = [media],
            Events = [Event("event", "Resolved Event", EventBehavior.Playback)]
        };

        AudioMediaNaming.Apply(catalog);

        Assert.Equal("Stored Clip", media.DisplayLabel);
        Assert.Equal("Stored Clip", media.Name);
        Assert.Same(identity, media.Identity);
        Assert.Contains("Resolved Event", media.Aliases);
        Assert.Contains(media.Names, name => name.Text == "Resolved Event" && name.Kind == NameKind.Description);
    }

    [Fact]
    public void EqualMediaIdsUseOnlyTheirExplicitEventKeys()
    {
        var first = NumericMedia(42, "bank-a", "media-a", ["event-a"]);
        var second = NumericMedia(42, "bank-b", "media-b", ["event-b"]);
        var catalog = new AudioCatalog
        {
            Media = [first, second],
            Events =
            [
                Event("event-a", "First Bank Event", EventBehavior.Playback),
                Event("event-b", "Second Bank Event", EventBehavior.Playback)
            ]
        };

        AudioMediaNaming.Apply(catalog);

        Assert.Equal("First Bank Event", first.DisplayLabel);
        Assert.Equal("Second Bank Event", second.DisplayLabel);
        Assert.DoesNotContain("Second Bank Event", first.Aliases);
        Assert.DoesNotContain("First Bank Event", second.Aliases);
    }

    private static MediaEntry NumericMedia(uint id, string bankKey, string key, List<string> eventKeys) => new()
    {
        Key = key, Id = id, BankKey = bankKey, Name = id.ToString(), NameKind = NameKind.Stored,
        EventKeys = eventKeys
    };

    private static AudioEvent Event(string key, string name, EventBehavior behavior,
        List<NameEvidence>? names = null) => new()
    {
        Key = key, Name = name, Behavior = behavior, Names = names ?? []
    };
}
