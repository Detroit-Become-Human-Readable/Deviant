using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationDiscoveryTests
{
    private const string IndexId = "synthetic-index";

    [Fact]
    public void GroupsExactStoredPrefixesWhileCountingDuplicateOccurrences()
    {
        var resource = new ResourceIdentity(IndexId, 12);
        var records = new[]
        {
            MakeRecord(resource, "UI_OPEN", "Open", "ENG", 0),
            MakeRecord(resource, "UI_OPEN", "Ouvrir", "FRE", 1, recordOffset: 12),
            MakeRecord(resource, "UI_SETTINGS_SECTION", null, "ENG", 2,
                LocalizationRecordKind.Pointer, LocalizationRecordStatus.Unresolved, recordOffset: 24),
            MakeRecord(resource, "MENU_START", "Start", "ENG", 3, recordOffset: 36)
        };

        var result = LocalizationDiscovery.Analyze(records, new LocalizationDiscoveryOptions(PrefixDepth: 1));

        Assert.Equal(4, result.Occurrences);
        var ui = Assert.Single(result.KeyPrefixes, group => group.StoredPrefix == "UI_");
        Assert.Equal(3, ui.Occurrences);
        Assert.Equal(2, ui.DistinctKeys);
        Assert.Equal(["ENG", "FRE"], ui.Languages);
        Assert.Equal(3, result.StatusCounts[nameof(LocalizationRecordStatus.Complete)]);
        Assert.Equal(1, result.StatusCounts[nameof(LocalizationRecordStatus.Unresolved)]);
    }

    [Fact]
    public void DetectsPlaceholderSpellingsAndOffsetsWithoutRewritingText()
    {
        var resource = new ResourceIdentity(IndexId, 3);
        const string text = "こんにちは {player}, %1$s and ${status}!";
        var record = MakeRecord(resource, "DIALOG_LINE_01", text, "JPN", 0);

        var result = LocalizationDiscovery.Analyze([record]);

        Assert.Equal(text, record.Text);
        Assert.Equal(3, result.PlaceholderOccurrences.Count);
        Assert.Contains(result.PlaceholderOccurrences, token => token.Token == "{player}" &&
            token.Syntax == LocalizationPlaceholderSyntax.Braces && token.Utf16Offset == text.IndexOf("{player}", StringComparison.Ordinal));
        Assert.Contains(result.PlaceholderOccurrences, token => token.Token == "%1$s" &&
            token.Syntax == LocalizationPlaceholderSyntax.Printf && token.Utf16Offset == text.IndexOf("%1$s", StringComparison.Ordinal));
        Assert.Contains(result.PlaceholderOccurrences, token => token.Token == "${status}" &&
            token.Syntax == LocalizationPlaceholderSyntax.DollarBraces && token.Utf16Offset == text.IndexOf("${status}", StringComparison.Ordinal));
        Assert.All(result.PlaceholderOccurrences, token => Assert.Equal(record.Identity, token.Identity));
    }

    [Fact]
    public void StreamingAccumulatorSnapshotsIncrementallyAndSelectedRecordPlaceholderLookupIsBounded()
    {
        var resource = new ResourceIdentity(IndexId, 4);
        var first = MakeRecord(resource, "UI_FIRST", "{one} and %s", "ENG", 0);
        var second = MakeRecord(resource, "UI_SECOND", "${two}", "FRE", 1, recordOffset: 16);
        var accumulator = new LocalizationDiscovery.StreamingAccumulator(
            new LocalizationDiscoveryOptions(MaximumPlaceholderOccurrences: 1));
        accumulator.Add(first);
        var beforeSecond = accumulator.Snapshot();
        accumulator.Add(second);
        var afterSecond = accumulator.Snapshot();

        Assert.Equal(1, beforeSecond.Occurrences);
        Assert.Equal(2, afterSecond.Occurrences);
        Assert.Equal(3, afterSecond.PlaceholderSummary.Sum(item => item.Occurrences));
        Assert.Single(afterSecond.PlaceholderOccurrences);
        Assert.Equal(2, afterSecond.OmittedPlaceholderOccurrences);

        var selectedRecordTokens = LocalizationDiscovery.InspectPlaceholders(first, maximumTokens: 1);
        var selectedToken = Assert.Single(selectedRecordTokens);
        Assert.Equal("{one}", selectedToken.Token);
        Assert.Equal(first.Text!.IndexOf("{one}", StringComparison.Ordinal), selectedToken.Utf16Offset);
    }

    [Fact]
    public void LinksOnlyVerifiedUniqueDirectType1016References()
    {
        var owner = new ResourceIdentity(IndexId, 9);
        var target = new ResourceIdentity(IndexId, 20);
        var localized = MakeRecord(target, "UI_OK", "OK", "ENG", 0);
        var records = new[] { localized };
        var relationships = new[]
        {
            Relationship(owner, 20, 1016, ReferenceStatus.Unique, [target], verified: true),
            Relationship(owner, 21, 1016, ReferenceStatus.Missing, [], verified: true),
            Relationship(owner, 22, 1016, ReferenceStatus.Ambiguous, [new(IndexId, 22), new(IndexId, 23)], verified: true),
            Relationship(owner, 24, 1016, ReferenceStatus.Unique, [new(IndexId, 24)], verified: false),
            Relationship(owner, 25, 1016, ReferenceStatus.Unique, [new(IndexId, 25)], verified: true),
            Relationship(owner, 20, 1033, ReferenceStatus.Unique, [target], verified: true)
        };

        var links = LocalizationDiscovery.ResolveDirectReferences(records, relationships);

        Assert.Equal(5, links.Count);
        Assert.Equal(LocalizationReferenceLinkStatus.Linked, links[0].Status);
        Assert.Same(localized, Assert.Single(links[0].Matches));
        Assert.Equal(LocalizationReferenceLinkStatus.Unmatched, links[1].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Ambiguous, links[2].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Unverified, links[3].Status);

        Assert.Equal(LocalizationReferenceLinkStatus.TargetResourceNotSampled, links[4].Status);
    }

    [Fact]
    public void FollowsVerifiedResourceLevelDialogueAndAudioReferencesWithoutLinkingIndividualKeys()
    {
        var localized = new ResourceIdentity(IndexId, 30);
        var header = new ResourceIdentity(IndexId, 31);
        var audio = new ResourceIdentity(IndexId, 32);
        var dialogueEvent = new ResourceIdentity(IndexId, 33);
        var ambiguousHeader = new ResourceIdentity(IndexId, 34);
        var missingSource = new ResourceIdentity(IndexId, 35);
        var relationships = new[]
        {
            Relationship(localized, 31, 4091, ReferenceStatus.Unique, [header], verified: true),
            Relationship(header, 32, 1033, ReferenceStatus.Unique, [audio], verified: true),
            Relationship(header, 33, 1031, ReferenceStatus.Unique, [dialogueEvent], verified: true),
            Relationship(ambiguousHeader, 36, 4091, ReferenceStatus.Ambiguous, [header, new(IndexId, 36)], verified: true)
        };

        var targets = LocalizationDiscovery.ResolveDialogueAudioTargets(
            [localized, ambiguousHeader, missingSource], relationships, [header]);

        Assert.Equal(4, targets.Count);
        Assert.Equal(LocalizationReferenceLinkStatus.Linked, targets[0].Status);
        Assert.Equal((uint)1033, targets[0].TargetResourceType);
        Assert.Equal(audio, targets[0].TargetResource);
        Assert.Equal(2, targets[0].EvidencePath.Count);
        Assert.Equal(LocalizationReferenceLinkStatus.Linked, targets[1].Status);
        Assert.Equal((uint)1031, targets[1].TargetResourceType);
        Assert.Equal(dialogueEvent, targets[1].TargetResource);
        Assert.Equal(LocalizationReferenceLinkStatus.Ambiguous, targets[2].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Unmatched, targets[3].Status);
    }

    [Fact]
    public void ResolvesPointerKindOneOnlyAgainstMatchingVerifiedNineByteSourceEdges()
    {
        var resource = new ResourceIdentity(IndexId, 40);
        var uniqueTarget = new ResourceIdentity(IndexId, 41);
        var ambiguousTargetA = new ResourceIdentity(IndexId, 42);
        var ambiguousTargetB = new ResourceIdentity(IndexId, 43);
        var records = new[]
        {
            MakePointerRecord(resource, "PTR_UNIQUE", 4091, 41, 0xA5, 0),
            MakePointerRecord(resource, "PTR_MISSING", 4091, 44, 0xA5, 8),
            MakePointerRecord(resource, "PTR_AMBIGUOUS", 4091, 45, 0xA5, 16),
            MakePointerRecord(resource, "PTR_UNVERIFIED", 4091, 46, 0xA5, 24),
            MakePointerRecord(resource, "PTR_TRAILER_MISMATCH", 4091, 47, 0xA5, 32)
        };
        var edges = new[]
        {
            Relationship(resource, 41, 4091, ReferenceStatus.Unique, [uniqueTarget], verified: true),
            Relationship(resource, 44, 4091, ReferenceStatus.Missing, [], verified: true),
            Relationship(resource, 45, 4091, ReferenceStatus.Ambiguous, [ambiguousTargetA, ambiguousTargetB], verified: true),
            Relationship(resource, 46, 4091, ReferenceStatus.Unique, [new(IndexId, 46)], verified: false),
            Relationship(resource, 47, 4091, ReferenceStatus.Unique, [new(IndexId, 47)], verified: true, sourceReferenceTrailer: 0xA4)
        };

        var links = LocalizationDiscovery.ResolvePointerReferences(records, edges);

        Assert.Equal(5, links.Count);
        Assert.Equal(LocalizationReferenceLinkStatus.Linked, links[0].Status);
        Assert.Equal((byte)0xA5, links[0].PayloadPrefix);
        Assert.Equal(uniqueTarget, links[0].TargetResource);
        Assert.Equal(LocalizationReferenceLinkStatus.Unmatched, links[1].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Ambiguous, links[2].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Unverified, links[3].Status);
        Assert.Equal(LocalizationReferenceLinkStatus.Unmatched, links[4].Status);

        var audio = new ResourceIdentity(IndexId, 50);
        var dialogueEvent = new ResourceIdentity(IndexId, 51);
        var related = LocalizationDiscovery.ResolvePointerDialogueAudioTargets([links[0]],
            [Relationship(uniqueTarget, 50, 1033, ReferenceStatus.Unique, [audio], verified: true),
             Relationship(uniqueTarget, 51, 1031, ReferenceStatus.Unique, [dialogueEvent], verified: true)],
            [uniqueTarget]);
        Assert.Equal(2, related.Count);
        Assert.All(related, target => Assert.Equal(LocalizationReferenceLinkStatus.Linked, target.Status));
        Assert.Contains(related, target => target.TargetResourceType == 1033 && target.TargetResource == audio && target.EvidencePath.Count == 2);
        Assert.Contains(related, target => target.TargetResourceType == 1031 && target.TargetResource == dialogueEvent && target.EvidencePath.Count == 2);
    }

    private static LocalizationRecord MakeRecord(ResourceIdentity resource, string key, string? text, string language,
        int keyIndex, LocalizationRecordKind kind = LocalizationRecordKind.Text,
        LocalizationRecordStatus status = LocalizationRecordStatus.Complete, long recordOffset = 0)
    {
        var identity = new ResourceIdentity(resource.IndexContentId, resource.Ordinal,
            $"loc/v6/l0/r{recordOffset:D12}/k{keyIndex:D4}/{kind.ToString().ToLowerInvariant()}");
        return new(identity, resource, resource.Ordinal, 1016, 6, 0, language, language, recordOffset,
            keyIndex, kind, status, key, text, "UTF-16LE",
            new LocalizationExtent("Record", "synthetic.dat", recordOffset, text?.Length ?? 0),
            Array.Empty<LocalizationExtent>(), new Dictionary<string, string>());
    }

    private static AssetRelationship Relationship(ResourceIdentity owner, uint id, uint resourceType,
        ReferenceStatus status, IReadOnlyList<ResourceIdentity> candidates, bool verified,
        byte sourceReferenceTrailer = 0xA5)
    {
        var reference = new ResourceReference(IdentifierNamespace.Archive, id.ToString(), owner, resourceType);
        var kind = resourceType switch
        {
            1016 => AssetRelationshipKind.Localization,
            1031 => AssetRelationshipKind.Event,
            1033 => AssetRelationshipKind.Media,
            4091 => AssetRelationshipKind.Container,
            _ => AssetRelationshipKind.Dependency
        };
        return new(owner, kind,
            "COM_CONT reference", reference, new(status, candidates),
            new(verified, "synthetic COM_CONT", "Explicit type and identifier reference.",
                new SourceExtent("synthetic.dat", 0, 9), SourceReferenceTrailer: sourceReferenceTrailer));
    }

    private static LocalizationRecord MakePointerRecord(ResourceIdentity resource, string key, uint targetType,
        uint targetId, byte prefix, long recordOffset)
    {
        var record = MakeRecord(resource, key, null, "ENG", 0, LocalizationRecordKind.Pointer,
            LocalizationRecordStatus.Unresolved, recordOffset);
        var payload = new byte[9];
        payload[0] = prefix;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), targetType);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5, 4), targetId);
        return record with { PointerKind = 1, PointerPayloadBase64 = Convert.ToBase64String(payload) };
    }
}
