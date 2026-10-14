using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeProceduralTextureReaderTests
{
    [Fact]
    public void ReadsZeroPayloadProfileAsRawFieldsAndPreservesFiveUnboundPairs()
    {
        var fixture = Fixture.Create();
        var result = Read(fixture);

        Assert.NotNull(result.Value);
        var document = result.Value!;
        Assert.Equal(NativeProceduralTextureReader.Version, Assert.Single(result.Steps).Version);
        Assert.Equal(fixture.Frame.Identity, document.Identity);
        Assert.Equal(fixture.Revision, document.Revision);
        Assert.Equal(3u, document.CommonPrefix.ClassCount);
        Assert.Equal(32, document.CommonPrefix.NextClassOffset);
        Assert.Equal(7u, document.Version);
        Assert.Equal(2u, document.SamplerWord);
        Assert.Equal(0u, document.OutputModeWord);
        Assert.Equal(0x3CA3D70Au, document.PowerController.RawValueWord);
        Assert.Equal(0x3C23D70Au, document.SpeedController.RawValueWord);
        Assert.Equal(0u, document.PowerController.PayloadLengthWord);
        Assert.Equal(0u, document.SpeedController.PayloadLengthWord);
        Assert.Equal(4, document.TextureReferences.Count);

        var pairs = new[] { document.TypedReference }.Concat(document.TextureReferences).ToArray();
        Assert.Equal(5, pairs.Length);
        Assert.All(pairs, pair =>
        {
            Assert.Equal(0u, pair.NativeTypeWord);
            Assert.Equal(0u, pair.NativeIdWord);
            Assert.Equal(NativeProceduralTextureReferenceState.NoTarget, pair.State);
            Assert.Null(pair.Reference);
            Assert.Equal(8, pair.Extent.Length);
        });
        Assert.Equal(fixture.Frame.PayloadExtent, document.Extent);
        Assert.Equal(108, document.Extent.Length);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void CommonPrefixReferencesMoveClassCursorWithoutChangingProfileRelativeLayout()
    {
        var fixture = Fixture.Create(commonReferencePairs: [new(91, 700, 1)]);
        var result = Read(fixture);

        Assert.NotNull(result.Value);
        var document = result.Value!;
        Assert.Single(document.CommonPrefix.References);
        Assert.Equal(41, document.CommonPrefix.NextClassOffset);
        Assert.Equal(fixture.Frame.PayloadExtent.Offset + 41,
            document.ProfileExtent.Offset);
        Assert.Equal(117, document.Extent.Length);
        Assert.Equal(4, document.TextureReferences.Count);
        Assert.Equal(document.ProfileExtent.Offset + 28,
            document.PowerController.LengthExtent.Offset);
        Assert.Equal(document.ProfileExtent.Offset + 68,
            document.SpeedController.LengthExtent.Offset);
        Assert.Equal(document.Extent.Offset + document.Extent.Length,
            document.SpeedController.RawValueExtent.Offset + document.SpeedController.RawValueExtent.Length);
    }

    [Fact]
    public void NonemptyControllerBytesRemainOpaqueAndShiftLaterFieldsExactly()
    {
        byte[] first = [0x00, 0xFF, 0x80];
        byte[] second = [0xF1, 0x00];
        var references = new[] { new ReferenceWords(2137, 51), new ReferenceWords(2137, 52),
            new ReferenceWords(2137, 53), new ReferenceWords(2137, 54) };
        var fixture = Fixture.Create(powerPayload: first, speedPayload: second,
            textureReferences: references, powerValue: 0x7FC00011, speedValue: 0xDEADBEEF);

        var result = Read(fixture);

        Assert.NotNull(result.Value);
        var document = result.Value!;
        Assert.Equal(3u, document.PowerController.PayloadLengthWord);
        Assert.Equal(first, document.PowerController.PayloadBytes.ToArray());
        Assert.Equal(2u, document.SpeedController.PayloadLengthWord);
        Assert.Equal(second, document.SpeedController.PayloadBytes.ToArray());
        Assert.Equal(0x7FC00011u, document.PowerController.RawValueWord);
        Assert.Equal(0xDEADBEEFu, document.SpeedController.RawValueWord);
        Assert.Equal(4, document.TextureReferences.Count);
        Assert.Equal(references.Select(item => item.Type).ToArray(),
            document.TextureReferences.Select(item => item.NativeTypeWord).ToArray());
        Assert.Equal(references.Select(item => item.Id).ToArray(),
            document.TextureReferences.Select(item => item.NativeIdWord).ToArray());
        Assert.Equal(fixture.Frame.RawPayload.Length, document.Extent.Length);
        Assert.Equal(fixture.Frame.PayloadExtent.Offset + fixture.Frame.RawPayload.Length,
            document.SpeedController.RawValueExtent.Offset + document.SpeedController.RawValueExtent.Length);
    }

    [Fact]
    public void PayloadMemoriesBorrowFrameAndAreExcludedFromJson()
    {
        var fixture = Fixture.Create(powerPayload: [1, 2, 3]);
        var document = Assert.IsType<NativeProceduralTextureDocument>(Read(fixture).Value);
        Assert.True(MemoryMarshal.TryGetArray(document.PowerController.PayloadBytes, out var segment));
        Assert.Same(fixture.Payload, segment.Array);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document));
        Assert.False(json.RootElement.TryGetProperty("RawPayload", out _));
        Assert.False(json.RootElement.GetProperty("PowerController").TryGetProperty("PayloadBytes", out _));
        Assert.False(json.RootElement.GetProperty("SpeedController").TryGetProperty("PayloadBytes", out _));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("ambiguous")]
    public void TypedReferencePairsUseNativeResolverWithoutSelectingFirst(string resolution)
    {
        var fixture = Fixture.Create(typedReference: new(2137, 42));
        var document = Assert.IsType<NativeProceduralTextureDocument>(Read(fixture).Value);
        var reference = Assert.IsType<ResourceReference>(document.TypedReference.Reference);
        Assert.Equal(IdentifierNamespace.Native, reference.Namespace);
        Assert.Equal(fixture.Frame.Identity, reference.Owner);
        Assert.Equal(2137u, reference.ResourceType);
        Assert.Equal("42", reference.Id);

        var objects = new List<NativeObjectFrame> { fixture.Frame };
        if (resolution is "type" or "ambiguous")
            objects.Add(Fixture.NativeMember(fixture.Container, fixture.Revision, 10, 2137, 42));
        if (resolution == "ambiguous")
            objects.Add(Fixture.NativeMember(fixture.Container, fixture.Revision, 11, 2137, 42));
        if (resolution == "id")
            objects.Add(Fixture.NativeMember(fixture.Container, fixture.Revision, 12, 2137, 41));
        var directory = Fixture.Directory(fixture.Container, fixture.Revision, objects);
        var resolved = new NativeReferenceResolver(directory).Resolve(reference);
        Assert.Equal(resolution == "type" ? ReferenceStatus.Unique :
            resolution == "ambiguous" ? ReferenceStatus.Ambiguous : ReferenceStatus.Missing, resolved.Status);
        Assert.Equal(resolution == "type" ? 1 : resolution == "ambiguous" ? 2 : 0,
            resolved.Candidates.Count);
    }

    [Fact]
    public void RetainsUnboundAndSentinelPairsWithoutInventingReferences()
    {
        var fixture = Fixture.Create(typedReference: new(0, 0),
            textureReferences: [new(0, uint.MaxValue), new(uint.MaxValue, 0), new(0, 0), new(2137, uint.MaxValue)]);
        var document = Assert.IsType<NativeProceduralTextureDocument>(Read(fixture).Value);
        Assert.Equal(NativeProceduralTextureReferenceState.NoTarget, document.TypedReference.State);
        Assert.Null(document.TypedReference.Reference);
        Assert.Equal(new uint[] { 0, uint.MaxValue, 0, 2137 },
            document.TextureReferences.Select(item => item.NativeTypeWord).ToArray());
        Assert.Equal(new uint[] { uint.MaxValue, 0, 0, uint.MaxValue },
            document.TextureReferences.Select(item => item.NativeIdWord).ToArray());
        Assert.All(document.TextureReferences, item => Assert.Equal(8, item.Extent.Length));
    }

    [Fact]
    public void PreservesNonfiniteValueWordBitsWithoutFloatClassification()
    {
        var fixture = Fixture.Create(powerValue: 0x7FC00001, speedValue: 0xFF800000);
        var document = Assert.IsType<NativeProceduralTextureDocument>(Read(fixture).Value);
        Assert.Equal(0x7FC00001u, document.PowerController.RawValueWord);
        Assert.Equal(0xFF800000u, document.SpeedController.RawValueWord);
    }

    [Theory]
    [InlineData("native-type")]
    [InlineData("class-count")]
    [InlineData("tag")]
    [InlineData("version")]
    [InlineData("member-path")]
    [InlineData("revision-index")]
    [InlineData("empty-stamp")]
    [InlineData("payload-extent")]
    public void RejectsWrongProfileIdentityAndRevision(string mutation)
    {
        var fixture = Fixture.Create();
        var frame = fixture.Frame;
        var revision = fixture.Revision;
        switch (mutation)
        {
            case "native-type": frame = frame with { NativeType = 2002 }; break;
            case "class-count": frame = frame with { ClassCount = 2 }; break;
            case "tag": fixture.Payload.AsSpan(fixture.ClassStart, 8).Fill((byte)'X'); break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(fixture.Payload.AsSpan(fixture.ClassStart + 8), 6); break;
            case "member-path": frame = frame with { Identity = frame.Identity with { MemberPath = "dc-object/04/2217/5" } }; break;
            case "revision-index": revision = revision with { IndexContentId = "another-index" }; break;
            case "empty-stamp": revision = revision with { PhysicalSourceStamp = "" }; break;
            case "payload-extent": frame = frame with { PayloadExtent = frame.PayloadExtent with { Length = frame.PayloadExtent.Length - 1 } }; break;
        }

        var result = new NativeProceduralTextureReader().Read(frame, revision, fixture.PhysicalExtents);

        Assert.Null(result.Value);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RejectsTruncatedAndUnexpectedTrailingFrameBytes()
    {
        var truncated = Fixture.Create();
        truncated.Payload = truncated.Payload[..^1];
        var shortFrame = truncated.WithPayload(truncated.Payload);
        Assert.Null(Read(shortFrame).Value);

        var trailing = Fixture.Create();
        trailing.Payload = [.. trailing.Payload, 0xCC];
        Assert.Null(Read(trailing.WithPayload(trailing.Payload)).Value);
    }

    [Fact]
    public void RejectsControllerLengthsBeforeSlicingOrAllocatingProjection()
    {
        var negative = Fixture.Create();
        BinaryPrimitives.WriteUInt32LittleEndian(negative.Payload.AsSpan(negative.ClassStart + 28), uint.MaxValue);
        Assert.Null(Read(negative.WithPayload(negative.Payload)).Value);

        var oversized = Fixture.Create(powerPayload: new byte[9]);
        var limits = new NativeProceduralTextureReadLimits(MaximumControllerPayloadBytes: 8);
        var oversizedResult = Read(oversized, limits);
        Assert.Null(oversizedResult.Value);
        Assert.Contains(oversizedResult.Diagnostics, item => item.Contains("Limit", StringComparison.Ordinal));

        var aggregate = Fixture.Create(powerPayload: new byte[4], speedPayload: new byte[4]);
        var aggregateResult = Read(aggregate, new(MaximumControllerPayloadBytes: 8,
            MaximumAggregateControllerPayloadBytes: 7));
        Assert.Null(aggregateResult.Value);
        Assert.Contains(aggregateResult.Diagnostics, item => item.Contains("Limit", StringComparison.Ordinal));
    }

    [Fact]
    public void EnforcesFrameCommonPrefixAndAggregateProvenanceCaps()
    {
        var fixture = Fixture.Create(commonReferencePairs: [new(1, 2, 0), new(3, 4, 0)]);
        var result = Read(fixture, new(MaximumCommonPrefixReferences: 1));
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, item => item.Contains("Limit", StringComparison.Ordinal));

        var source = Fixture.Create();
        var provenanceResult = new NativeProceduralTextureReader().Read(source.Frame, source.Revision,
            source.PhysicalExtents, new(MaximumSourceProvenanceBytes: 1));
        Assert.Null(provenanceResult.Value);
        Assert.Contains(provenanceResult.Diagnostics, item => item.Contains("Limit", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecksLimitsForPayloadSizePhysicalExtentsAndReaderCancellation()
    {
        var fixture = Fixture.Create();
        var oversizedPayloadLimit = Read(fixture, new(MaximumPayloadBytes: fixture.Payload.Length - 1));
        Assert.Null(oversizedPayloadLimit.Value);

        var sourceLimit = new NativeProceduralTextureReader().Read(fixture.Frame, fixture.Revision,
            fixture.PhysicalExtents, new(MaximumPhysicalSourceExtents: 0));
        Assert.Null(sourceLimit.Value);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeProceduralTextureReader().Read(
            fixture.Frame, fixture.Revision, fixture.PhysicalExtents, token: canceled.Token));
    }

    [Fact]
    public void RejectsUnsafeSourceExtentPathAndAggregateSourceByteLimit()
    {
        var fixture = Fixture.Create();
        var unsafeExtent = new SourceExtent("../source.dat", 0, 1, new string('a', 64));
        var unsafeResult = new NativeProceduralTextureReader().Read(fixture.Frame, fixture.Revision, [unsafeExtent]);
        Assert.Null(unsafeResult.Value);
        Assert.Contains(unsafeResult.Diagnostics, item => item.Contains("Source", StringComparison.Ordinal));

        var byteLimitResult = new NativeProceduralTextureReader().Read(fixture.Frame, fixture.Revision,
            fixture.PhysicalExtents, new(MaximumSourceBytes: 1));
        Assert.Null(byteLimitResult.Value);
        Assert.Contains(byteLimitResult.Diagnostics, item => item.Contains("Limit", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturesAndValidatesEachPhysicalExtentExactlyOnce()
    {
        var fixture = Fixture.Create();
        var supplied = new SourceExtent("source-one.dat", 11, 29, new string('b', 64));
        var changing = new ChangingExtentList(supplied,
            new SourceExtent("../changed-on-second-read.dat", -1, -1, null));

        var result = new NativeProceduralTextureReader().Read(fixture.Frame, fixture.Revision, changing);

        var document = Assert.IsType<NativeProceduralTextureDocument>(result.Value);
        Assert.Equal(1, changing.IndexReads);
        Assert.Equal(supplied, Assert.Single(document.OriginalPhysicalExtents));
        Assert.Equal(supplied, Assert.Single(result.Extents));
        Assert.True(document.AccountedMetadataBytes > 0);
        Assert.InRange(document.AccountedMetadataBytes, 1, 64 * 1024);
    }

    private static DecodeResult<NativeProceduralTextureDocument> Read(Fixture fixture,
        NativeProceduralTextureReadLimits? limits = null) =>
        new NativeProceduralTextureReader().Read(fixture.Frame, fixture.Revision, fixture.PhysicalExtents, limits);

    private sealed record ReferenceWords(uint Type, uint Id, byte Trailer = 0);

    private sealed class ChangingExtentList(SourceExtent first, SourceExtent later) : IReadOnlyList<SourceExtent>
    {
        public int IndexReads { get; private set; }
        public int Count => 1;
        public SourceExtent this[int index]
        {
            get
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                return ++IndexReads == 1 ? first : later;
            }
        }
        public IEnumerator<SourceExtent> GetEnumerator() => Enumerable.Range(0, Count).Select(index => this[index]).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        public required ResourceIdentity Container { get; init; }
        public required SourceRevision Revision { get; init; }
        public required NativeObjectFrame Frame { get; set; }
        public required byte[] Payload { get; set; }
        public required int ClassStart { get; init; }
        public required IReadOnlyList<SourceExtent> PhysicalExtents { get; init; }

        public static Fixture Create(ReferenceWords[]? commonReferencePairs = null, ReferenceWords? typedReference = null,
            ReferenceWords[]? textureReferences = null, byte[]? powerPayload = null, byte[]? speedPayload = null,
            uint samplerWord = 2, uint outputMode = 0,
            uint powerValue = 0x3CA3D70A, uint speedValue = 0x3C23D70A)
        {
            const string index = "generated-proctex-index";
            const long ordinal = 37;
            const int directoryIndex = 4;
            const uint nativeId = 5;
            var container = new ResourceIdentity(index, ordinal);
            var revision = new SourceRevision(index, "generated-proctex-source-stamp");
            commonReferencePairs ??= [];
            textureReferences ??= [new(0, 0), new(0, 0), new(0, 0), new(0, 0)];
            typedReference ??= new(0, 0);
            powerPayload ??= [];
            speedPayload ??= [];
            if (textureReferences.Length != 4) throw new ArgumentOutOfRangeException(nameof(textureReferences));

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write((uint)commonReferencePairs.Length);
            foreach (var commonReference in commonReferencePairs)
            {
                writer.Write(commonReference.Type); writer.Write(commonReference.Id); writer.Write(commonReference.Trailer);
            }
            writer.Write("LOADCONT"u8); writer.Write(2u);
            var classStart = checked((int)stream.Position);
            writer.Write("PROCTEX_"u8); writer.Write(7u);
            writer.Write(samplerWord); writer.Write(outputMode);
            WritePair(writer, typedReference);
            writer.Write((uint)powerPayload.Length); writer.Write(powerPayload); writer.Write(powerValue);
            foreach (var reference in textureReferences) WritePair(writer, reference);
            writer.Write((uint)speedPayload.Length); writer.Write(speedPayload); writer.Write(speedValue);
            var payload = stream.ToArray();
            var identity = new ResourceIdentity(index, ordinal,
                $"dc-object/{directoryIndex}/2217/{nativeId}");
            var payloadExtent = new DecodedExtent(container, 200, payload.Length);
            var frame = new NativeObjectFrame(identity, directoryIndex, 2217, nativeId,
                new(container, 10, 8), new(container, 188, 12), payloadExtent,
                new byte[12], payload, 0, 0, 0, 3);
            var sourceExtents = new[] { new SourceExtent("archive.dat", 0, 4096, new string('a', 64)) };
            return new() { Container = container, Revision = revision, Frame = frame,
                Payload = payload, ClassStart = classStart, PhysicalExtents = sourceExtents };
        }

        public Fixture WithPayload(byte[] payload)
        {
            Payload = payload;
            Frame = Frame with { RawPayload = payload, PayloadExtent = Frame.PayloadExtent with { Length = payload.Length } };
            return this;
        }

        public static NativeObjectFrame NativeMember(ResourceIdentity container, SourceRevision revision,
            int directoryIndex, uint nativeType, uint nativeId)
        {
            var identity = new ResourceIdentity(container.IndexContentId, container.Ordinal,
                $"dc-object/{directoryIndex}/{nativeType}/{nativeId}");
            return new(identity, directoryIndex, nativeType, nativeId,
                new(container, directoryIndex * 8, 8), new(container, 500 + directoryIndex * 8, 12),
                new(container, 512 + directoryIndex * 8, 4), new byte[12], new byte[4], 0, 0, 0, 1);
        }

        public static NativeObjectDirectoryDocument Directory(ResourceIdentity container, SourceRevision revision,
            IReadOnlyList<NativeObjectFrame> objects) => new(container, revision,
                new(container, 0, 100), new(container, 100, 100), [], new byte[100], objects,
                null, ReadOnlyMemory<byte>.Empty);

        private static void WritePair(BinaryWriter writer, ReferenceWords reference)
        { writer.Write(reference.Type); writer.Write(reference.Id); }
    }
}
