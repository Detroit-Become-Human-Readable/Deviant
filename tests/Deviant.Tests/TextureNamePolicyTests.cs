using Deviant.Assets;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class TextureNamePolicyTests
{
    private static readonly ResourceIdentity Texture = new(new string('a', 64), 10);
    private static readonly ResourceIdentity Container = Texture with { Ordinal = 99 };
    private static readonly SourceRevision Revision = new(Texture.IndexContentId, "revision-a");

    [Fact]
    public void RecoveredNameWinsOverPathOwnerAndInferenceWhileRetainingPath()
    {
        var candidates = new[]
        {
            Candidate("Suggested surface", TextureNameKind.InferredDescription),
            Candidate("Prop · Color", TextureNameKind.VerifiedOwnerRole, owner: Texture with { Ordinal = 20 }, role: "Color"),
            Candidate("assets/surfaces/stone.dds", TextureNameKind.OriginalPath),
            Candidate("Stone surface", TextureNameKind.OriginalName)
        };

        var decision = Decide(candidates);

        Assert.Equal("Stone surface", decision.Label);
        Assert.Equal(TextureNameKind.OriginalName, decision.Kind);
        Assert.Equal("Stone surface", decision.OriginalName);
        Assert.Equal("assets/surfaces/stone.dds", decision.OriginalPath);
        Assert.Empty(decision.Conflicts);
    }

    [Theory]
    [InlineData("assets/surfaces/stone.dds")]
    [InlineData("assets\\surfaces\\stone.dds")]
    public void RecoveredPathUsesBasenameAndExportOmitsSourceExtension(string path)
    {
        var decision = Decide([Candidate(path, TextureNameKind.OriginalPath)]);

        Assert.Equal("stone.dds", decision.Label);
        Assert.Equal(path, decision.OriginalPath);
        Assert.Null(decision.OriginalName);
        Assert.Equal(TextureNameKind.OriginalPath, decision.Kind);
        Assert.StartsWith("stone-", decision.ExportStem);
        Assert.DoesNotContain(".dds", decision.ExportStem);
    }

    [Theory]
    [InlineData(TextureNameKind.OriginalName)]
    [InlineData(TextureNameKind.OriginalPath)]
    public void ContradictoryOriginalCandidatesUseFallbackAndRetainBothConflicts(TextureNameKind kind)
    {
        var first = Candidate("First candidate", kind);
        var second = Candidate("Second candidate", kind);
        var forward = Decide([first, second]);
        var reverse = Decide([second, first]);

        Assert.Equal(TextureNameKind.Fallback, forward.Kind);
        Assert.Equal("Texture 0000002A", forward.Label);
        Assert.Equal(new[] { "First candidate", "Second candidate" }, forward.Conflicts);
        Assert.Null(forward.OriginalName);
        Assert.Null(forward.OriginalPath);
        Assert.Equal(forward.Label, reverse.Label);
        Assert.Equal(forward.ExportStem, reverse.ExportStem);
        Assert.Equal(forward.Conflicts, reverse.Conflicts);
    }

    [Fact]
    public void ConflictingNamesDoNotPromoteLowerRankCandidate()
    {
        var decision = Decide([
            Candidate("Surface A", TextureNameKind.OriginalName),
            Candidate("Surface B", TextureNameKind.OriginalName),
            Candidate("assets/surface.dds", TextureNameKind.OriginalPath),
            Candidate("Prop · Color", TextureNameKind.VerifiedOwnerRole, owner: Texture with { Ordinal = 20 }, role: "Color")
        ]);

        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Equal(new[] { "Surface A", "Surface B" }, decision.Conflicts);
        Assert.Equal("assets/surface.dds", decision.OriginalPath);
    }

    [Fact]
    public void UniqueNameKeepsContradictoryPathEvidenceAsConflicts()
    {
        var decision = Decide([
            Candidate("Surface", TextureNameKind.OriginalName),
            Candidate("assets/a.dds", TextureNameKind.OriginalPath),
            Candidate("assets/b.dds", TextureNameKind.OriginalPath)
        ]);

        Assert.Equal("Surface", decision.OriginalName);
        Assert.Null(decision.OriginalPath);
        Assert.Equal(new[] { "assets/a.dds", "assets/b.dds" }, decision.Conflicts);
    }

    [Fact]
    public void RepeatedOriginalTextIsNotAConflictAndPreservesEveryEvidenceSource()
    {
        var first = Evidence(Container);
        var second = Evidence(Container with { Ordinal = 100 });
        var candidates = new[]
        {
            Candidate("Surface", TextureNameKind.OriginalName, [first]),
            Candidate("Surface", TextureNameKind.OriginalName, [second])
        };

        var decision = Decide(candidates);

        Assert.Equal("Surface", decision.Label);
        Assert.Empty(decision.Conflicts);
        Assert.Same(first, candidates[0].Evidence.Single());
        Assert.Same(second, candidates[1].Evidence.Single());
    }

    [Fact]
    public void VerifiedOwnersAllContributeWithoutPromotingRoleToOriginalName()
    {
        var candidates = new[]
        {
            Candidate("Prop B · Normal", TextureNameKind.VerifiedOwnerRole, owner: Texture with { Ordinal = 21 }, role: "Normal"),
            Candidate("Prop A · Color", TextureNameKind.VerifiedOwnerRole, owner: Texture with { Ordinal = 20 }, role: "Color")
        };
        var decision = Decide(candidates);

        Assert.Equal("Prop A · Color / Prop B · Normal", decision.Label);
        Assert.Equal(TextureNameKind.VerifiedOwnerRole, decision.Kind);
        Assert.Null(decision.OriginalName);
        Assert.Null(decision.OriginalPath);
        Assert.Equal(new long[] { 21, 20 }, candidates.Select(item => item.Owner!.Ordinal));
        Assert.Equal(new[] { "Normal", "Color" }, candidates.Select(item => item.Role));
        Assert.Equal(decision.Label, Decide(candidates.Reverse().ToArray()).Label);
    }

    [Fact]
    public void InferredDescriptionRetainsItsClassification()
    {
        var decision = Decide([Candidate("Possible fabric surface", TextureNameKind.InferredDescription)]);

        Assert.Equal(TextureNameKind.InferredDescription, decision.Kind);
        Assert.Null(decision.OriginalName);
        Assert.Null(decision.OriginalPath);
        Assert.Empty(decision.Conflicts);
    }

    [Fact]
    public void FallbackKeepsResourceIdentityWithoutRepeatingMetadata()
    {
        var decision = Decide([]);
        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Equal("Texture 0000002A", decision.Label);
        Assert.NotEqual(decision.Label, Decide([], resourceId: 43).Label);
        Assert.Equal(decision.Label, Decide([], width: 256).Label);
        Assert.Equal(decision.Label, Decide([], format: "BC7").Label);
        Assert.NotEqual(decision.ExportStem, Decide([], identity: Texture with { Ordinal = 11 }).ExportStem);
    }

    [Fact]
    public void MissingDimensionsAndFormatDoNotInventMetadata()
    {
        var decision = TextureNamePolicy.Decide(Texture, 7, 42, null, null, null, []);
        Assert.Equal("Texture 0000002A", decision.Label);
        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvidenceFromAnotherIndexCannotAssignAName(bool differentSource)
    {
        var evidence = Evidence(Container);
        evidence = differentSource
            ? evidence with { Source = Container with { IndexContentId = new string('b', 64) } }
            : evidence with { Revision = Revision with { IndexContentId = new string('b', 64) } };

        var decision = Decide([Candidate("Foreign name", TextureNameKind.OriginalName, [evidence])]);

        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Null(decision.OriginalName);
    }

    [Fact]
    public void EveryEvidenceItemMustBelongToTheSelectedIndex()
    {
        var foreign = Evidence(Container with { IndexContentId = new string('b', 64) });
        var decision = Decide([Candidate("Mixed evidence", TextureNameKind.OriginalName, [Evidence(Container), foreign])]);

        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Null(decision.OriginalName);
    }

    [Fact]
    public void EmptyEvidenceCannotPromoteAName()
    {
        var decision = Decide([Candidate("Unbound name", TextureNameKind.OriginalName, [])]);
        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Null(decision.OriginalName);
    }

    [Fact]
    public void DecodedEvidenceContainerMustMatchItsSourceIdentity()
    {
        var evidence = Evidence(Container) with { DecodedExtent = new(Container with { Ordinal = 100 }, 20, 12) };
        var decision = Decide([Candidate("Mismatched source", TextureNameKind.OriginalName, [evidence])]);

        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
        Assert.Null(decision.OriginalName);
    }

    [Fact]
    public void VerifiedOwnerMustBelongToTheSelectedIndex()
    {
        var foreignOwner = new ResourceIdentity(new string('b', 64), 20);
        var decision = Decide([Candidate("Foreign owner · Color", TextureNameKind.VerifiedOwnerRole, owner: foreignOwner, role: "Color")]);

        Assert.Equal(TextureNameKind.Fallback, decision.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifiedOwnerRoleRequiresBothOwnerAndRole(bool missingOwner)
    {
        var candidate = Candidate("Prop · Color", TextureNameKind.VerifiedOwnerRole,
            owner: missingOwner ? null : Texture with { Ordinal = 20 }, role: missingOwner ? "Color" : null);

        Assert.Equal(TextureNameKind.Fallback, Decide([candidate]).Kind);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("reader")]
    [InlineData("field")]
    [InlineData("decoded-offset")]
    [InlineData("physical-length")]
    public void InvalidProvenanceCannotPromoteCandidate(string invalidPart)
    {
        var evidence = Evidence(Container);
        evidence = invalidPart switch
        {
            "digest" => evidence with { SourceSha256 = new string('z', 64) },
            "reader" => evidence with { Reader = "" },
            "field" => evidence with { Field = " " },
            "decoded-offset" => evidence with { DecodedExtent = evidence.DecodedExtent! with { Offset = -1 } },
            "physical-length" => evidence with { PhysicalExtent = evidence.PhysicalExtent! with { Length = -1 } },
            _ => throw new ArgumentException(nameof(invalidPart))
        };

        Assert.Equal(TextureNameKind.Fallback, Decide([Candidate("Surface", TextureNameKind.OriginalName, [evidence])]).Kind);
    }

    [Fact]
    public async Task UserAliasRemainsSeparateFromRecoveredCandidates()
    {
        using var fixture = new ArchiveFixture();
        var library = Path.Combine(fixture.Root, "library");
        var candidates = new[] { Candidate("Recovered surface", TextureNameKind.OriginalName) };
        var before = Decide(candidates);

        await new AliasStore(library).SetAsync(Texture, "My surface");
        var reopened = new AliasStore(library);
        var after = Decide(candidates);

        Assert.Equal("My surface", reopened.Get(Texture));
        Assert.Equal(before, after);
        Assert.Equal("Recovered surface", candidates.Single().Text);
        Assert.Equal(TextureNameKind.OriginalName, candidates.Single().Kind);
        Assert.Null(reopened.Get(Texture with { Ordinal = 11 }));
        Assert.Null(reopened.Get(Texture with { IndexContentId = new string('b', 64) }));
        await reopened.SetAsync(Texture, null);
        Assert.Null(new AliasStore(library).Get(Texture));
        Assert.Equal("Recovered surface", Decide(candidates).Label);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("prn.txt")]
    [InlineData("AUX")]
    [InlineData("nul.dds")]
    [InlineData("COM1")]
    [InlineData("com9.dds")]
    [InlineData("LPT1")]
    [InlineData("lpt9.dds")]
    public void ReservedWindowsDeviceNamesProduceUsableStems(string name)
    {
        var decision = Decide([Candidate(name, TextureNameKind.OriginalName)]);
        Assert.StartsWith("asset_", decision.ExportStem);
        Assert.DoesNotContain(decision.ExportStem, character => Path.GetInvalidFileNameChars().Contains(character));
    }

    [Fact]
    public void PathSeparatorsControlsAndPunctuationCannotEscapeExportDirectory()
    {
        var decision = Decide([Candidate("../folder\\surface:<a>|?*\u0001", TextureNameKind.OriginalName)]);

        Assert.DoesNotContain(decision.ExportStem, character => "<>:\"/\\|?*".Contains(character) || char.IsControl(character));
        Assert.False(Path.IsPathRooted(decision.ExportStem));
        Assert.Equal(decision.ExportStem, Path.GetFileName(decision.ExportStem));
        Assert.Equal(decision.ExportStem, Decide([Candidate("../folder\\surface:<a>|?*\u0001", TextureNameKind.OriginalName)]).ExportStem);
    }

    [Fact]
    public void DuplicateNamesRemainDistinctAcrossTypeIdOrdinalMemberAndIndex()
    {
        var candidates = new[] { Candidate("Shared surface", TextureNameKind.OriginalName) };
        var baseline = Decide(candidates).ExportStem;
        Assert.NotEqual(baseline, Decide(candidates, resourceType: 8).ExportStem);
        Assert.NotEqual(baseline, Decide(candidates, resourceId: 43).ExportStem);
        Assert.NotEqual(baseline, Decide(candidates, identity: Texture with { Ordinal = 11 }).ExportStem);
        Assert.NotEqual(baseline, Decide(candidates, identity: Texture with { MemberPath = "member/one" }).ExportStem);
        Assert.NotEqual(
            Decide(candidates, identity: Texture with { MemberPath = "member/one" }).ExportStem,
            Decide(candidates, identity: Texture with { MemberPath = "member/two" }).ExportStem);

        var nextIndex = new ResourceIdentity(new string('b', 64), Texture.Ordinal);
        Assert.NotEqual(baseline, Decide([Candidate("Shared surface", TextureNameKind.OriginalName, [Evidence(nextIndex with { Ordinal = 99 })])], identity: nextIndex).ExportStem);
    }

    [Fact]
    public void IndexDigestsSharingTheSamePrefixCannotCollideOnExport()
    {
        var first = new ResourceIdentity("12345678" + new string('a', 56), 10);
        var second = new ResourceIdentity("12345678" + new string('b', 56), 10);
        var firstStem = Decide([Candidate("Surface", TextureNameKind.OriginalName, [Evidence(first with { Ordinal = 99 })])], identity: first).ExportStem;
        var secondStem = Decide([Candidate("Surface", TextureNameKind.OriginalName, [Evidence(second with { Ordinal = 99 })])], identity: second).ExportStem;

        Assert.NotEqual(firstStem, secondStem);
    }

    [Fact]
    public void SanitizedAndTruncatedLabelsStillHaveDistinctIdentitySuffixes()
    {
        var first = Decide([Candidate("a/b", TextureNameKind.OriginalName)]);
        var second = Decide([Candidate("a\\b", TextureNameKind.OriginalName)], identity: Texture with { Ordinal = 11 });
        Assert.NotEqual(first.ExportStem, second.ExportStem);
        var longName = new string('x', 160);
        var longFirst = Decide([Candidate(longName + "a", TextureNameKind.OriginalName)]);
        var longSecond = Decide([Candidate(longName + "b", TextureNameKind.OriginalName)], identity: Texture with { Ordinal = 11 });
        Assert.NotEqual(longFirst.ExportStem, longSecond.ExportStem);
        Assert.True(longFirst.ExportStem.Length < 255);
    }

    [Fact]
    public void NestedIdentityExportStemLeavesRoomForExtensionsAndReports()
    {
        var nested = Texture with { Ordinal = long.MaxValue, MemberPath = new string('x', 512) };
        var decision = Decide([Candidate(new string('s', 160), TextureNameKind.OriginalName)],
            identity: nested, resourceType: uint.MaxValue, resourceId: uint.MaxValue);

        Assert.True(decision.ExportStem.Length <= 220);
        Assert.True((decision.ExportStem + ".png.json").Length <= 255);
        Assert.NotEqual(decision.ExportStem,
            Decide([Candidate(new string('s', 160), TextureNameKind.OriginalName)],
                identity: nested with { MemberPath = new string('y', 512) }, resourceType: uint.MaxValue, resourceId: uint.MaxValue).ExportStem);
    }

    private static TextureNameCandidate Candidate(string text, TextureNameKind kind,
        IReadOnlyList<TextureEvidence>? evidence = null, ResourceIdentity? owner = null, string? role = null)
        => new(text, kind, evidence ?? [Evidence(Container)], owner, role);

    private static TextureEvidence Evidence(ResourceIdentity source)
        => new(source, new(source.IndexContentId, Revision.PhysicalSourceStamp), "fixture-reader", "1",
            new string('d', 64), "name", new("container.dat", 100, 40), new(source, 20, 12));

    private static TextureNameDecision Decide(IReadOnlyList<TextureNameCandidate> candidates,
        ResourceIdentity? identity = null, uint resourceType = 7, uint resourceId = 42,
        int width = 128, int height = 64, string format = "RGBA8")
        => TextureNamePolicy.Decide(identity ?? Texture, resourceType, resourceId, width, height, format, candidates);
}
