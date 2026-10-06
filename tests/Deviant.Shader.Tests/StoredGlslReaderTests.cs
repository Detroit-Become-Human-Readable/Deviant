using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Shaders;
using Xunit;

namespace Deviant.Shader.Tests;

public sealed class StoredGlslReaderTests
{
    [Fact]
    public async Task ScanPreservesExactUtf8TextAndBothExtentKinds()
    {
        const string sourceText = "#version 450\r\n// café\r\nvoid main() {}\r\n";
        var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
        var stored = new byte[] { 0x7F, 0x00 }.Concat(sourceBytes).Append((byte)0).Concat(new byte[] { 0x5A, 0x5A }).ToArray();
        var decoded = MakeDecoded(stored);

        var scan = StoredGlslReader.Scan(decoded);
        var source = Assert.Single(scan.Sources);

        Assert.Equal(sourceText, source.Text);
        Assert.Equal(sourceBytes, source.Utf8Bytes.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)), source.Sha256);
        Assert.Equal("#version 450\r\n", source.VersionDirective);
        Assert.Equal(2, source.DecodedExtent.Offset);
        Assert.Equal(sourceBytes.Length, source.DecodedExtent.Length);
        Assert.Equal(0, source.ContextExtent.Offset);
        Assert.Equal(new byte[] { 0x7F, 0x00 }, source.ContextBefore.ToArray());
        Assert.Equal(decoded.Value!.Chunks[0].PhysicalExtent, source.PhysicalExtent);
        Assert.Equal(new[] { source.PhysicalExtent }, source.PhysicalExtents);
        Assert.Equal(decoded.Steps[0], source.DecodeSteps[0]);
        Assert.True(source.HasNullTerminator);
        Assert.Equal("shader/00000002", source.Identity.MemberPath);

        await using var output = new MemoryStream();
        await source.WriteOriginalAsync(output);
        Assert.Equal(sourceBytes, output.ToArray());
        Assert.Equal("StoredShaderSource", source.ToAssetDescriptor().Family);
    }

    [Fact]
    public void MultipleSourcesStopAtEachNullTerminator()
    {
        const string first = "#version 450\nvoid first() {}\n";
        const string second = "#version 460\r\nvoid second() {}\r\n";
        var bytes = Encoding.UTF8.GetBytes("prefix\0" + first + "\0middle\0" + second + "\0suffix");

        var scan = StoredGlslReader.Scan(MakeDecoded(bytes));

        Assert.Equal(2, scan.Sources.Count);
        Assert.Equal(first, scan.Sources[0].Text);
        Assert.Equal(second, scan.Sources[1].Text);
        Assert.NotEqual(scan.Sources[0].Sha256, scan.Sources[1].Sha256);
        Assert.Empty(scan.Diagnostics);
    }

    [Fact]
    public void UnterminatedSourceIsRetainedWithDiagnostic()
    {
        var bytes = Encoding.UTF8.GetBytes("#version 450\nvoid partial() {}");

        var scan = StoredGlslReader.Scan(MakeDecoded(bytes));

        var source = Assert.Single(scan.Sources);
        Assert.Equal(Encoding.UTF8.GetString(bytes), source.Text);
        Assert.False(source.HasNullTerminator);
        Assert.Contains(scan.Diagnostics, item => item.Code == "UnterminatedSourceRegion");
        Assert.Equal(AssetCompleteness.Partial, source.ToAssetDescriptor().Completeness);
    }

    [Fact]
    public void InvalidUtf8BytesRemainExportableAndAreReported()
    {
        var prefix = Encoding.ASCII.GetBytes("#version 450\n");
        var bytes = prefix.Concat(new byte[] { 0xFF, 0 }).ToArray();

        var scan = StoredGlslReader.Scan(MakeDecoded(bytes));

        var source = Assert.Single(scan.Sources);
        Assert.Null(source.Text);
        Assert.Equal(bytes[..^1], source.Utf8Bytes.ToArray());
        Assert.Contains(scan.Diagnostics, item => item.Code == "InvalidUtf8");
        Assert.False(source.ToAssetDescriptor().Completeness == AssetCompleteness.Complete);
    }

    [Fact]
    public void NoVersionDirectiveProducesNoShaderAssets()
    {
        var scan = StoredGlslReader.Scan(MakeDecoded(Encoding.UTF8.GetBytes("ordinary text\0")));

        Assert.Empty(scan.Sources);
        Assert.Empty(scan.Diagnostics);
    }

    [Fact]
    public void SourceAndMultibyteUtf8MayCrossAValidatedChunkBoundary()
    {
        const string sourceText = "#version 450\r\n// café across chunks\r\nvoid main() {}\r\n";
        var sourceBytes = Encoding.UTF8.GetBytes(sourceText);
        var accentFirstByte = Array.IndexOf(sourceBytes, (byte)0xC3);
        Assert.True(accentFirstByte > 0);
        var cut = accentFirstByte + 1;
        var firstChunk = sourceBytes[..cut];
        var secondChunk = sourceBytes[cut..].Append((byte)0).ToArray();
        var decoded = MakeDecodedChunks(firstChunk, secondChunk);

        var source = Assert.Single(StoredGlslReader.Scan(decoded).Sources);

        Assert.Equal(sourceText, source.Text);
        Assert.Equal(sourceBytes, source.Utf8Bytes.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)), source.Sha256);
        Assert.True(source.HasNullTerminator);
        Assert.Equal(2, source.PhysicalExtents.Count);
        Assert.Equal("volume-0.dat", source.PhysicalExtents[0].RelativeSource);
        Assert.Equal("volume-1.dat", source.PhysicalExtents[1].RelativeSource);
        Assert.Equal(source.PhysicalExtents[0], source.PhysicalExtent);
        Assert.Equal(sourceBytes.Length, source.DecodedExtent.Length);
        Assert.Equal(sourceText, source.ToAssetDescriptor().SearchTerms);
    }

    [Fact]
    public void TerminatorChunkRemainsInPhysicalProvenanceWhenTextEndsAtBoundary()
    {
        var sourceBytes = Encoding.UTF8.GetBytes("#version 450\nvoid main() {}\n");
        var decoded = MakeDecodedChunks(sourceBytes, new byte[] { 0 });

        var source = Assert.Single(StoredGlslReader.Scan(decoded).Sources);

        Assert.Equal(sourceBytes, source.Utf8Bytes.ToArray());
        Assert.True(source.HasNullTerminator);
        Assert.Equal(2, source.PhysicalExtents.Count);
        Assert.Equal("volume-0.dat", source.PhysicalExtents[0].RelativeSource);
        Assert.Equal("volume-1.dat", source.PhysicalExtents[1].RelativeSource);
    }

    [Fact]
    public void DiscontinuousChunkExtentsPreventPrefixScanning()
    {
        var text = Encoding.UTF8.GetBytes("#version 450\nvoid main() {}\0");
        var cut = 10;
        var decoded = MakeDecodedChunks(text[..cut], text[cut..]);
        var document = decoded.Value!;
        var secondChunk = document.Chunks[1];
        var brokenSecond = secondChunk with
        {
            DecodedExtent = secondChunk.DecodedExtent with { Offset = secondChunk.DecodedExtent.Offset + 1 }
        };
        var broken = decoded with { Value = document with { Chunks = [document.Chunks[0], brokenSecond] } };

        var scan = StoredGlslReader.Scan(broken);

        Assert.Empty(scan.Sources);
        Assert.Contains(scan.Diagnostics, item => item.Code == "DecodedPrefixDiscontinuity");
    }

    [Fact]
    public void RawSuffixIsNotScannedAsDecodedShaderText()
    {
        var decoded = MakeDecoded(Encoding.UTF8.GetBytes("ordinary decoded data"),
            rawSuffix: Encoding.UTF8.GetBytes("#version 450\nvoid suffixOnly() {}\0"));

        var scan = StoredGlslReader.Scan(decoded);

        Assert.Empty(scan.Sources);
    }

    [Fact]
    public void ScanHonorsCancellation()
    {
        var decoded = MakeDecoded(Encoding.UTF8.GetBytes("#version 450\nvoid main() {}\0"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => StoredGlslReader.Scan(decoded, cancellation.Token));
    }

    private static DecodeResult<SegsDocument> MakeDecoded(byte[] decodedBytes, byte[]? rawSuffix = null) =>
        MakeDecodedChunksWithSuffix(rawSuffix, decodedBytes);

    private static DecodeResult<SegsDocument> MakeDecodedChunks(params byte[][] decodedChunks) =>
        MakeDecodedChunksWithSuffix(null, decodedChunks);

    private static DecodeResult<SegsDocument> MakeDecodedChunksWithSuffix(byte[]? rawSuffix, params byte[][] decodedChunks)
    {
        var identity = new ResourceIdentity("generic-index-id", 42);
        var revision = new SourceRevision(identity.IndexContentId, "generic-source-stamp");
        var prefix = decodedChunks.SelectMany(chunk => chunk).ToArray();
        var chunks = new List<SegsChunk>();
        var extents = new List<SourceExtent>();
        long decodedOffset = 0;
        for (var index = 0; index < decodedChunks.Length; index++)
        {
            var decodedBytes = decodedChunks[index];
            var physical = new SourceExtent($"volume-{index}.dat", 1024 + index * 512, decodedBytes.Length,
                $"source-hash-{index}");
            var decodedExtent = new DecodedExtent(identity, decodedOffset, decodedBytes.Length);
            chunks.Add(new(index, 1, 1, checked((uint)decodedOffset), checked((int)decodedOffset),
                decodedBytes.Length, physical, decodedExtent, decodedBytes));
            extents.Add(physical);
            decodedOffset += decodedBytes.Length;
        }
        var segmentCount = checked((ushort)chunks.Count);
        var tableEnd = SegsContainerReader.HeaderLength + segmentCount * SegsContainerReader.TableEntryLength;
        var document = new SegsDocument(1, segmentCount, (uint)prefix.Length, (uint)prefix.Length,
            tableEnd, tableEnd, prefix.Length, chunks, Array.Empty<SegsGap>(), prefix,
            rawSuffix?.Length > 0 ? new SourceExtent("volume-0.dat", 4096, rawSuffix.Length, "raw-suffix-hash") : null,
            rawSuffix ?? Array.Empty<byte>(), true);
        return new(document, identity, revision, extents,
            [new DecodeStep("fixture-decoder", "1", "synthetic chunk")],
            Array.Empty<string>(), Array.Empty<ResourceReference>(), new Dictionary<string, string>());
    }
}
