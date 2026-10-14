using ResourceReference = Deviant.Assets.ResourceReference;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class AudioRelationshipPresentationTests
{
    [Fact]
    public void UniqueTargetUsesExactIdentityWhenCatalogEventsShareNumericId()
    {
        var media = Identity(1);
        var dialogueEvent = Identity(2);
        var otherBankEvent = Identity(3);
        const uint sharedId = 0x1234;
        var relationship = Relationship(media, AssetRelationshipKind.Event, ReferenceStatus.Unique, [dialogueEvent], "dialogue-key");
        var events = new[]
        {
            Event(otherBankEvent, sharedId, "Other bank event"),
            Event(dialogueEvent, sharedId, "Generated dialogue event")
        };

        var targetBefore = relationship.NavigationTarget;
        var label = AudioRelationshipPresentation.EventLabel(relationship, events);

        Assert.Equal("Generated dialogue event", label);
        Assert.Equal(dialogueEvent, targetBefore);
        Assert.Equal(targetBefore, relationship.NavigationTarget);
        Assert.NotEqual(otherBankEvent, relationship.NavigationTarget);
    }

    [Fact]
    public void AmbiguousCandidatesKeepEveryNameAndDoNotCreateNavigationTarget()
    {
        var media = Identity(1);
        var candidateA = Identity(2);
        var candidateB = Identity(3);
        const uint sharedId = 0x2345;
        var relationship = Relationship(media, AssetRelationshipKind.Event, ReferenceStatus.Ambiguous,
            [candidateA, candidateB], "ambiguous-dialogue-key");
        var events = new[]
        {
            Event(candidateB, sharedId, "Generated candidate B"),
            Event(candidateA, sharedId, "Generated candidate A")
        };

        var label = AudioRelationshipPresentation.EventLabel(relationship, events);

        Assert.NotNull(label);
        Assert.Contains("Generated candidate A", label, StringComparison.Ordinal);
        Assert.Contains("Generated candidate B", label, StringComparison.Ordinal);
        Assert.Contains("Ambiguous", label, StringComparison.Ordinal);
        Assert.Null(relationship.NavigationTarget);
    }

    [Fact]
    public void HircActionLabelUsesItsSourceEventAndPreservesActionNavigationTarget()
    {
        var eventIdentity = new ResourceIdentity("generated-index", 4, "hirc/00000041/0");
        var actionIdentity = new ResourceIdentity("generated-index", 4, "hirc/00000052/0");
        var relationship = Relationship(eventIdentity, AssetRelationshipKind.Event, ReferenceStatus.Unique,
            [actionIdentity], "00000052", IdentifierNamespace.Hirc);
        var events = new[] { Event(eventIdentity, 0x41, "Generated_Play_Event") };

        var label = AudioRelationshipPresentation.EventLabel(relationship, events);

        Assert.Equal("Generated_Play_Event · Action", label);
        Assert.Equal(actionIdentity, relationship.NavigationTarget);
    }

    [Fact]
    public void UnnamedNumericEventUsesHexFallbackAndNonEventRelationshipReturnsNull()
    {
        var source = Identity(1);
        var eventIdentity = Identity(2);
        const uint eventId = 0x2A;
        var eventRelationship = Relationship(source, AssetRelationshipKind.Event, ReferenceStatus.Unique,
            [eventIdentity], "42");
        var unnamedEvent = Event(eventIdentity, eventId, eventId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal("Event 0x0000002A", AudioRelationshipPresentation.EventLabel(eventRelationship, [unnamedEvent]));

        var mediaRelationship = Relationship(source, AssetRelationshipKind.Media, ReferenceStatus.Unique,
            [eventIdentity], "42");
        Assert.Null(AudioRelationshipPresentation.EventLabel(mediaRelationship, [unnamedEvent]));
    }

    private static AudioEvent Event(ResourceIdentity identity, uint id, string name) => new()
    {
        Identity = identity,
        Id = id,
        Name = name,
        Key = $"event:{identity.Key}",
        BankKey = "generated-bank"
    };

    private static AssetRelationship Relationship(ResourceIdentity source, AssetRelationshipKind kind,
        ReferenceStatus status, IReadOnlyList<ResourceIdentity> candidates, string referenceId,
        IdentifierNamespace referenceNamespace = IdentifierNamespace.Archive) => new(
        source,
        kind,
        "Generated relationship",
        new ResourceReference(referenceNamespace, referenceId, source),
        new ReferenceResolution(status, candidates),
        new RelationshipEvidence(true, "Generated fixture", "Generated relationship evidence."));

    private static ResourceIdentity Identity(long ordinal) => new("generated-index", ordinal);
}
