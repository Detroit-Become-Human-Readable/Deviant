using Deviant.Assets;
using Deviant.Browser;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class ModelCatalogClassificationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CharacterNameNeedsValidatedRigAndLinkedSkin(bool validatedRig, bool skin)
    {
        var fixture = Create("Pilot_Formal_CHA", validatedRig, skin);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("rigged", result.Kind);
        Assert.Null(result.CharacterKey);
        Assert.Null(result.CharacterName);
    }

    [Fact]
    public void LinkedCharacterSourceNameProducesActorAndVariantWithoutChangingSourceLabel()
    {
        var fixture = Create("Pilot_Formal_CHA", validatedRig: true, skin: true);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("character", result.Kind);
        Assert.Equal("pilot", result.CharacterKey);
        Assert.Equal("Pilot", result.CharacterName);
        Assert.Equal("Formal", result.Variant);
        Assert.Equal("Pilot · Formal", result.DisplayLabel);
        Assert.Equal("Pilot_Formal_CHA", result.SourceLabel);
    }

    [Theory]
    [InlineData("CHAIR")]
    [InlineData("Pilot_CHAIR")]
    [InlineData("Pilot")]
    public void OrdinaryNamesAndRigCountsDoNotCreateCharacterMembership(string name)
    {
        var fixture = Create(name, validatedRig: true, skin: true);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("rigged", result.Kind);
        Assert.Null(result.CharacterKey);
    }

    [Fact]
    public void PairedFacialAndLimbDeformersSupportCharacterWithoutChaToken()
    {
        var fixture = Create("Pilot_head", validatedRig: true, skin: true,
            rigNames: ["Pilot.Face.EyelidDEF", "Pilot.Body.ArmDEF"]);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("character", result.Kind);
        Assert.Equal("Pilot", result.CharacterName);
        Assert.Equal("pilot", result.CharacterKey);
    }

    [Fact]
    public void LinkedSkinStreamsAndNamedFaceAndLimbJointsSupportAssemblyNamesWithoutPaletteRows()
    {
        var fixture = Create("TECHNICIAN_EXT03_HEAD_BS", validatedRig: true, skin: false,
            rigNames: ["FA_L_Eye_Dilator", "EX_R_Elbow_ForeArm_Slide"]);
        var binding = fixture.Binding with { Lods = [fixture.Binding.Lods[0] with { HasSkinAttributes = true }] };
        var result = ModelCatalogClassification.Project(fixture.Summary, [binding]);
        Assert.Equal("character", result.Kind); Assert.Equal("Technician", result.CharacterName);
        Assert.Equal("technician", result.CharacterKey); Assert.Equal("EXT03", result.Variant);
        Assert.Equal("TECHNICIAN_EXT03_HEAD_BS", result.SourceLabel);
    }

    [Fact]
    public void AssemblyStyleNamesDoNotClassifyAPropWithoutFacialAndLimbEvidence()
    {
        var fixture = Create("TECHNICIAN_EXT03_HEAD_BS", validatedRig: true, skin: true, rigNames: ["hinge", "latch"]);
        Assert.Equal("rigged", ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]).Kind);
    }

    [Fact]
    public void NativeCharacterPrefixAndSeparatorDoNotBecomeTheActorName()
    {
        var fixture = Create("CHA_:TECHNICIAN_INT02_HEAD_BS", validatedRig: true, skin: true,
            rigNames: ["FA_R_Eye_Dilator", "EX_L_Wrist_Ext"]);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("Technician", result.CharacterName); Assert.Equal("INT02", result.Variant);
        Assert.Equal("technician", result.CharacterKey);
    }

    [Theory]
    [InlineData("Pilot.Face.EyelidDEF", "Pilot.Face.JawDEF")]
    [InlineData("Pilot.Body.ArmDEF", "Pilot.Body.LegDEF")]
    public void OneDeformerFamilyOrPlainBoneNamesRemainGenericRiggedModels(string first, string second)
    {
        var fixture = Create("Pilot_head", validatedRig: true, skin: true, rigNames: [first, second]);
        Assert.Equal("rigged", ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]).Kind);
    }

    [Fact]
    public void ConflictingActorNamesDoNotSelectFirstCandidate()
    {
        var fixture = Create("Pilot_CHA", validatedRig: true, skin: true,
            rigNames: ["Engineer_CHA"]);
        var result = ModelCatalogClassification.Project(fixture.Summary, [fixture.Binding]);
        Assert.Equal("rigged", result.Kind);
        Assert.Null(result.CharacterName);
    }

    [Fact]
    public void ParsedUnskinnedModelIsStaticAndMissingSkinMetadataIsUnclassified()
    {
        var fixture = Create("metal_chair", validatedRig: false, skin: false);
        var binding = fixture.Binding with { OwnerNativeType = 2009, Rig = null };
        var result = ModelCatalogClassification.Project(fixture.Summary with { OwnerNativeType = 2009 }, [binding]);
        Assert.Equal("static", result.Kind);
        Assert.Equal("Metal Chair", result.DisplayLabel);
        binding = binding with { Lods = [binding.Lods[0] with { HasSkinPalette = null }] };
        Assert.Equal("unclassified", ModelCatalogClassification.Project(fixture.Summary with { OwnerNativeType = 2009 }, [binding]).Kind);
    }

    [Fact]
    public void OtherMembersRigAndAmbiguousMeshLinksDoNotClassifySelectedModel()
    {
        var fixture = Create("Pilot_CHA", validatedRig: true, skin: true);
        var other = fixture.Binding with { SelectedIdentity = fixture.Binding.SelectedIdentity with { MemberPath = "dc-object/2/2013/44" } };
        Assert.Equal("unclassified", ModelCatalogClassification.Project(fixture.Summary, [other]).Kind);
        var ambiguous = fixture.Binding with { Lods = [fixture.Binding.Lods[0] with
            { MeshDataResolution = new(ReferenceStatus.Ambiguous, [fixture.Binding.Lods[0].MeshDataIdentity!]) }] };
        Assert.Equal("rigged", ModelCatalogClassification.Project(fixture.Summary, [ambiguous]).Kind);
    }

    [Fact]
    public void SearchIncludesLabelAliasSourceNamesIdentityAndVariant()
    {
        var fixture = Create("Pilot_Formal_CHA", validatedRig: true, skin: true);
        var descriptor = new AssetDescriptor(fixture.Summary.Identity, fixture.Summary.Revision,
            "character-model", "Saved alias", null, AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.None, [new("Pilot_Formal_CHA", NameOrigin.Stored, "generated mesh"),
                new("Saved alias", NameOrigin.UserAlias, "AliasStore")], [])
        { Metadata = new Dictionary<string, string> { ["model.variant"] = "Formal", ["NativeId"] = "42" } };
        var terms = ModelCatalogClassification.SearchTerms(descriptor);
        Assert.Contains("Saved alias", terms);
        Assert.Contains("Pilot_Formal_CHA", terms);
        Assert.Contains(descriptor.Identity.Key, terms);
        Assert.Contains("Formal", terms);
        Assert.Contains("42", terms);
    }

    private static (CharacterModelSourceSummary Summary, CharacterModelBindingResult Binding) Create(
        string name, bool validatedRig, bool skin, string[]? rigNames = null)
    {
        var owner = new ResourceIdentity("generated-models", 1);
        var model = owner with { MemberPath = "dc-object/0/2013/42" };
        var mesh = owner with { MemberPath = "dc-object/1/2130/43" };
        var catalog = new ResourceIdentity(owner.IndexContentId, 0);
        var revision = new SourceRevision(owner.IndexContentId, "generated-revision");
        var extent = new DecodedExtent(owner, 0, 128);
        var reference = new ResourceReference(IdentifierNamespace.Native, "43", model, 2130);
        var resolution = new ReferenceResolution(ReferenceStatus.Unique, [mesh]);
        var lod = new CharacterMeshLodBinding(0, 1, reference, resolution, mesh, 0, 1,
            name, new NativeModelSelection(mesh, 0), extent, extent, extent, null) { HasSkinPalette = skin, HasSkinAttributes = skin };
        var rig = new CharacterRigSummary(15, 100, 1,
            (rigNames ?? []).Select((text, index) => new CharacterNameObservation(0, (uint)index, text, extent)).ToArray(),
            false, extent) { NodeBindingValidated = validatedRig };
        var binding = new CharacterModelBindingResult(model, catalog, owner, revision, 2013, 42,
            0, 0, null, null, new(ReferenceStatus.Unique, [owner]), [], [], [lod], rig, null);
        var summary = new CharacterModelSourceSummary(model, revision, 2013, 42, [],
            [new(name, NameOrigin.Stored, "generated mesh")], [], [], 0, true);
        return (summary, binding);
    }
}
