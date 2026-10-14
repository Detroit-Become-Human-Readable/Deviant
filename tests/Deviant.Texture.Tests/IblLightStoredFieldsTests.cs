using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class IblLightStoredFieldsTests
{
    [Fact]
    public void ProjectsExactFieldMapFromNonzeroClassAndContainerOffsets()
    {
        uint[] influence = [Bits(1.25f), Bits(2.5f), Bits(3.75f), Bits(4.125f), Bits(5.5f), Bits(6.75f)];
        uint[] reflection = [Bits(-1.25f), Bits(-2.5f), Bits(-3.75f), Bits(-4.125f), Bits(-5.5f), Bits(-6.75f)];
        var fixture = CreateFixture(2, tagOffset: 5, firstSpecularIndex: 0xA1B2C3D4,
            influenceWords: influence, reflectionWords: reflection, attenuationStart: Bits(7.25f), intensity: Bits(8.5f), scalar77: Bits(9.75f),
            scalar83: Bits(10.125f), trailing0: Bits(11.5f), trailing1: Bits(12.75f),
            storedReference: (4101, 5102), references: [(6101, 7102), (8101, 9102)]);
        var fields = ProjectValue(fixture.Context);
        long origin = fixture.ClassOffset;

        Assert.Equal(fixture.Identity, fields.Identity);
        Assert.Equal(fixture.Revision, fields.Revision);
        Assert.Equal(fixture.Context.Extent, fields.Extent);
        Assert.Equal(fixture.Container, fields.Enabled.Extent.Container);
        Assert.NotEqual(fields.Identity.MemberPath, fields.Enabled.Extent.Container.MemberPath);
        Assert.NotEqual(0, fields.ExtentBounds.MinimumX.Extent.Offset % 4);

        Assert.Equal((byte)0xA5, fields.Enabled.Value); Assert.True(fields.Enabled.IsNonzero);
        Assert.Equal(origin + 12, fields.Enabled.Extent.Offset); Assert.Equal(1, fields.Enabled.Extent.Length);
        AssertBounds(fields.ExtentBounds, influence, origin + 13);
        AssertBounds(fields.LocalBoxBounds, reflection, origin + 37);
        AssertPair(fields.StoredReference, (4101, 5102), origin + 61);
        AssertFloat(fields.AttenuationStart, Bits(7.25f), origin + 69);
        AssertFloat(fields.Intensity, Bits(8.5f), origin + 73);
        AssertFloat(fields.Scalar77, Bits(9.75f), origin + 77);
        Assert.Equal((byte)0x7E, fields.SpecularTexturesEnabled.Value);
        Assert.True(fields.SpecularTexturesEnabled.IsNonzero);
        Assert.Equal(origin + 81, fields.SpecularTexturesEnabled.Extent.Offset);
        Assert.Equal((byte)0x91, fields.Byte82.Value); Assert.True(fields.Byte82.IsNonzero);
        Assert.Equal(origin + 82, fields.Byte82.Extent.Offset);
        AssertFloat(fields.Scalar83, Bits(10.125f), origin + 83);
        Assert.Equal(2u, fields.TextureReferenceCount.Value);
        Assert.Equal(origin + 87, fields.TextureReferenceCount.Extent.Offset);
        Assert.Equal(4, fields.TextureReferenceCount.Extent.Length);
        Assert.Equal(0x92A4B6C8u, fields.Word91.Value);
        Assert.Equal(origin + 91, fields.Word91.Extent.Offset);
        AssertPair(fields.TextureReferences[0], (6101, 7102), origin + 95);
        AssertPair(fields.TextureReferences[1], (8101, 9102), origin + 103);
        Assert.Equal(0xA1B2C3D4u, fields.FirstSpecularSetIndex.Value);
        Assert.Equal(origin + 111, fields.FirstSpecularSetIndex.Extent.Offset);
        Assert.Equal(4, fields.FirstSpecularSetIndex.Extent.Length);
        Assert.Equal("Owner", fields.OwnerName.Text);
        Assert.Equal(origin + 115, fields.OwnerName.LengthExtent.Offset);
        Assert.Equal(origin + 119, fields.OwnerName.ValueExtent.Offset);
        Assert.Equal("assets/probe.bin", fields.SourcePath.Text);
        AssertFloat(fields.TrailingScalar0, Bits(11.5f), origin + fixture.Record.Length - 8);
        AssertFloat(fields.TrailingScalar1, Bits(12.75f), origin + fixture.Record.Length - 4);

        Assert.Equal(fixture.PhysicalExtents, fields.OriginalPhysicalExtents);
        Assert.Equal(fixture.PhysicalExtents, Project(fixture.Context).Extents);
    }

    [Theory]
    [InlineData(0, 95)]
    [InlineData(1, 103)]
    [InlineData(2, 111)]
    [InlineData(TextureContextReader.MaximumReferences, 2143)]
    public void FirstSpecularIndexUsesStoredReferenceCountAndIsNeverClamped(int count, int classOffset)
    {
        var fixture = CreateFixture(count, firstSpecularIndex: uint.MaxValue);
        var fields = ProjectValue(fixture.Context);

        Assert.Equal((uint)count, fields.TextureReferenceCount.Value);
        Assert.Equal(uint.MaxValue, fields.FirstSpecularSetIndex.Value);
        Assert.Equal(fixture.ClassOffset + classOffset, fields.FirstSpecularSetIndex.Extent.Offset);
        Assert.Equal(4, fields.FirstSpecularSetIndex.Extent.Length);
        Assert.Equal(count, fields.TextureReferences.Count);
    }

    [Fact]
    public void RejectsReferenceCountMismatchAndOverLimitPublicContexts()
    {
        var fixture = CreateFixture(2);
        var fewerReferences = fixture.Context with { TextureReferences = fixture.Context.TextureReferences.Take(1).ToArray() };
        AssertFailure(fewerReferences, "ReferenceCountMismatch");

        var fixedFields = fixture.Context.RawFixedFields83.ToArray();
        WriteWord(fixedFields, 75, TextureContextReader.MaximumReferences + 1);
        var excessiveReferences = Enumerable.Range(0, TextureContextReader.MaximumReferences + 1)
            .Select(index => new TextureContextReference((uint)(100 + index), (uint)(200 + index), fixture.Context.TextureReferences[0].Extent))
            .ToArray();
        AssertFailure(fixture.Context with { RawFixedFields83 = fixedFields, TextureReferences = excessiveReferences }, "ReferenceCountMismatch");
    }

    [Fact]
    public void BindsOnlyValidNativePairsToTheIblOwnerAndRetainsSentinels()
    {
        var fixture = CreateFixture(4, storedReference: (3001, 4002),
            references: [(5001, 6002), (uint.MaxValue, 7002), (8001, 0), (0, 9002)]);
        var result = Project(fixture.Context);
        var fields = Assert.IsType<IblLightStoredFields>(result.Value);

        Assert.Equal(new ResourceReference(IdentifierNamespace.Native, "4002", fixture.Identity, 3001), fields.GetStoredResourceReference());
        Assert.Equal(new ResourceReference(IdentifierNamespace.Native, "6002", fixture.Identity, 5001), fields.GetTextureResourceReference(0));
        Assert.Null(fields.GetTextureResourceReference(1));
        Assert.Null(fields.GetTextureResourceReference(2));
        Assert.Null(fields.GetTextureResourceReference(3));
        Assert.Equal(2, result.UnresolvedReferences.Count);
        Assert.All(result.UnresolvedReferences, reference => Assert.Equal(fixture.Identity, reference.Owner));
        Assert.Equal(uint.MaxValue, fields.TextureReferences[1].Type);
        Assert.Equal(0u, fields.TextureReferences[2].Id);
        Assert.False(fields.TextureReferences[1].IsBound);
    }

    [Fact]
    public void PreservesIeeeWordsAndSerializesTheirBitsInsteadOfNonfiniteFloats()
    {
        const uint negativeZero = 0x80000000;
        const uint quietNanPayload = 0x7FC12345;
        const uint positiveInfinity = 0x7F800000;
        const uint negativeInfinity = 0xFF800000;
        var fixture = CreateFixture(1,
            influenceWords: [negativeZero, Bits(1), quietNanPayload, Bits(2), positiveInfinity, Bits(3)],
            attenuationStart: negativeZero, intensity: quietNanPayload, scalar77: positiveInfinity,
            scalar83: negativeInfinity, trailing0: quietNanPayload, trailing1: negativeZero);
        var fields = ProjectValue(fixture.Context);

        Assert.Equal(negativeZero, fields.ExtentBounds.MinimumX.Bits);
        Assert.Equal(quietNanPayload, fields.ExtentBounds.MinimumY.Bits);
        Assert.Equal(positiveInfinity, fields.ExtentBounds.MinimumZ.Bits);
        Assert.Equal(negativeZero, BitConverter.SingleToUInt32Bits(fields.AttenuationStart.GetValue()));
        Assert.Equal(quietNanPayload, fields.Intensity.Bits);
        Assert.Equal(positiveInfinity, BitConverter.SingleToUInt32Bits(fields.Scalar77.GetValue()));
        Assert.Equal(negativeInfinity, fields.Scalar83.Bits);
        Assert.Equal(quietNanPayload, fields.TrailingScalar0.Bits);
        Assert.Equal(negativeZero, fields.TrailingScalar1.Bits);
        Assert.False(fields.ExtentBounds.IsFinite);
        Assert.False(fields.ExtentBounds.IsOrdered);
        Assert.False(fields.ExtentBounds.HasPositiveVolume);

        var json = JsonSerializer.Serialize(fields);
        Assert.Contains($"\"Bits\":{quietNanPayload}", json);
        Assert.Contains($"\"Bits\":{negativeZero}", json);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public void BoundsDistinguishFiniteOrderedAndPositiveVolume()
    {
        var positive = ProjectValue(CreateFixture(0, influenceWords:
            [Bits(-1), Bits(1), Bits(-2), Bits(2), Bits(-3), Bits(3)]).Context).ExtentBounds;
        Assert.True(positive.IsFinite); Assert.True(positive.IsOrdered); Assert.True(positive.HasPositiveVolume);
        Assert.True(positive.TryGetBounds(out var converted));
        Assert.Equal(new Vector3(-1, -2, -3), converted!.Minimum);
        Assert.Equal(new Vector3(1, 2, 3), converted.Maximum);

        var zeroExtent = ProjectValue(CreateFixture(0, influenceWords:
            [Bits(1), Bits(1), Bits(-2), Bits(2), Bits(-3), Bits(3)]).Context).ExtentBounds;
        Assert.True(zeroExtent.IsFinite); Assert.True(zeroExtent.IsOrdered); Assert.False(zeroExtent.HasPositiveVolume);
        Assert.True(zeroExtent.TryGetBounds(out var zeroBounds));
        Assert.Equal(zeroBounds!.Minimum.X, zeroBounds.Maximum.X);

        var reversed = ProjectValue(CreateFixture(0, influenceWords:
            [Bits(2), Bits(1), Bits(-2), Bits(2), Bits(-3), Bits(3)]).Context).ExtentBounds;
        Assert.True(reversed.IsFinite); Assert.False(reversed.IsOrdered); Assert.False(reversed.HasPositiveVolume);
        Assert.False(reversed.TryGetBounds(out var invalidBounds)); Assert.Null(invalidBounds);

        var nonfinite = ProjectValue(CreateFixture(0, influenceWords:
            [0x7FC12345, Bits(1), Bits(-2), Bits(2), Bits(-3), Bits(3)]).Context).ExtentBounds;
        Assert.False(nonfinite.IsFinite); Assert.False(nonfinite.IsOrdered); Assert.False(nonfinite.HasPositiveVolume);
        Assert.False(nonfinite.TryGetBounds(out var nonfiniteBounds)); Assert.Null(nonfiniteBounds);
    }

    [Fact]
    public void RejectsForgedContainerFieldStringAndClassBoundaries()
    {
        var fixture = CreateFixture(2);
        AssertFailure(fixture.Context with { Identity = new ResourceIdentity("other-index", fixture.Identity.Ordinal, fixture.Identity.MemberPath) }, "SourceMismatch");
        AssertFailure(fixture.Context with { Extent = fixture.Context.Extent with { Container = new ResourceIdentity(fixture.Container.IndexContentId, fixture.Container.Ordinal + 1) } }, "SourceMismatch");
        AssertFailure(fixture.Context with { Extent = fixture.Context.Extent with { Offset = long.MaxValue - 2 } }, "SourceMismatch");
        AssertFailure(fixture.Context with { Extent = fixture.Context.Extent with { Length = fixture.Context.Extent.Length - 1 } }, "RecordBoundaryMismatch");
        AssertFailure(fixture.Context with { Extent = fixture.Context.Extent with { Length = fixture.Context.Extent.Length + 1 } }, "RecordBoundaryMismatch");
        AssertFailure(fixture.Context with { RawFixedFields83 = fixture.Context.RawFixedFields83[..^1] }, "InvalidFieldStorage");

        var badReference = fixture.Context.TextureReferences[0] with
        { Extent = fixture.Context.TextureReferences[0].Extent with { Offset = fixture.Context.TextureReferences[0].Extent.Offset + 1 } };
        AssertFailure(fixture.Context with { TextureReferences = [badReference, fixture.Context.TextureReferences[1]] }, "ReferenceExtentMismatch");
        var wrongReferenceContainer = fixture.Context.TextureReferences[0] with
        { Extent = fixture.Context.TextureReferences[0].Extent with { Container = new ResourceIdentity(fixture.Container.IndexContentId, fixture.Container.Ordinal, "member/other") } };
        AssertFailure(fixture.Context with { TextureReferences = [wrongReferenceContainer, fixture.Context.TextureReferences[1]] }, "ReferenceExtentMismatch");
        var badOwner = fixture.Context.OwnerName with { ValueExtent = fixture.Context.OwnerName.ValueExtent with { Offset = fixture.Context.OwnerName.ValueExtent.Offset + 1 } };
        AssertFailure(fixture.Context with { OwnerName = badOwner }, "InvalidOwnerName");
        var badPath = fixture.Context.SourcePath with { LengthExtent = fixture.Context.SourcePath.LengthExtent with { Length = 3 } };
        AssertFailure(fixture.Context with { SourcePath = badPath }, "InvalidSourcePath");
    }

    [Fact]
    public void ValidatesStringDigestsAndAcceptsEmptyAndEitherHexCase()
    {
        var empty = CreateFixture(0, owner: "", path: "").Context;
        var emptyFields = ProjectValue(empty);
        Assert.Equal("", emptyFields.OwnerName.Text); Assert.Equal("", emptyFields.SourcePath.Text);

        var fixture = CreateFixture(1);
        var uppercase = fixture.Context with
        {
            OwnerName = fixture.Context.OwnerName with { ValueSha256 = fixture.Context.OwnerName.ValueSha256.ToUpperInvariant() },
            SourcePath = fixture.Context.SourcePath with { ValueSha256 = fixture.Context.SourcePath.ValueSha256.ToLowerInvariant() }
        };
        Assert.NotNull(Project(uppercase).Value);

        AssertFailure(fixture.Context with { OwnerName = fixture.Context.OwnerName with { Text = "Other" } }, "InvalidOwnerName");
        AssertFailure(fixture.Context with { SourcePath = fixture.Context.SourcePath with { Text = "assets/other.bin" } }, "InvalidSourcePath");
        AssertFailure(fixture.Context with { OwnerName = fixture.Context.OwnerName with { ValueSha256 = null! } }, "InvalidOwnerName");
        AssertFailure(fixture.Context with { OwnerName = fixture.Context.OwnerName with { ValueSha256 = "xyz" } }, "InvalidOwnerName");
        AssertFailure(fixture.Context with { SourcePath = fixture.Context.SourcePath with { ValueSha256 = new string('g', 64) } }, "InvalidSourcePath");
        AssertFailure(fixture.Context with { SourcePath = fixture.Context.SourcePath with { ValueSha256 = new string('0', 64) } }, "InvalidSourcePath");
    }

    [Fact]
    public void ProjectionOwnsCopiesOfMutableStorageAndBoundedPhysicalProvenance()
    {
        var physical = new[]
        {
            new SourceExtent("volume-a", 100, 12, new string('a', 64)),
            new SourceExtent("volume-b", 400, 20, new string('b', 64))
        };
        var fixture = CreateFixture(2, physicalExtents: physical);
        var result = Project(fixture.Context);
        var fields = Assert.IsType<IblLightStoredFields>(result.Value);
        var expectedFixed = fields.RawFixedFields83.ToArray();
        var expectedTail = fields.RawTrailingFields8.ToArray();
        var expectedReference = fields.TextureReferences[0];
        var expectedPhysical = fields.OriginalPhysicalExtents.ToArray();

        fixture.Context.RawFixedFields83[1] ^= 0x40;
        fixture.Context.RawTrailingFields8[0] ^= 0x20;
        ((TextureContextReference[])fixture.Context.TextureReferences)[0] = new(1, 2, expectedReference.Extent);
        ((SourceExtent[])fixture.Context.OriginalPhysicalExtents)[0] = new("changed-volume", 900, 1, new string('c', 64));

        Assert.Equal(expectedFixed, fields.RawFixedFields83.ToArray());
        Assert.Equal(expectedTail, fields.RawTrailingFields8.ToArray());
        Assert.Equal(expectedReference, fields.TextureReferences[0]);
        Assert.Equal(expectedPhysical, fields.OriginalPhysicalExtents);
        Assert.Equal(expectedPhysical, result.Extents);
    }

    [Fact]
    public void PhysicalProvenanceIsBoundedAndCopied()
    {
        var item = new SourceExtent("volume-a", 10, 20, new string('d', 64));
        var accepted = CreateFixture(0, physicalExtents: Enumerable.Repeat(item, IblLightStoredFieldsProjection.MaximumPhysicalExtents).ToArray());
        var result = Project(accepted.Context);
        Assert.NotNull(result.Value); Assert.Equal(IblLightStoredFieldsProjection.MaximumPhysicalExtents, result.Extents.Count);

        var rejected = CreateFixture(0, physicalExtents: Enumerable.Repeat(item, IblLightStoredFieldsProjection.MaximumPhysicalExtents + 1).ToArray());
        AssertFailure(rejected.Context, "PhysicalExtentLimit");
        AssertFailure(accepted.Context with { OriginalPhysicalExtents = [item with { Offset = -1 }] }, "PhysicalExtentMismatch");
    }

    [Fact]
    public void CancellationIsObservedBeforeProjection()
    {
        var fixture = CreateFixture(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => IblLightStoredFieldsProjection.Project(fixture.Context, cancellation.Token));
    }

    private static DecodeResult<IblLightStoredFields> Project(TextureIblLightV13Context context) =>
        IblLightStoredFieldsProjection.Project(context);

    private static IblLightStoredFields ProjectValue(TextureIblLightV13Context context) =>
        Assert.IsType<IblLightStoredFields>(Project(context).Value);

    private static void AssertFailure(TextureIblLightV13Context context, string code)
    {
        var result = Project(context);
        Assert.Null(result.Value);
        Assert.Contains(code, result.Diagnostics);
    }

    private static void AssertPair(TextureContextReference actual, (uint Type, uint Id) expected, long offset)
    {
        Assert.Equal(expected.Type, actual.Type); Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(offset, actual.Extent.Offset); Assert.Equal(8, actual.Extent.Length);
    }

    private static void AssertFloat(IblFloat32 actual, uint bits, long offset)
    {
        Assert.Equal(bits, actual.Bits); Assert.Equal(offset, actual.Extent.Offset); Assert.Equal(4, actual.Extent.Length);
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(actual.GetValue()));
    }

    private static void AssertBounds(IblStoredBounds actual, uint[] words, long offset)
    {
        Assert.Equal(6, words.Length);
        AssertFloat(actual.MinimumX, words[0], offset);
        AssertFloat(actual.MaximumX, words[1], offset + 4);
        AssertFloat(actual.MinimumY, words[2], offset + 8);
        AssertFloat(actual.MaximumY, words[3], offset + 12);
        AssertFloat(actual.MinimumZ, words[4], offset + 16);
        AssertFloat(actual.MaximumZ, words[5], offset + 20);
    }

    private static Fixture CreateFixture(int referenceCount, int tagOffset = 7, uint firstSpecularIndex = 3,
        uint[]? influenceWords = null, uint[]? reflectionWords = null,
        uint attenuationStart = 0x3F800001, uint intensity = 0x3F800002,
        uint scalar77 = 0x3F800003, uint scalar83 = 0x3F800004,
        uint trailing0 = 0x3F800005, uint trailing1 = 0x3F800006,
        (uint Type, uint Id) storedReference = default,
        IReadOnlyList<(uint Type, uint Id)>? references = null,
        IReadOnlyList<SourceExtent>? physicalExtents = null,
        byte enabled = 0xA5, byte specularEnabled = 0x7E, byte byte82 = 0x91,
        string owner = "Owner", string path = "assets/probe.bin")
    {
        if (referenceCount < 0 || referenceCount > TextureContextReader.MaximumReferences)
            throw new ArgumentOutOfRangeException(nameof(referenceCount));
        var pairs = references?.ToArray() ?? Enumerable.Range(0, referenceCount)
            .Select(index => ((uint)(1000 + index), (uint)(2000 + index))).ToArray();
        if (pairs.Length != referenceCount) throw new ArgumentException("The reference fixture count must match.", nameof(references));
        influenceWords ??= [Bits(1), Bits(2), Bits(3), Bits(4), Bits(5), Bits(6)];
        reflectionWords ??= [Bits(11), Bits(12), Bits(13), Bits(14), Bits(15), Bits(16)];
        if (influenceWords.Length != 6 || reflectionWords.Length != 6) throw new ArgumentException("Bounds need six scalar words.");
        if (storedReference == default) storedReference = (3001, 4002);

        var ownerBytes = Encoding.ASCII.GetBytes(owner);
        var pathBytes = Encoding.ASCII.GetBytes(path);
        int firstIndexOffset = checked(95 + referenceCount * 8);
        int ownerLengthOffset = firstIndexOffset + 4;
        int ownerValueOffset = ownerLengthOffset + 4;
        int pathLengthOffset = ownerValueOffset + ownerBytes.Length;
        int pathValueOffset = pathLengthOffset + 4;
        int trailingOffset = pathValueOffset + pathBytes.Length;
        var record = new byte[trailingOffset + 8];

        "IBLLIGHT"u8.CopyTo(record);
        WriteWord(record, 8, 13);
        record[12] = enabled;
        for (int index = 0; index < 6; index++) WriteWord(record, 13 + index * 4, influenceWords[index]);
        for (int index = 0; index < 6; index++) WriteWord(record, 37 + index * 4, reflectionWords[index]);
        WriteWord(record, 61, storedReference.Type); WriteWord(record, 65, storedReference.Id);
        WriteWord(record, 69, attenuationStart); WriteWord(record, 73, intensity); WriteWord(record, 77, scalar77);
        record[81] = specularEnabled; record[82] = byte82; WriteWord(record, 83, scalar83);
        WriteWord(record, 87, checked((uint)referenceCount)); WriteWord(record, 91, 0x92A4B6C8);
        for (int index = 0; index < pairs.Length; index++)
        {
            WriteWord(record, 95 + index * 8, pairs[index].Type);
            WriteWord(record, 99 + index * 8, pairs[index].Id);
        }
        WriteWord(record, firstIndexOffset, firstSpecularIndex);
        WriteWord(record, ownerLengthOffset, checked((uint)ownerBytes.Length));
        ownerBytes.CopyTo(record, ownerValueOffset);
        WriteWord(record, pathLengthOffset, checked((uint)pathBytes.Length));
        pathBytes.CopyTo(record, pathValueOffset);
        WriteWord(record, trailingOffset, trailing0); WriteWord(record, trailingOffset + 4, trailing1);

        const string indexId = "generated-index";
        var container = new ResourceIdentity(indexId, 17);
        var identity = new ResourceIdentity(indexId, 17, "member/17");
        var revision = new SourceRevision(indexId, "generated-revision");
        var bodyOffset = 0x2400L;
        var body = new byte[tagOffset + record.Length + 2];
        record.CopyTo(body, tagOffset);
        var bodyExtent = new DecodedExtent(container, bodyOffset, body.Length);
        var physical = physicalExtents?.ToArray() ?? [new SourceExtent("volume-a", 200, 24, new string('a', 64))];
        var parsed = new TextureContextReader().ParseIblLightV13(body, tagOffset, tagOffset + record.Length,
            identity, revision, bodyExtent, physical);
        var context = Assert.IsType<TextureIblLightV13Context>(parsed.Value);
        return new(context, container, identity, revision, bodyOffset + tagOffset, record, physical);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed record Fixture(TextureIblLightV13Context Context, ResourceIdentity Container,
        ResourceIdentity Identity, SourceRevision Revision, long ClassOffset, byte[] Record,
        IReadOnlyList<SourceExtent> PhysicalExtents);
}
