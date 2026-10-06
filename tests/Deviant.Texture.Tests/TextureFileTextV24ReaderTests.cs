using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureFileTextV24ReaderTests
{
    [Fact]
    public void ParsesBoundedV24LodSizesReferencesAndExactBoundary()
    {
        var pair0 = new TestReference(2229, 41, 349696);
        var pair1 = new TestReference(2230, 42, 4194304);
        var pair2 = new TestReference(2230, 43, 1048576);
        var bytes = BuildRecord(selector: 1, descriptors:
        [
            new(1024, 1024, 1, 11),
            new(512, 512, 1, 10),
            new(256, 256, 1, 9)
        ], width: 1024, height: 1024, mipCount: 3,
            mipSizeGroups: [[4194304, 1048576, 262144]], references: [pair0, pair1, pair2]);
        var context = CreateContext(bytes, [pair0, pair1, pair2]);

        var result = Parse(bytes, context);

        Assert.NotNull(result.Value);
        Assert.Equal("filetext/1024", result.Identity.MemberPath);
        Assert.Equal(new DecodedExtent(context.Container, 1024, bytes.Length), result.Value.ExactConsumedDecodedExtent);
        Assert.Equal((ushort)1024, result.Value.Mip0Width);
        Assert.Equal((ushort)1024, result.Value.Mip0Height);
        Assert.Equal((byte)3, result.Value.MipSizeCount);
        Assert.Equal(0xB58180CAu, result.Value.NativeRegistryKey);
        Assert.Equal(3, result.Value.LodDescriptors.Count);
        Assert.Equal((ushort)512, result.Value.LodDescriptors[1].Width);
        Assert.Equal((byte)10, result.Value.LodDescriptors[1].TrailingByte);
        Assert.Equal(new uint[] { 4194304, 1048576, 262144 }, result.Value.MipByteLengthGroups[0]);
        Assert.Collection(result.Value.References,
            reference => AssertMapped(reference, pair0, 0),
            reference => AssertMapped(reference, pair1, 1),
            reference => AssertMapped(reference, pair2, 2));
        Assert.Equal(new ResourceIdentity("synthetic-filetext-index", 2), result.Value.References[0].CatalogOwner);
        Assert.Equal(result.Identity, result.Value.References[0].Reference.Owner);
        Assert.Equal(context.PhysicalExtents, result.Value.OriginalPhysicalExtents);
    }

    [Fact]
    public void ParsesOneReferenceWhenSelectorDoesNotProvideDescriptors()
    {
        var reference = new TestReference(2229, 17, 87552);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 128, height: 128,
            mipCount: 2, mipSizeGroups: [[87552, 16384]], references: [reference]);
        var context = CreateContext(bytes, [reference]);

        var result = Parse(bytes, context);

        Assert.NotNull(result.Value);
        Assert.Equal((byte)0, result.Value.SerializedSelector);
        Assert.Empty(result.Value.LodDescriptors);
        Assert.Single(result.Value.References);
        AssertMapped(result.Value.References[0], reference, 0);
    }

    [Fact]
    public void ParsesSixSizeGroupsWhenNativeSelectorRequestsSix()
    {
        var reference = new TestReference(2229, 70, 10);
        uint[][] groups = [[1, 2], [3, 4], [5, 6], [7, 8], [9, 10], [11, 12]];
        var bytes = BuildRecord(selector: 1, descriptors: [new(64, 64, 1, 7)], width: 64,
            height: 64, mipCount: 2, sizeGroupSelector: 2, mipSizeGroups: groups,
            references: [reference]);
        var context = CreateContext(bytes, [reference]);

        var result = Parse(bytes, context);

        Assert.NotNull(result.Value);
        Assert.Equal(6, result.Value.MipByteLengthGroups.Count);
        Assert.Equal(new uint[] { 11, 12 }, result.Value.MipByteLengthGroups[5]);
    }

    [Fact]
    public void SkipsMipSizeTablesWhenNativeFlagIsSet()
    {
        var reference = new TestReference(2229, 8, 10);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 8, height: 8,
            mipCount: 2, skipMipSizeTables: true, mipSizeGroups: [], references: [reference]);
        var context = CreateContext(bytes, [reference]);

        var result = Parse(bytes, context);

        Assert.NotNull(result.Value);
        Assert.True(result.Value.SkipLodSizeTables);
        Assert.Empty(result.Value.MipByteLengthGroups);
        Assert.Single(result.Value.References);
    }

    [Fact]
    public void KeepsMissingAndDuplicateCatalogEvidenceExplicit()
    {
        var missing = new TestReference(2230, 88, 256);
        var missingBytes = BuildRecord(selector: 0, descriptors: [], width: 16, height: 16,
            mipCount: 1, mipSizeGroups: [[256]], references: [missing]);
        var missingContext = CreateContext(missingBytes, []);
        var missingResult = Parse(missingBytes, missingContext);
        Assert.NotNull(missingResult.Value);
        Assert.Equal(ReferenceStatus.Missing, missingResult.Value.References[0].Resolution.Status);
        Assert.Contains(missingResult.UnresolvedReferences, item => item.ResourceType == 2230 && item.Id == "88");

        var duplicate = new TestReference(2229, 19, 10);
        var duplicateBytes = BuildRecord(selector: 0, descriptors: [], width: 8, height: 8,
            mipCount: 1, mipSizeGroups: [[10]], references: [duplicate]);
        var duplicateContext = CreateContext(duplicateBytes, [duplicate], duplicateCandidates: true);
        var duplicateResult = Parse(duplicateBytes, duplicateContext);
        Assert.NotNull(duplicateResult.Value);
        var mapped = duplicateResult.Value.References[0];
        Assert.Equal(ReferenceStatus.Ambiguous, mapped.Resolution.Status);
        Assert.Equal(new[] { 0, 1 }, mapped.CatalogReferenceIndices);
        Assert.Equal(2, mapped.Resolution.Candidates.Count);
    }

    [Fact]
    public void RequiresExactTypeAndIdCatalogPair()
    {
        var serialized = new TestReference(2229, 24, 100);
        var differentIdCatalogRow = new TestReference(2229, 25, 100);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 8, height: 8,
            mipCount: 1, mipSizeGroups: [[100]], references: [serialized]);
        var context = CreateContext(bytes, [differentIdCatalogRow]);

        var result = Parse(bytes, context);

        Assert.NotNull(result.Value);
        Assert.Equal(ReferenceStatus.Missing, result.Value.References[0].Resolution.Status);
        Assert.Empty(result.Value.References[0].CatalogReferenceIndices);
    }

    [Fact]
    public void ReturnsUnsupportedForUnknownVersionTruncationAndBoundaryMismatch()
    {
        var reference = new TestReference(2229, 2, 1);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 4, height: 4,
            mipCount: 1, mipSizeGroups: [[1]], references: [reference]);
        var context = CreateContext(bytes, [reference]);

        var unknownVersion = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(unknownVersion.AsSpan(8, 4), 25);
        AssertUnsupported(Parse(unknownVersion, context).Value);

        AssertUnsupported(Parse(bytes, context with { MaximumEnd = bytes.Length - 1 }).Value);
        AssertUnsupported(Parse(bytes, context with { MaximumEnd = 96 }).Value);
    }

    [Fact]
    public void ReturnsUnsupportedWhenSourceIdentityOrCatalogShapeDoesNotMatch()
    {
        var reference = new TestReference(2229, 2, 1);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 4, height: 4,
            mipCount: 1, mipSizeGroups: [[1]], references: [reference]);
        var context = CreateContext(bytes, [reference]);
        var foreignRevision = context.Revision with { PhysicalSourceStamp = "other-source" };
        AssertUnsupported(Parse(bytes, context with { Revision = foreignRevision }).Value);

        var invalid = context.Membership with
        {
            TextureReferences = [context.Membership.TextureReferences[0] with
            {
                Reference = context.Membership.TextureReferences[0].Reference with { Owner = new ResourceIdentity("other", 2) }
            }]
        };
        AssertUnsupported(Parse(bytes, context with { Membership = invalid }).Value);
    }

    [Fact]
    public void EnforcesRecordAndMipTableCapsAndHonorsCancellation()
    {
        var reference = new TestReference(2229, 2, 1);
        var bytes = BuildRecord(selector: 0, descriptors: [], width: 4, height: 4,
            mipCount: 1, mipSizeGroups: [[1]], references: [reference]);
        var context = CreateContext(bytes, [reference]);
        var tooLarge = new byte[TextureFileTextV24Reader.MaximumRecordBytes + 1];
        "FILETEXT"u8.CopyTo(tooLarge);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(8, 4), 24);
        var largeContext = CreateContext(tooLarge, [reference]);
        AssertUnsupported(Parse(tooLarge, largeContext).Value);

        var tooManyMipSizes = BuildRecord(selector: 0, descriptors: [], width: 4, height: 4,
            mipCount: 255, sizeGroupSelector: 2, mipSizeGroups: [], references: [reference],
            includeMipSizes: false);
        var capContext = CreateContext(tooManyMipSizes, [reference]);
        AssertUnsupported(Parse(tooManyMipSizes, capContext).Value);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Parse(bytes, context, cancellation.Token));
    }

    private static void AssertMapped(TextureFileTextV24ResourceReference actual,
        TestReference expected, int catalogReferenceIndex)
    {
        Assert.Equal(expected.Type, actual.Reference.ResourceType);
        Assert.Equal(expected.Id.ToString(), actual.Reference.Id);
        Assert.Equal(expected.FollowingValue, actual.FollowingUInt32);
        Assert.Equal(new[] { catalogReferenceIndex }, actual.CatalogReferenceIndices);
        Assert.Equal(ReferenceStatus.Unique, actual.Resolution.Status);
        Assert.Single(actual.Resolution.Candidates);
    }

    private static void AssertUnsupported(TextureFileTextV24Document? value) => Assert.Null(value);

    private static DecodeResult<TextureFileTextV24Document> Parse(byte[] bytes, ParseContext context,
        CancellationToken cancellationToken = default) => new TextureFileTextV24Reader().Parse(bytes,
        context.TagOffset, context.MaximumEnd, context.Container, context.Revision, context.BodyExtent,
        context.PhysicalExtents, context.Membership, cancellationToken);

    private static ParseContext CreateContext(byte[] bytes, IReadOnlyList<TestReference> references,
        bool duplicateCandidates = false)
    {
        const string indexId = "synthetic-filetext-index";
        var container = new ResourceIdentity(indexId, 6);
        var catalog = new ResourceIdentity(indexId, 2);
        var revision = new SourceRevision(indexId, "synthetic-source-stamp");
        var physicalExtents = new[] { new SourceExtent("archive-volume.dat", 0x1000, 0x2000) };
        var candidates = duplicateCandidates
            ? new[] { new ResourceIdentity(indexId, 12), new ResourceIdentity(indexId, 13) }
            : new[] { new ResourceIdentity(indexId, 12) };
        var catalogRows = references.Select((reference, index) => CreateCatalogRow(catalog, reference,
            index, duplicateCandidates, candidates)).ToList();
        if (duplicateCandidates && references.Count > 0)
            catalogRows.Add(CreateCatalogRow(catalog, references[0], catalogRows.Count, true,
                [new ResourceIdentity(indexId, 13)]));
        var membership = new TextureCatalogMembership(catalog, revision, 2,
            new SourceExtent("archive-volume.dat", 0x200, 468), catalogRows.Count, 0, catalogRows);
        var bodyExtent = new DecodedExtent(container, 0x400, bytes.Length);
        return new ParseContext(0, bytes.Length, container, revision, bodyExtent, physicalExtents, membership);
    }

    private static TextureCatalogReference CreateCatalogRow(ResourceIdentity catalog, TestReference item,
        int index, bool duplicate, IReadOnlyList<ResourceIdentity> candidates)
    {
        var kind = item.Type == 2229 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod;
        var reference = new ResourceReference(IdentifierNamespace.Archive, item.Id.ToString(), catalog, item.Type);
        var status = duplicate ? ReferenceStatus.Ambiguous : ReferenceStatus.Unique;
        var evidence = new SourceExtent("archive-volume.dat", 0x300 + index * 9, 9);
        return new TextureCatalogReference(index, kind, reference, status, candidates,
            0, evidence, duplicate, null);
    }

    private static byte[] BuildRecord(byte selector, IReadOnlyList<TestDescriptor> descriptors,
        ushort width, ushort height, byte mipCount, IReadOnlyList<uint[]> mipSizeGroups,
        IReadOnlyList<TestReference> references, byte sizeGroupSelector = 1, bool skipMipSizeTables = false,
        bool includeMipSizes = true)
    {
        var bytes = new List<byte>(256);
        bytes.AddRange(Encoding.ASCII.GetBytes("FILETEXT"));
        AppendUInt32(bytes, 24);
        var prefix = new byte[TextureFileTextV24Reader.FixedPrefixLength];
        prefix[0] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), height);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = mipCount;
        prefix[8] = 0xA1;
        prefix[9] = 0xB2;
        prefix[10] = 0xC3;
        prefix[11] = 0xD4;
        prefix[12] = sizeGroupSelector;
        prefix[13] = skipMipSizeTables ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(16), 0xB58180CA);
        for (var i = 27; i < prefix.Length; i++) prefix[i] = checked((byte)(i ^ 0x5A));
        bytes.AddRange(prefix);
        bytes.Add(selector);
        if (selector == 1)
        {
            bytes.Add(0x31);
            bytes.Add(checked((byte)descriptors.Count));
            foreach (var descriptor in descriptors)
            {
                AppendUInt16(bytes, descriptor.Width);
                AppendUInt16(bytes, descriptor.Height);
                AppendUInt16(bytes, descriptor.ThirdU16);
                bytes.Add(descriptor.TrailingByte);
            }
        }
        bytes.Add(1);
        bytes.Add(0x5A);
        bytes.Add(0x6B);
        if (!skipMipSizeTables && includeMipSizes)
            foreach (var group in mipSizeGroups)
                foreach (var size in group) AppendUInt32(bytes, size);
        foreach (var reference in references)
        {
            AppendUInt32(bytes, reference.Type);
            AppendUInt32(bytes, reference.Id);
            AppendUInt32(bytes, reference.FollowingValue);
        }
        return bytes.ToArray();
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private sealed record TestReference(uint Type, uint Id, uint FollowingValue);
    private sealed record TestDescriptor(ushort Width, ushort Height, ushort ThirdU16, byte TrailingByte);
    private sealed record ParseContext(int TagOffset, int MaximumEnd, ResourceIdentity Container,
        SourceRevision Revision, DecodedExtent BodyExtent, IReadOnlyList<SourceExtent> PhysicalExtents,
        TextureCatalogMembership Membership);
}
