using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Export.Animation;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationDataTests
{
    private static readonly ResourceIdentity Identity = new("index", 7);
    private static readonly SourceRevision Revision = new("index", "private-stamp");

    [Theory]
    [InlineData(4, 13)]
    [InlineData(5, 17)]
    [InlineData(6, 13)]
    public void ReadsFramingWithoutHardcodedBodyOffsets(int containerVersion, int prefixLength)
    {
        var encoded = Fixture((uint)containerVersion, prefixLength);
        var result = Read(encoded);
        var document = Assert.IsType<AnimationDataDocument>(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, document.Sections.Count);
        Assert.Equal(48 + prefixLength, document.Data.BodyOffset);
        Assert.Equal(8, document.HeaderWords.Count);
        Assert.Equal(0x3D088889u, document.HeaderWords[6].Bits);
        Assert.Equal(encoded, document.EncodedResource.ToArray());
        Assert.Equal(new byte[] { 0xFF, 0, 0x80, 0x7F }, document.Data.Body.Span[32..].ToArray());
        encoded[^1] = 0;
        Assert.Equal(0x7F, document.EncodedResource.Span[^1]);
    }

    [Fact]
    public void RetainsOtherVersionsWithoutAssigningHeaderMeaning()
    {
        var result = Read(Fixture(6, 13, 13));
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.HeaderWords);
        Assert.False(result.Value.HasVersion14Header);
        Assert.Single(result.Diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(70)]
    public void RejectsTruncatedRecords(int length)
    {
        var result = Read(Fixture(6, 13)[..length]);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RejectsOverflowingLengthsAndExtraSuffix()
    {
        var encoded = Fixture(6, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12), uint.MaxValue);
        Assert.Null(Read(encoded).Value);
        Assert.Null(Read([.. Fixture(6, 13), 0]).Value);
    }

    [Fact]
    public void RejectsWrongRecordOrderAndRetainsShortTablePrefix()
    {
        var encoded = Fixture(6, 13);
        encoded[29] = (byte)'X';
        Assert.Null(Read(encoded).Value);
        encoded = Fixture(6, 13)[..80];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(57), 19);
        var shortPrefix = Read(encoded);
        Assert.NotNull(shortPrefix.Value);
        Assert.Null(shortPrefix.Value.Version14Layout);
        Assert.Empty(shortPrefix.Diagnostics);
    }

    [Fact]
    public void RejectsMismatchedIdentityAndSourceExtent()
    {
        var reader = new AnimationDataReader(); var encoded = Fixture(6, 13);
        Assert.Throws<ArgumentException>(() => reader.Decode(encoded, Identity, Revision with { IndexContentId = "other" }, new("volume.dat", 0, encoded.Length)));
        Assert.Throws<ArgumentException>(() => reader.Decode(encoded, Identity, Revision, new("volume.dat", 0, encoded.Length + 1)));
        Assert.Throws<OverflowException>(() => reader.Decode(encoded, Identity, Revision, new("volume.dat", long.MaxValue, encoded.Length)));
    }

    [Fact]
    public async Task JsonExportPreservesEveryEncodedByteAndRevision()
    {
        var encoded = Fixture(5, 17); var decoded = Read(encoded);
        using var stream = new MemoryStream();
        await AnimationJsonWriter.WriteAsync(stream, decoded);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        using var json = JsonDocument.Parse(text);
        Assert.Equal(encoded, Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!));
        Assert.Equal(Revision.PhysicalSourceStamp, json.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString());
        Assert.Equal(49, json.RootElement.GetProperty("Sections")[2].GetProperty("ResourceOffset").GetInt64());
    }

    [Theory]
    [InlineData("/source/volume.dat")]
    [InlineData("Z:\\source\\volume.dat")]
    [InlineData("../volume.dat")]
    [InlineData("source/../volume.dat")]
    public async Task JsonExportRejectsMachinePaths(string relativeSource)
    {
        var result = Read(Fixture(6, 13));
        result = result with { Extents = [result.Extents[0] with { RelativeSource = relativeSource }] };
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => AnimationJsonWriter.WriteAsync(stream, result));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void StopsBeforeHashingAnOverLimitResource()
    {
        var bytes = new byte[AnimationDataReader.MaximumResourceBytes + 1];
        var result = Read(bytes);
        Assert.Null(result.Value);
        Assert.Single(result.Diagnostics);
        Assert.Null(result.Steps[0].PayloadSha256);
    }

    [Fact]
    public async Task CancelledExportLeavesDestinationEmpty()
    {
        using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AnimationJsonWriter.WriteAsync(stream, Read(Fixture(6, 13)), cancellation.Token));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void PayloadProvenanceUsesOwnedEncodedBytes()
    {
        var encoded = Fixture(6, 13);
        var result = Read(encoded);
        Assert.Equal(result.Steps[0].PayloadSha256, result.Extents[0].PayloadSha256);
        Assert.NotNull(result.Extents[0].PayloadSha256);
        Assert.Throws<ArgumentException>(() => new AnimationDataReader().Decode(encoded, Identity, Revision,
            new("volume.dat", 64, encoded.Length, new string('0', 64))));
    }

    [Fact]
    public async Task RevisionReferencesDistinguishSourceChangesAndRetainDirectEvidence()
    {
        var decoded = Read(Fixture(6, 13));
        var a = await Write(decoded);
        var b = await Write(decoded with { Revision = decoded.Revision with { PhysicalSourceStamp = "other-private-stamp" } });
        using var left = JsonDocument.Parse(a); using var right = JsonDocument.Parse(b);
        Assert.NotEqual(left.RootElement.GetProperty("SourceRevisionRef").GetString(),
            right.RootElement.GetProperty("SourceRevisionRef").GetString());
        Assert.Equal("other-private-stamp", right.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString());

        static async Task<string> Write(DecodeResult<AnimationDataDocument> value)
        {
            using var output = new MemoryStream(); await AnimationJsonWriter.WriteAsync(output, value);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    [Fact]
    public void OlderShortEnvelopeRetainsDataAtItsDeclaredOffset()
    {
        var bytes = Fixture(4, 4, 13);
        var result = Read(bytes);
        var document = Assert.IsType<AnimationDataDocument>(result.Value);
        Assert.Equal(20, document.Sections[1].ResourceOffset);
        Assert.Equal(36, document.Data.ResourceOffset);
        Assert.Equal(52, document.Data.BodyOffset);
        Assert.Empty(document.HeaderWords);
        Assert.Equal(bytes.AsSpan(52).ToArray(), document.Data.Body.ToArray());
        Assert.Equal(bytes, document.EncodedResource.ToArray());
    }

    [Fact]
    public void EmbeddedTagsInsideContainerBodyDoNotCreateRecordBoundaries()
    {
        var bytes = Fixture(6, 64);
        Encoding.ASCII.GetBytes("ANIMDATA").CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 14);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), uint.MaxValue);
        var result = Read(bytes);
        var document = Assert.IsType<AnimationDataDocument>(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(112, document.Data.BodyOffset);
        Assert.Equal(bytes, document.EncodedResource.ToArray());
    }

    [Fact]
    public void UnknownHeaderBitsRemainRawWithoutFloatOrIdentifierValidation()
    {
        var bytes = Fixture(6, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(61 + 12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(61 + 24), 0x7FC01234);
        var result = Read(bytes);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(uint.MaxValue, result.Value!.HeaderWords[3].Bits);
        Assert.Equal(0x7FC01234u, result.Value.HeaderWords[6].Bits);
        Assert.Equal("0x7FC01234", result.UnknownFields["ANIMDATA.header.24"]);
    }

    private static DecodeResult<AnimationDataDocument> Read(byte[] bytes) =>
        new AnimationDataReader().Decode(bytes, Identity, Revision, new("volume.dat", 64, bytes.Length));

    private static byte[] Fixture(uint containerVersion, int prefixLength, uint dataVersion = 14)
    {
        using var output = new MemoryStream();
        Write("COM_CONT", containerVersion, new byte[prefixLength]);
        Write("LOADCONT", 2, []);
        var body = new byte[36];
        uint[] words = [2, 5, 9, 0x4ACE, 75, 0, 0x3D088889, 0];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 4), words[i]);
        new byte[] { 0xFF, 0, 0x80, 0x7F }.CopyTo(body, 32);
        Write("ANIMDATA", dataVersion, body);
        return output.ToArray();

        void Write(string tag, uint version, byte[] data)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)data.Length);
            output.Write(header); output.Write(data);
        }
    }
}
