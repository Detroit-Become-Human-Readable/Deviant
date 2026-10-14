using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class ModelAnimationLinkTests
{
    [Fact]
    public void UniqueTypedReferenceToValidatedAnimationClassAdmitsExactMember()
    {
        var fixture = Create();
        var links = CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, fixture.References);
        var link = Assert.Single(links);
        Assert.Equal(fixture.Target.Identity, link.AnimationIdentity);
        Assert.Equal(fixture.Owner.Identity, link.SourceIdentity);
        Assert.Equal("ENTITY.Reference1", link.SourceField);
        Assert.Equal(fixture.References[0].ReferenceExtent, link.ReferenceExtent);
        Assert.Equal(14U, link.AnimationVersion);
        Assert.Equal(544, link.AnimationClassExtent.Offset);
        Assert.Equal(12, link.AnimationClassExtent.Length);
    }

    [Theory]
    [InlineData("ANIMDATA", 13U, 3U)]
    [InlineData("MESHDATA", 14U, 3U)]
    [InlineData("ANIMDATA", 14U, 4U)]
    public void TypeIdWithoutExpectedClassAndVersionDoesNotAdmitClip(string tag, uint version, uint classCount)
    {
        var fixture = Create(tag: tag, version: version, classCount: classCount);
        Assert.Empty(CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, fixture.References));
    }

    [Fact]
    public void AnimationTagOutsideTheExpectedClassBoundaryDoesNotAdmitClip()
    {
        var fixture = Create(tag: "OTHER___");
        var bytes = fixture.Target.RawPayload.ToArray();
        "ANIMDATA"u8.CopyTo(bytes.AsSpan(48));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 14);
        var target = fixture.Target with { RawPayload = bytes };
        var directory = fixture.Directory with { Objects = [fixture.Owner, target] };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(directory, fixture.Owner.Identity, fixture.References));
    }

    [Fact]
    public void AmbiguousNativeIdDoesNotChooseFirstClipMember()
    {
        var fixture = Create();
        var duplicate = fixture.Target with
        { Identity = fixture.Target.Identity with { MemberPath = "dc-object/2/2033/77" }, DirectoryIndex = 2 };
        var directory = fixture.Directory with { Objects = [fixture.Owner, fixture.Target, duplicate] };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(directory, fixture.Owner.Identity, fixture.References));
    }

    [Fact]
    public void ArchiveNamespaceAndForeignOwnerDoNotJoinMatchingNativeIds()
    {
        var fixture = Create();
        var reference = fixture.References[0];
        var archive = reference with { Reference = reference.Reference with { Namespace = IdentifierNamespace.Archive } };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, [archive]));
        var foreign = reference with { Reference = reference.Reference with { Owner = new("other-index", 1) } };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, [foreign]));
    }

    [Fact]
    public void ReferenceMustMatchStoredTypeIdBytesAndExtent()
    {
        var fixture = Create();
        var reference = fixture.References[0];
        var changed = reference with { Reference = reference.Reference with { Id = "78" } };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, [changed]));
        var outside = reference with { ReferenceExtent = reference.ReferenceExtent with { Offset = 1024 } };
        Assert.Empty(CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, [outside]));
    }

    [Fact]
    public void DuplicateReferencesCountOneExactClipAndKeepPartialState()
    {
        var fixture = Create();
        var bytes = fixture.Owner.RawPayload.ToArray();
        bytes.AsSpan(8, 8).CopyTo(bytes.AsSpan(16));
        var owner = fixture.Owner with { RawPayload = bytes };
        var directory = fixture.Directory with { Objects = [owner, fixture.Target] };
        var second = fixture.References[0] with { SourceField = "ENTITY.Reference2",
            ReferenceExtent = fixture.References[0].ReferenceExtent with { Offset = 16 } };
        var links = CharacterModelAnimationLinks.Resolve(directory, owner.Identity, [fixture.References[0], second]);
        Assert.Equal(2, links.Count);
        var binding = Binding(directory, owner) with { AnimationLinks = links };
        var descriptor = Descriptor(directory, owner);
        var projected = CharacterCatalogSession.ApplyModelAnimationMetadata(descriptor, [binding, binding]);
        Assert.Equal("partial", projected.Metadata["model.animation-state"]);
        Assert.Equal("1", projected.Metadata["model.animation-count"]);
        Assert.Equal(new[] { fixture.Target.Identity.Key }, JsonSerializer.Deserialize<string[]>(projected.Metadata["model.animation-identities"])!);
    }

    [Fact]
    public void NoQualifiedLinkClearsStaleCountAndRemainsUnknown()
    {
        var fixture = Create(tag: "OTHER___");
        var descriptor = Descriptor(fixture.Directory, fixture.Owner) with
        { Metadata = new Dictionary<string, string> { ["model.animation-count"] = "0", ["NativeId"] = "42" } };
        var projected = CharacterCatalogSession.ApplyModelAnimationMetadata(descriptor,
            [Binding(fixture.Directory, fixture.Owner)]);
        Assert.Equal("unknown", projected.Metadata["model.animation-state"]);
        Assert.False(projected.Metadata.ContainsKey("model.animation-count"));
        Assert.Empty(JsonSerializer.Deserialize<string[]>(projected.Metadata["model.animation-identities"])!);
        Assert.Equal("42", projected.Metadata["NativeId"]);
    }

    [Fact]
    public void EntityAndMeshContextReferencesRetainTheirOwnSourceExtents()
    {
        var fixture = Create();
        var bytes = fixture.Owner.RawPayload.ToArray();
        bytes.AsSpan(8, 8).CopyTo(bytes.AsSpan(64));
        bytes.AsSpan(8, 8).CopyTo(bytes.AsSpan(80));
        var owner = fixture.Owner with { RawPayload = bytes };
        var fields = new Dictionary<string, DecodedExtent>
        {
            ["Node"] = new(fixture.Directory.Identity, 0, 8),
            ["Reference1"] = new(fixture.Directory.Identity, 8, 8),
            ["Reference2"] = new(fixture.Directory.Identity, 16, 8),
            ["Reference3"] = new(fixture.Directory.Identity, 24, 8)
        };
        var entity = new SceneEntityRecord(owner.Identity, owner.NativeType, owner.NativeId, null,
            [fixture.References[0].Reference], 0, 0, 4, 49, new(fixture.Directory.Identity, 0, 49), fields);
        var contextExtent = new DecodedExtent(fixture.Directory.Identity, 64, 8);
        var extraExtent = new DecodedExtent(fixture.Directory.Identity, 80, 8);
        var blank = new ResourceReference(IdentifierNamespace.Native, "0", owner.Identity, 0);
        var prefix = new NativeMeshPrefixDocument(owner.Identity, entity, 29, [], 0, 0,
            new(0, 0, blank, blank, new(fixture.Directory.Identity, 32, 24)), 1, 0,
            [new(0, [], fixture.References[0].Reference, null, null, contextExtent,
                contextExtent with { Length = 0 }, contextExtent, null, null)],
            contextExtent with { Length = 0 }, contextExtent with { Length = 0 },
            [new(fixture.References[0].Reference, extraExtent)],
            new(0, 0, 0, [], extraExtent), new(fixture.Directory.Identity, 0, 128), 128,
            new(fixture.Directory.Identity, 128, 0), new Dictionary<string, DecodedExtent>
            {
                ["FixedReference0"] = new(fixture.Directory.Identity, 40, 8),
                ["FixedReference1"] = new(fixture.Directory.Identity, 48, 8)
            });
        var references = CharacterModelAnimationLinks.ReadReferences(owner, entity, prefix);
        Assert.Equal(3, references.Count);
        Assert.Contains(references, item => item.SourceField == "ENTITY.Reference1" && item.ReferenceExtent.Offset == 8);
        Assert.Contains(references, item => item.SourceField == "MESH.Context[0]" && item.ReferenceExtent == contextExtent);
        Assert.Contains(references, item => item.SourceField == "MESH.ExtraReference[0]" && item.ReferenceExtent == extraExtent);
    }

    [Fact]
    public void ExcessReferencesAndCancellationAreBounded()
    {
        var fixture = Create();
        var excess = Enumerable.Repeat(fixture.References[0], CharacterModelAnimationLinks.MaximumDirectReferences + 1).ToArray();
        Assert.Throws<InvalidDataException>(() => CharacterModelAnimationLinks.Resolve(fixture.Directory, fixture.Owner.Identity, excess));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CharacterModelAnimationLinks.Resolve(fixture.Directory,
            fixture.Owner.Identity, fixture.References, cancelled.Token));
    }

    private static CharacterModelBindingResult Binding(NativeObjectDirectoryDocument directory, NativeObjectFrame owner) =>
        new(owner.Identity, new(directory.Identity.IndexContentId, 0), directory.Identity, directory.Revision,
            owner.NativeType, owner.NativeId, 0, 0, null, null, new(ReferenceStatus.Unique, [directory.Identity]), [], [], [], null, null);

    private static AssetDescriptor Descriptor(NativeObjectDirectoryDocument directory, NativeObjectFrame owner) =>
        new(owner.Identity, directory.Revision, "character-model", "Generated model", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, [], []);

    private static (NativeObjectDirectoryDocument Directory, NativeObjectFrame Owner, NativeObjectFrame Target,
        IReadOnlyList<CharacterModelDirectReference> References) Create(string tag = "ANIMDATA", uint version = 14, uint classCount = 3)
    {
        var container = new ResourceIdentity("generated-animation-links", 1);
        var ownerBytes = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(ownerBytes.AsSpan(8), 2033);
        BinaryPrimitives.WriteUInt32LittleEndian(ownerBytes.AsSpan(12), 77);
        var owner = Frame(container, 0, 2009, 42, 0, ownerBytes, 4);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(classCount); writer.Write("COM_CONT"u8); writer.Write(6U); writer.Write(0U);
        writer.Write("LOADCONT"u8); writer.Write(2U); writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(version);
        writer.Write(new byte[28]);
        var target = Frame(container, 1, 2033, 77, 512, stream.ToArray(), classCount);
        var directory = new NativeObjectDirectoryDocument(container, new(container.IndexContentId, "generated-revision"),
            new(container, 0, 16), new(container, 0, 1024), Array.Empty<byte>(), ReadOnlyMemory<byte>.Empty, [owner, target], null, ReadOnlyMemory<byte>.Empty);
        var reference = new CharacterModelDirectReference(owner.Identity, "ENTITY.Reference1",
            new(IdentifierNamespace.Native, "77", owner.Identity, 2033), new(container, 8, 8));
        return (directory, owner, target, [reference]);
    }

    private static NativeObjectFrame Frame(ResourceIdentity container, int ordinal, uint type, uint id,
        int offset, byte[] payload, uint classes) => new(container with { MemberPath = $"dc-object/{ordinal}/{type}/{id}" },
            ordinal, type, id, new(container, ordinal * 8, 8), new(container, offset, 13), new(container, offset, payload.Length),
            new byte[13], payload, 0, 0, 0, classes);
}
