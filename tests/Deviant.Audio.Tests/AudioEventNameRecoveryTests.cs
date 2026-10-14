using System.Text;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioEventNameRecoveryTests
{
    [Fact]
    public void RecoversFullAndUnderscoreSuffixIdentifiersWithTheirEvidence()
    {
        const string recoveredName = "Z_PlayDial";
        const string sourceText = "Container_Action_Z_PlayDial";
        const string inputSha = "fixture-sha";
        var id = Hash(recoveredName);
        var entry = Event(id);
        var catalog = new AudioCatalog
        {
            Events = [entry],
            Names = [new NameEvidence(sourceText, "SourceText", 0, NameKind.Stored,
                "synthetic metadata", 128, InputSha256: inputSha)]
        };

        AudioEventNameRecovery.Apply(catalog);

        Assert.Equal(recoveredName, entry.Name);
        var recovered = Assert.Single(entry.Names, name => name.Text == recoveredName);
        Assert.Equal("WwiseEvent", recovered.Namespace);
        Assert.Equal(id, recovered.Id);
        Assert.Equal(NameKind.Candidate, recovered.Kind);
        Assert.Equal("synthetic metadata", recovered.Source);
        Assert.Equal(128, recovered.Offset);
        Assert.Equal("Medium", recovered.Confidence);
        Assert.Equal("FNV-1 lowercase UTF-8", recovered.HashMethod);
        Assert.Equal(inputSha, recovered.InputSha256);
        Assert.Contains(catalog.Names, name => name == recovered);
    }

    [Fact]
    public void DirectEventEvidenceIsMatchedByNamespaceAndIdAndCollisionsStayUnresolved()
    {
        const uint id = 1234;
        var first = new NameEvidence("Candidate_One", "WwiseEvent", id, NameKind.Candidate, "dictionary-a");
        var second = new NameEvidence("Candidate_Two", "WwiseEvent", id, NameKind.Candidate, "dictionary-b");
        var entry = Event(id);
        var catalog = new AudioCatalog { Events = [entry], Names = [first, second] };

        AudioEventNameRecovery.Apply(catalog);

        Assert.Equal("1234", entry.Name);
        Assert.Contains(first, entry.Names);
        Assert.Contains(second, entry.Names);
        Assert.Equal(2, entry.Names.Count(name => name.Namespace == "WwiseEvent" && name.Id == id));
    }

    [Fact]
    public void PreservesMeaningfulStoredEventNameWhenUniqueHashCandidateExists()
    {
        const string recoveredName = "Recovered_Event";
        var id = Hash(recoveredName);
        var entry = Event(id, "Stored_Event");
        var catalog = new AudioCatalog
        {
            Events = [entry],
            Localization =
            [
                new LocalizedText("plain", "ENG", "Intro_Recovered_Event", "localization text", 96)
            ]
        };

        AudioEventNameRecovery.Apply(catalog);

        Assert.Equal("Stored_Event", entry.Name);
        Assert.Contains(entry.Names, name => name.Text == recoveredName && name.Source == "localization text" &&
            name.Offset == 96 && name.InputSha256 is null);
    }

    [Fact]
    public void CandidatePromotedNameReturnsToNumericWhenAdditionalCollidingNameAppears()
    {
        const uint id = 1234;
        var promoted = new NameEvidence("Candidate_One", "WwiseEvent", id, NameKind.Candidate, "exe strings");
        var collision = new NameEvidence("Candidate_Two", "WwiseEvent", id, NameKind.Candidate, "catalog strings");
        var entry = Event(id, "Candidate_One");
        entry.Names.Add(promoted);
        var catalog = new AudioCatalog { Events = [entry], Names = [promoted, collision] };

        AudioEventNameRecovery.Apply(catalog);

        Assert.Equal(id.ToString(), entry.Name);
        Assert.Contains(promoted, entry.Names);
        Assert.Contains(collision, entry.Names);
    }

    [Fact]
    public void CancellationStopsBeforeChangingEvents()
    {
        var entry = Event(Hash("Recovered_Event"));
        var catalog = new AudioCatalog
        {
            Events = [entry],
            Localization = [new LocalizedText("Action_Recovered_Event", "ENG", "", "localization", 64)]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AudioEventNameRecovery.Apply(catalog, cancellation.Token));
        Assert.Equal(entry.Id.ToString(), entry.Name);
        Assert.Empty(entry.Names);
        Assert.Empty(catalog.Names);
    }

    private static AudioEvent Event(uint id, string? name = null) => new()
    {
        Key = $"event:{id:X8}", Id = id, Name = name ?? id.ToString()
    };

    private static uint Hash(string value)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash = unchecked(hash * 16777619);
            hash ^= b;
        }
        return hash;
    }
}
