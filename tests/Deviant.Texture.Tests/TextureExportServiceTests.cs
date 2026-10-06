using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureExportServiceTests
{
    [Fact]
    public async Task DdsExportRetainsTheOriginalValidatedBytes()
    {
        var fixture = MakeFixture();
        try
        {
            var source = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
                TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
            var document = new DdsTextureReader().Read(source);
            var result = await new TextureExportService().ExportAsync(document, null, AssetExportFormat.Dds,
                fixture.SourceDirectory, fixture.OutputDirectory, "textures/test.dds");

            var exported = await File.ReadAllBytesAsync(Path.Combine(fixture.OutputDirectory, "textures", "test.dds"));
            Assert.Equal(source, exported);
            Assert.Equal(source.Length, result.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(), result.Sha256);
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task PngExportWritesDecodedPixelsAndRejectsWritesIntoSourceRoot()
    {
        var fixture = MakeFixture();
        try
        {
            byte[] rgba = [5, 20, 200, 255];
            var document = new DdsTextureReader().Read(TextureFixtureBuilder.CreateDx10Rgba8(1, 1, rgba));
            var pixels = new DdsTextureDecoder().DecodeMip(document);
            var result = await new TextureExportService().ExportAsync(document, pixels, AssetExportFormat.Png,
                fixture.SourceDirectory, fixture.OutputDirectory, "textures/test.png");

            var png = await File.ReadAllBytesAsync(Path.Combine(fixture.OutputDirectory, "textures", "test.png"));
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            Assert.Equal(png.Length, result.Length);
            Assert.Equal(0, result.SelectedMip);
            Assert.Equal(document.ContentSha256, result.InputPayloadSha256);
            Assert.Equal("R8G8B8A8_UNORM", result.PixelFormat);
            Assert.False(result.SourceIsHdr);
            Assert.False(result.OutputIsHdr);
            Assert.False(result.SourceIsSrgb);
            Assert.False(result.OutputIsSrgb);
            Assert.Equal("RGBA8", result.Precision);
            await Assert.ThrowsAsync<ArchiveException>(() => new TextureExportService().ExportAsync(document, pixels,
                AssetExportFormat.Png, fixture.SourceDirectory, fixture.SourceDirectory, "inside.png"));
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task ExistingOutputRequiresExplicitOverwrite()
    {
        var fixture = MakeFixture();
        try
        {
            var document = new DdsTextureReader().Read(TextureFixtureBuilder.CreateDxt1(4, 4, 1,
                TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)));
            Directory.CreateDirectory(fixture.OutputDirectory);
            var existingPath = Path.Combine(fixture.OutputDirectory, "existing.dds");
            await File.WriteAllTextAsync(existingPath, "keep");

            var error = await Assert.ThrowsAsync<ArchiveException>(() => new TextureExportService().ExportAsync(document,
                null, AssetExportFormat.Original, fixture.SourceDirectory, fixture.OutputDirectory, "existing.dds"));

            Assert.Equal("DestinationExists", error.Code);
            Assert.Equal("keep", await File.ReadAllTextAsync(existingPath));
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task PngExportRejectsPixelsFromAnotherDocumentEvenWhenDimensionsMatch()
    {
        var fixture = MakeFixture();
        try
        {
            var reader = new DdsTextureReader();
            var decoder = new DdsTextureDecoder();
            var first = reader.Read(TextureFixtureBuilder.CreateDx10Rgba8(1, 1, [1, 2, 3, 255]));
            var second = reader.Read(TextureFixtureBuilder.CreateDx10Rgba8(1, 1, [4, 5, 6, 255]));
            var pixels = decoder.DecodeMip(first);

            var error = await Assert.ThrowsAsync<ArchiveException>(() => new TextureExportService().ExportAsync(second,
                pixels, AssetExportFormat.Png, fixture.SourceDirectory, fixture.OutputDirectory, "wrong.png"));

            Assert.Equal("TextureSurfaceMismatch", error.Code);
            Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "wrong.png")));
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task HdrPngExportReportsToneMapExposureAndEightBitPrecision()
    {
        var fixture = MakeFixture();
        try
        {
            var document = new DdsTextureReader().Read(TextureFixtureBuilder.CreateDx10(4, 4, 95, new byte[16]));
            var decoder = new DdsTextureDecoder();
            var hdr = decoder.DecodeMip(document);
            var sdr = HdrDisplayTransform.ToSdr(hdr, exposureStops: 1, toneMap: TextureToneMapOperator.Aces);

            var result = await new TextureExportService().ExportAsync(document, sdr, AssetExportFormat.Png,
                fixture.SourceDirectory, fixture.OutputDirectory, "hdr-preview.png");

            Assert.True(result.SourceIsHdr);
            Assert.False(result.OutputIsHdr);
            Assert.False(result.SourceIsSrgb);
            Assert.True(result.OutputIsSrgb);
            Assert.Equal(0, result.SelectedMip);
            Assert.Equal("RGBA8", result.Precision);
            Assert.Contains("Aces", result.Losses, StringComparison.Ordinal);
            Assert.Contains("1 EV", result.Losses, StringComparison.Ordinal);
            Assert.Equal(TextureToneMapOperator.Aces, result.DisplayTransform?.Operator);
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task ArchiveBackedExportRequiresAndMatchesTheSelectedRevision()
    {
        var fixture = MakeFixture();
        try
        {
            var bytes = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
                TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
            var identity = new ResourceIdentity("index-1", 4);
            var revision = new SourceRevision("index-1", "physical-1");
            var document = new DdsTextureReader().Read(bytes, identity, revision);
            var service = new TextureExportService();

            var missing = await Assert.ThrowsAsync<ArchiveException>(() => service.ExportAsync(document, null,
                AssetExportFormat.Original, fixture.SourceDirectory, fixture.OutputDirectory, "source.bin"));
            var changed = await Assert.ThrowsAsync<ArchiveException>(() => service.ExportAsync(document, null,
                AssetExportFormat.Original, fixture.SourceDirectory, fixture.OutputDirectory, "source.bin",
                expectedRevision: new SourceRevision("index-1", "physical-2")));
            var exported = await service.ExportAsync(document, null, AssetExportFormat.Original,
                fixture.SourceDirectory, fixture.OutputDirectory, "source.bin", expectedRevision: revision,
                preCommitValidation: _ => Task.CompletedTask);
            var identityOnly = new DdsTextureReader().Read(bytes, identity);
            var unvalidated = await Assert.ThrowsAsync<ArchiveException>(() => service.ExportAsync(identityOnly, null,
                AssetExportFormat.Original, fixture.SourceDirectory, fixture.OutputDirectory, "source-unvalidated.bin",
                expectedRevision: revision));

            Assert.Equal("SourceRevisionRequired", missing.Code);
            Assert.Equal("SourceChanged", changed.Code);
            Assert.Equal("SourceValidationRequired", unvalidated.Code);
            Assert.Equal(identity, exported.Identity);
            Assert.Equal(revision, exported.Revision);
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task OriginalExportDoesNotUseOrReportAnUnrelatedDisplaySurface()
    {
        var fixture = MakeFixture();
        try
        {
            var reader = new DdsTextureReader();
            var original = reader.Read(TextureFixtureBuilder.CreateDxt1(4, 4, 1,
                TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)));
            var hdr = reader.Read(TextureFixtureBuilder.CreateDx10(4, 4, 95, new byte[16]));
            var displaySurface = HdrDisplayTransform.ToSdr(new DdsTextureDecoder().DecodeMip(hdr), 1,
                TextureToneMapOperator.Reinhard);

            var result = await new TextureExportService().ExportAsync(original, displaySurface, AssetExportFormat.Original,
                fixture.SourceDirectory, fixture.OutputDirectory, "original.dds");

            Assert.Null(result.DisplayTransform);
            Assert.Equal(original.OriginalBytes.ToArray(), await File.ReadAllBytesAsync(Path.Combine(fixture.OutputDirectory, "original.dds")));
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task PngExportReportsTheSelectedCubeFaceAndArrayLayer()
    {
        var fixture = MakeFixture();
        try
        {
            var payload = new byte[2 * 6 * 4];
            for (var i = 0; i < 12; i++)
            {
                payload[i * 4] = (byte)(i * 10);
                payload[i * 4 + 3] = 255;
            }
            var document = new DdsTextureReader().Read(TextureFixtureBuilder.CreateDx10(1, 1, 28,
                payload, arraySize: 2, miscFlag: 0x4));
            var selection = new TextureSubresourceSelection(1, CubeFace.NegativeY, 0);
            var surface = new DdsTextureDecoder().DecodeSubresource(document, selection);

            var result = await new TextureExportService().ExportAsync(document, surface, AssetExportFormat.Png,
                fixture.SourceDirectory, fixture.OutputDirectory, "cube-face.png");

            Assert.Equal(selection, result.SelectedSubresource);
            Assert.Equal(0, result.SelectedMip);
        }
        finally { DeleteFixture(fixture.Root); }
    }

    [Fact]
    public async Task FailedPreCommitValidationRetainsTheExistingOutput()
    {
        var fixture = MakeFixture();
        try
        {
            var document = new DdsTextureReader().Read(TextureFixtureBuilder.CreateDxt1(4, 4, 1,
                TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)));
            Directory.CreateDirectory(fixture.OutputDirectory);
            var output = Path.Combine(fixture.OutputDirectory, "existing.dds");
            await File.WriteAllTextAsync(output, "original output");
            var called = false;

            var error = await Assert.ThrowsAsync<ArchiveException>(() => new TextureExportService().ExportAsync(document,
                null, AssetExportFormat.Dds, fixture.SourceDirectory, fixture.OutputDirectory, "existing.dds",
                overwrite: true, preCommitValidation: _ =>
                {
                    called = true;
                    return Task.FromException(new ArchiveException("SourceChanged", "The pinned source changed."));
                }));

            Assert.True(called);
            Assert.Equal("SourceChanged", error.Code);
            Assert.Equal("original output", await File.ReadAllTextAsync(output));
            Assert.Single(Directory.GetFiles(fixture.OutputDirectory));
        }
        finally { DeleteFixture(fixture.Root); }
    }

    private static (string Root, string SourceDirectory, string OutputDirectory) MakeFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "TextureExport-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        return (root, source, Path.Combine(root, "output"));
    }

    private static void DeleteFixture(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
