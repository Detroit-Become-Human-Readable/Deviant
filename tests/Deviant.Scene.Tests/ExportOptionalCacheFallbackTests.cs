using System.Numerics;
using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class ExportOptionalCacheFallbackTests
{
    [Theory]
    [InlineData("quota")]
    [InlineData("disk-full")]
    [InlineData("handle-disk-full")]
    public async Task OptionalPreparationRecoversOnlyStorageAdmissionFailures(string kind)
    {
        var expected = new object(); int preparations = 0, fallbacks = 0;
        IOException error = kind switch
        {
            "disk-full" => new("Generated disk full.", unchecked((int)0x80070070)),
            "handle-disk-full" => new("Generated handle disk full.", unchecked((int)0x80070027)),
            _ => new("Generated cache admission failure.")
        };
        if (kind == "quota") error.Data["ErrorCode"] = "CacheStorageLimit";

        var actual = await NativeModelSession.UseOptionalExportPreparationAsync<object>(
            () => { preparations++; throw error; }, () => { fallbacks++; return Task.FromResult(expected); });

        Assert.Same(expected, actual); Assert.Equal(1, preparations); Assert.Equal(1, fallbacks);
    }

    [Fact]
    public async Task CancellationAfterCacheRejectionPreventsFallback()
    {
        using var canceled = new CancellationTokenSource(); int fallbacks = 0;
        var error = new IOException("Generated cache admission failure."); error.Data["ErrorCode"] = "CacheStorageLimit";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeModelSession.UseOptionalExportPreparationAsync(
            () => { canceled.Cancel(); throw error; }, () => { fallbacks++; return Task.FromResult(1); }, canceled.Token));
        Assert.Equal(0, fallbacks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public async Task MediaPublisherUsesRequestedDestinationWhenOptionalCacheCannotFit(long cacheBytes)
    {
        using var fixture = new Fixture(cacheBytes);
        int validations = 0;
        Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validations++; return Task.CompletedTask; }
        var publish = Publisher(fixture, Validate);
        string destination = Path.Combine(fixture.Root, "export");

        var actual = await publish(destination, "actual.png", default);
        var expected = await new TextureExportService().ExportAsync(fixture.Map.Document, fixture.Map.Surface,
            AssetExportFormat.Png, fixture.SourceRoot, Path.Combine(fixture.Root, "reference"), "expected.png",
            expectedRevision: fixture.Map.Document.Revision, preCommitValidation: Validate);

        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal("actual.png", actual.RelativePath);
        Assert.Equal(fixture.Map.Document.Identity, actual.Identity);
        Assert.Equal(fixture.Map.Document.Revision, actual.Revision);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "reference", "expected.png")),
            await File.ReadAllBytesAsync(Path.Combine(destination, actual.RelativePath)));
        Assert.True(validations >= 2);
        Assert.Empty(Directory.EnumerateFiles(fixture.CacheRoot, "*", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("permission")]
    [InlineData("memory")]
    [InlineData("cancel")]
    public async Task MediaPublisherPropagatesFailuresOutsideOptionalStorageAdmission(string kind)
    {
        using var fixture = new Fixture(256);
        Exception expected = kind switch
        {
            "source" => new ArchiveException("SourceChanged", "Generated source change."),
            "permission" => new UnauthorizedAccessException("Generated access failure."),
            "memory" => new ArchiveException("AssetMemoryLimit", "Generated memory admission failure."),
            _ => new OperationCanceledException()
        };
        int validations = 0;
        var publish = Publisher(fixture, _ => { validations++; throw expected; });

        var actual = await Record.ExceptionAsync(() => publish(Path.Combine(fixture.Root, "export"), "failed.png", default));

        Assert.Same(expected, actual);
        Assert.Equal(1, validations);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "export", "failed.png")));
        Assert.Equal(0, fixture.Manager.ActivePreviewLeases);
        Assert.Equal(0, fixture.Budget.UsedBytes);
    }

    private static Func<string, string, CancellationToken, Task<TextureExportResult>> Publisher(Fixture fixture, Func<CancellationToken, Task> validate)
    {
        var method = typeof(NativeModelSession).GetMethod("CreatePreparedMediaPublisher", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Func<string, string, CancellationToken, Task<TextureExportResult>>)method.Invoke(null,
            [fixture.Preparation, "generated-texture", fixture.Map.Document, fixture.Map.Surface, fixture.SourceRoot, validate, fixture.Budget])!;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Deviant-export-fallback-tests", Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "source");
        public string CacheRoot => Path.Combine(Root, "cache");
        public CacheManager Manager { get; }
        public NativeModelExportPreparationCache Preparation { get; }
        public AssetMemoryBudget Budget { get; } = new();
        public MaterialSurfaceMap Map { get; }
        public Fixture(long cacheBytes)
        {
            Directory.CreateDirectory(SourceRoot);
            Manager = new(Path.Combine(Root, "settings"), new(CacheRoot, cacheBytes));
            Preparation = new(Manager);
            var identity = new ResourceIdentity("generated-export-fallback", 1);
            var revision = new SourceRevision(identity.IndexContentId, "revision");
            var material = new MaterialAsset(identity, revision, "generated", [], [], []) { SurfaceGraph = new(
                new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.25f, .5f, .75f]), null, null, [], [], []) };
            var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [0, 1, 2], IndexElementFormat.UInt16, [], TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
            Map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Budget.Reserve);
        }
        public void Dispose() { Manager.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
