using Deviant.Assets;
using Xunit;

namespace Deviant.Tests;

public sealed class TypeRegistryValidationTests
{
    [Fact]
    public void NumericNamespacesAndSerializedVersionsStayDistinct()
    {
        var keys = new[]
        {
            new TypeKey(TypeDomain.Archive, 17),
            new TypeKey(TypeDomain.Native, 17),
            new TypeKey(TypeDomain.SerializedClass, null, "EXAMPLE ", 1),
            new TypeKey(TypeDomain.SerializedClass, null, "EXAMPLE ", 2)
        };
        var registry = new TypeRegistry(keys.Select(key => Definition(key)));
        var entries = registry.Reconcile(keys.Select((key, index) => Observation(key, index + 1)));

        Assert.Equal(4, entries.Count);
        foreach (var key in keys)
            Assert.Equal(Array.IndexOf(keys, key) + 1, Assert.Single(entries, entry => entry.Type == key).Count);
        Assert.Equal(10, entries.Sum(entry => entry.Count));
        Assert.Equal(4, entries.Select(entry => entry.Type.Key).Distinct().Count());
    }

    [Fact]
    public void RegisteredWithoutOccurrencesAndUnknownObservedTypesRemainSeparate()
    {
        var known = new TypeKey(TypeDomain.Native, 17);
        var unknown = new TypeKey(TypeDomain.Native, 999);
        var registry = new TypeRegistry([Definition(known) with { Registered = true }]);
        var entries = registry.Reconcile([Observation(unknown, 3)]);

        var registration = Assert.Single(entries, entry => entry.Type == known);
        Assert.Equal("Registered", registration.Presence);
        Assert.Equal(0, registration.Count);
        Assert.Empty(registration.Samples);
        var observed = Assert.Single(entries, entry => entry.Type == unknown);
        Assert.Equal("Observed", observed.Presence);
        Assert.Equal("Unclassified", observed.Category);
        Assert.Equal("Unknown", observed.Definition.Support.Properties);
        Assert.Equal("Unknown", observed.Definition.Support.Placement);
        Assert.Equal(3, Assert.Single(TypeRegistry.Search(entries, observedOnly: true)).Count);
    }

    [Fact]
    public void CandidateClassHeadersDoNotAcquireSemanticOrPlacementSupport()
    {
        var candidate = new TypeKey(TypeDomain.SerializedClass, null, "EXAMPLE ", 7);
        var registry = new TypeRegistry([]);
        var entries = registry.Reconcile([Observation(candidate, 4) with { ValidatedHeader = false }]);

        var entry = Assert.Single(entries);
        Assert.Equal(4, entry.Count);
        Assert.Empty(entry.Definition.Evidence);
        Assert.Empty(entry.Definition.Fields);
        Assert.Equal("Unknown", entry.Definition.Support.Structure);
        Assert.Equal("Unknown", entry.Definition.Support.References);
        Assert.Equal("Unknown", entry.Definition.Support.Placement);
        Assert.Equal("None", entry.Definition.Support.Visualization);
        Assert.NotEmpty(entry.Definition.Questions);
    }

    [Fact]
    public void ReconciliationKeepsDistinctNestedSourceIdentitiesAndBoundsRepresentatives()
    {
        var key = new TypeKey(TypeDomain.Native, 17);
        var first = new ResourceIdentity("generated-index", 1, "dc-object/0/17/4");
        var second = new ResourceIdentity("generated-index", 2, "dc-object/0/17/4");
        var additional = Enumerable.Range(3, 110).Select(ordinal => new ResourceIdentity("generated-index", ordinal, "dc-object/0/17/4")).ToArray();
        var entry = Assert.Single(new TypeRegistry([]).Reconcile([
            new(key, 1, 1, [first]), new(key, 1, 1, [second, first]), new(key, 110, 110, additional)
        ]));

        Assert.Equal(112, entry.Count);
        Assert.Equal(112, entry.ContainerCount);
        Assert.Equal(100, entry.Samples.Count);
        Assert.Contains(first, entry.Samples);
        Assert.Contains(second, entry.Samples);
        Assert.Equal(100, entry.Samples.Distinct().Count());
    }

    [Fact]
    public void ConflictingNamesAndQuestionsRemainSearchableWithoutNamespaceBleed()
    {
        var key = new TypeKey(TypeDomain.Native, 17);
        var first = Definition(key) with { Name = "First name", Questions = ["First question"] };
        var second = Definition(key) with { Name = "Second name", Questions = ["Second question"],
            Evidence = [new("generated-consumer", "Read a serialized count", EvidenceLevel.ConsumerConfirmed)] };
        var registry = new TypeRegistry([first, second, Definition(new(TypeDomain.Archive, 17))]);
        var entries = registry.Reconcile([Observation(key, 2)]);

        var match = Assert.Single(TypeRegistry.Search(entries, "SECOND NAME", TypeDomain.Native, "generated", true));
        Assert.Equal(key, match.Type);
        Assert.Contains("First name", match.Definition.CompetingNames!);
        Assert.Contains("Second name", match.Definition.CompetingNames!);
        Assert.Equal(2, match.Definition.Questions.Count);
        Assert.Single(match.Definition.Evidence);
        Assert.Empty(TypeRegistry.Search(entries, "Second name", TypeDomain.Archive));
        Assert.Empty(TypeRegistry.Search(entries, "Second absent"));
    }

    [Fact]
    public void NegativeOccurrenceCannotBeHiddenByAnotherObservation()
    {
        var key = new TypeKey(TypeDomain.Native, 17);
        var registry = new TypeRegistry([]);
        Assert.Throws<ArgumentException>(() => registry.Reconcile([Observation(key, -1), Observation(key, 2)]));
        Assert.Throws<ArgumentException>(() => registry.Reconcile([
            Observation(key, 1) with { ContainerCount = -1 }, Observation(key, 1) with { ContainerCount = 2 }
        ]));
    }

    [Fact]
    public void MultipleProfilesKeepRegistrationFieldsAndSupportDisagreement()
    {
        var key = new TypeKey(TypeDomain.Native, 17);
        var first = Definition(key) with { Fields = [new("Header", "u32", "0", null, EvidenceLevel.ObservedStructure)] };
        var later = Definition(key) with { Registered = true,
            Fields = [new("Reference", "typed-id", "4", null, EvidenceLevel.ConsumerConfirmed)],
            Support = new(Structure: "Bounded profile", References: "Typed references") };
        var entry = Assert.Single(new TypeRegistry([first, later]).Reconcile([]));

        Assert.Equal("Registered", entry.Presence);
        Assert.Equal(2, entry.Definition.Fields.Count);
        Assert.Equal("Mixed profiles", entry.Definition.Support.Structure);
        Assert.Equal("Mixed profiles", entry.Definition.Support.References);
        Assert.Equal("Unknown", entry.Definition.Support.Placement);
        Assert.Equal(0, entry.Count);
    }

    private static TypeDefinition Definition(TypeKey key) =>
        new(key, "Generated type", "Generated", [], [], new(), []);
    private static TypeObservation Observation(TypeKey key, long count) =>
        new(key, count, count, [new("generated-index", 0)]);
}
