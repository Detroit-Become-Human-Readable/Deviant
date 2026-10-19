using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PreparedSceneOutputAdmissionTests
{
    private delegate Task StreamWriter(Stream output, string key, RenderAssetSnapshot scene, CancellationToken token);
    private static StreamWriter Writer()
    {
        var method = typeof(PreparedSceneCodec).GetMethod(nameof(PreparedSceneCodec.WriteAsync),
            BindingFlags.Public | BindingFlags.Static, [typeof(Stream), typeof(string), typeof(RenderAssetSnapshot), typeof(CancellationToken)]);
        Assert.NotNull(method);
        return method.CreateDelegate<StreamWriter>();
    }

    [Fact]
    public async Task AdmittedSceneMatchesStandaloneBytesAndPreservesGeometryMaterialsPixelsAndSourceProof()
    {
        var write = Writer(); using var fixture = new SceneMetadataFixture();
        var budget = new AssetMemoryBudget(); using var scene = Scene(fixture.Snapshot, budget);
        using var cache = Cache(fixture, 8 * 1024 * 1024);
        var standalone = Path.Combine(Path.GetDirectoryName(fixture.Snapshot.IndexPath)!, "standalone.scene");
        await PreparedSceneCodec.WriteAsync(standalone, "roundtrip", scene);
        using var sources = ArchiveSourceSession.Acquire(fixture.Snapshot, (bytes, reason) => budget.Reserve(bytes, reason));
        int productions = 0, validations = 0;
        async Task<bool> Validate(string path, CancellationToken token)
        {
            validations++;
            if (!await PreparedSceneCodec.ValidateAsync(path, "roundtrip", scene.Revision, token, budget)) return false;
            await sources.Session.VerifyAsync(scene.SourceExtents, token);
            sources.Session.EnsureCurrent(); return true;
        }
        using (var lease = await cache.AcquirePreparedOutputAsync("scenes", "roundtrip", ".scene", async (output, token) =>
        {
            productions++; await write(output, "roundtrip", scene, token);
            Assert.True(output.CanRead && output.CanWrite && output.CanSeek);
        }, Validate))
        {
            Assert.StartsWith(cache.Location + Path.DirectorySeparatorChar, lease.Path);
            Assert.Equal(await File.ReadAllBytesAsync(standalone), await File.ReadAllBytesAsync(lease.Path));
            using var restored = await PreparedSceneCodec.ReadAsync(lease.Path, "roundtrip", sources.Session,
                fixture.Snapshot, budget, false);
            var actual = Assert.Single(restored.Meshes.Values).Geometry.Primitives[0];
            var expected = Assert.Single(scene.Meshes.Values).Geometry.Primitives[0];
            Assert.Equal(expected.Positions, actual.Positions); Assert.Equal(expected.Indices, actual.Indices);
            Assert.Equal(expected.Normals, actual.Normals); Assert.Equal(expected.Tangents, actual.Tangents);
            Assert.Equal(expected.TextureCoordinates![0], actual.TextureCoordinates![0]); Assert.Equal(expected.Colors, actual.Colors);
            var expectedPlacement = Assert.Single(scene.Occurrences); var actualPlacement = Assert.Single(restored.Occurrences);
            Assert.Equal(expectedPlacement.Key, actualPlacement.Key); Assert.Equal(expectedPlacement.Entity, actualPlacement.Entity);
            Assert.Equal(expectedPlacement.Node, actualPlacement.Node); Assert.Equal(expectedPlacement.World, actualPlacement.World);
            Assert.Equal(expectedPlacement.WorldBounds, actualPlacement.WorldBounds); Assert.Equal(expectedPlacement.NativeFlags, actualPlacement.NativeFlags);
            Assert.Equal(expectedPlacement.InstancePath, actualPlacement.InstancePath);
            var material = Assert.Single(restored.Materials.Values);
            Assert.Equal(Assert.Single(scene.Materials.Values).Asset.NativeRenderParameters, material.Asset.NativeRenderParameters);
            Assert.Equal(Assert.Single(scene.Materials.Values).TexturesBySlot[0].Surface.Rgba8.ToArray(), material.TexturesBySlot[0].Surface.Rgba8.ToArray());
            Assert.True(material.TexturesBySlot[0].Surface.IsSrgb); Assert.Equal(2, material.TexturesBySlot[0].Surface.MipIndex);
            Assert.Equal(scene.SourceExtents, restored.SourceExtents);
        }
        using (await cache.AcquirePreparedOutputAsync("scenes", "roundtrip", ".scene",
            (_, _) => throw new InvalidOperationException("A valid hit must skip production."), Validate)) { }
        Assert.Equal(1, productions); Assert.Equal(2, validations); Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(cache.Location, "artifact-staging-v1")));
        sources.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task QuotaRejectsGrowthBeforePublicationAndLeavesTheSceneUsable()
    {
        var write = Writer(); using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget); using var cache = Cache(fixture, 64 * 1024);
        long observedBytes = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => cache.AcquirePreparedOutputAsync("scenes", "quota", ".scene",
            async (output, token) =>
            {
                try { await write(output, "quota", scene, token); }
                finally { observedBytes = PhysicalBytes(cache.Location); }
            }, (path, token) => PreparedSceneCodec.ValidateAsync(path, "quota", scene.Revision, token, budget)));
        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.InRange(observedBytes, 1, cache.LimitBytes);
        Assert.Equal(0, PhysicalBytes(cache.Location)); Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.False(scene.HasIncompleteMaterials); Assert.Equal(3, Assert.Single(scene.Meshes.Values).Geometry.Primitives[0].Positions.Count);
        Assert.Equal(256 * 256 * 4, Assert.Single(scene.Materials.Values).TexturesBySlot[0].Surface.Rgba8.Length);
        using var stillOwned = scene.AcquireLease(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task ChecksumTrailerUsesTheSameAdmissionAsTheScenePayload()
    {
        var write = Writer(); using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget);
        var reference = Path.Combine(Path.GetDirectoryName(fixture.Snapshot.IndexPath)!, "reference.scene");
        await PreparedSceneCodec.WriteAsync(reference, "trailer", scene);
        long length = new FileInfo(reference).Length;
        long limit = (length - 1) * 8 / 7;
        while (limit - limit / 8 < length - 1) limit++;
        using var cache = Cache(fixture, limit);
        long completedPayload = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => cache.AcquirePreparedOutputAsync("scenes", "trailer", ".scene",
            async (output, token) =>
            {
                try { await write(output, "trailer", scene, token); }
                finally { completedPayload = output.Length; }
            }, (path, token) => PreparedSceneCodec.ValidateAsync(path, "trailer", scene.Revision, token, budget)));
        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]); Assert.Equal(32L, error.Data["RequestedBytes"]);
        Assert.Equal(length - 32, completedPayload); Assert.Equal(0, PhysicalBytes(cache.Location));
        Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, cache.ActivePreviewLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCleansPartialProductionAndAllowsTheSameKeyToRetry(bool duringWrite)
    {
        var write = Writer(); using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget); using var cache = Cache(fixture, 8 * 1024 * 1024);
        using var cancellation = new CancellationTokenSource(); if (!duringWrite) cancellation.Cancel();
        long written = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquirePreparedOutputAsync("scenes", "cancel", ".scene",
            async (output, token) =>
            {
                using var observed = new CancelOnWrite(output, cancellation);
                try { await write(observed, "cancel", scene, token); }
                finally { written = output.Length; }
            }, (path, token) => PreparedSceneCodec.ValidateAsync(path, "cancel", scene.Revision, token, budget), cancellation.Token));
        if (duringWrite) Assert.True(written > 0); else Assert.Equal(0, written);
        Assert.Equal(0, PhysicalBytes(cache.Location)); Assert.Equal(0, cache.ActivePreviewLeases); Assert.Equal(0, budget.UsedBytes);
        using (await cache.AcquirePreparedOutputAsync("scenes", "cancel", ".scene",
            (output, token) => write(output, "cancel", scene, token),
            (path, token) => PreparedSceneCodec.ValidateAsync(path, "cancel", scene.Revision, token, budget))) { }
        Assert.Equal(0, cache.ActivePreviewLeases); Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task ProductionPreparationKeepsTheResidentSceneWhenStorageIsUnavailable(long limit)
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget); using var cache = Cache(fixture, limit);
        await using (var catalogs = new TextureCatalogSession(fixture.Snapshot, fixture.Revision))
        await using (var textures = new NativeTextureSession(catalogs))
        await using (var renderer = new SceneRenderSession(fixture.Snapshot, new NativeModelSession(textures, budget), budget, cache))
        {
            await renderer.StorePreparedSceneAsync("optional", scene);
            Assert.Equal(0, PhysicalBytes(cache.Location)); Assert.Equal(0, cache.ActivePreviewLeases);
            Assert.False(scene.HasIncompleteMaterials);
            Assert.Equal(3, Assert.Single(scene.Meshes.Values).Geometry.Primitives[0].Positions.Count);
            Assert.Equal(256 * 256 * 4, Assert.Single(scene.Materials.Values).TexturesBySlot[0].Surface.Rgba8.Length);
            using var retained = scene.AcquireLease();
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task ProductionPreparationDoesNotDowngradeSourceReplacement()
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget); using var cache = Cache(fixture, 8 * 1024 * 1024);
        var revisions = new ChangedRevisions(fixture.Snapshot);
        await using (var pinned = ArchiveSourceSession.Acquire(fixture.Snapshot, revisions: revisions))
        await using (var catalogs = new TextureCatalogSession(fixture.Snapshot, fixture.Revision))
        await using (var textures = new NativeTextureSession(catalogs))
        await using (var renderer = new SceneRenderSession(fixture.Snapshot, new NativeModelSession(textures, budget), budget, cache))
        {
            revisions.Changed = true;
            var error = await Assert.ThrowsAsync<ArchiveException>(() => renderer.StorePreparedSceneAsync("source-change", scene));
            Assert.Equal("SourceChanged", error.Code); Assert.Equal(0, PhysicalBytes(cache.Location));
            Assert.Equal(0, cache.ActivePreviewLeases);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task ProductionPreparationPublishesThroughTheSameValidatedSceneProfile()
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using var scene = Scene(fixture.Snapshot, budget); using var cache = Cache(fixture, 8 * 1024 * 1024);
        await using (var catalogs = new TextureCatalogSession(fixture.Snapshot, fixture.Revision))
        await using (var textures = new NativeTextureSession(catalogs))
        await using (var renderer = new SceneRenderSession(fixture.Snapshot, new NativeModelSession(textures, budget), budget, cache))
        {
            await renderer.StorePreparedSceneAsync("production", scene);
            using var prepared = await cache.TryAcquirePreparedAsync("scenes", "production", ".scene",
                (path, token) => PreparedSceneCodec.ValidateAsync(path, "production", scene.Revision, token, budget));
            Assert.NotNull(prepared); Assert.StartsWith(cache.Location + Path.DirectorySeparatorChar, prepared.Path);
            Assert.True(await PreparedSceneCodec.ValidateAsync(prepared.Path, "production", scene.Revision));
        }
        Assert.Equal(0, cache.ActivePreviewLeases); Assert.Equal(0, budget.UsedBytes);
    }

    private static CacheManager Cache(SceneMetadataFixture fixture, long limit)
    {
        var root = Path.GetDirectoryName(fixture.Snapshot.IndexPath)!;
        return new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), limit));
    }

    private static long PhysicalBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

    private static RenderAssetSnapshot Scene(ArchiveSnapshot archive, AssetMemoryBudget budget)
    {
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var meshIdentity = new ResourceIdentity(archive.IndexContentId, 0, "generated/mesh");
        var materialIdentity = meshIdentity with { MemberPath = "generated/material" };
        var textureIdentity = meshIdentity with { MemberPath = "generated/texture" };
        var primitive = new MeshPrimitive("triangle", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, [],
            Normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], Tangents: [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]], Colors: [Vector4.One, Vector4.One, Vector4.One]);
        var mesh = new RenderMesh(meshIdentity, 0, new(new("generated", "1"), [], [primitive], new Dictionary<string, string>()),
            new Dictionary<string, ResourceIdentity> { [primitive.Key] = materialIdentity });
        var placement = new RenderOccurrence("placement", meshIdentity, meshIdentity, "Generated mesh", mesh.Key,
            Matrix4x4.CreateRotationY(.5f) * Matrix4x4.CreateTranslation(3, 2, 1), new(Vector3.Zero, Vector3.One), [meshIdentity]) { NativeFlags = 7 };
        var pixels = Enumerable.Range(0, 256 * 256 * 4).Select(value => (byte)value).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var texture = new RenderTexture(textureIdentity, TextureSurface.RestoreRgba8(256, 256, pixels, false, true, 2,
            TextureStorageFormat.Rgba8, "RGBA8", digest, new(MipIndex: 2), textureIdentity, revision), digest);
        var material = new RenderMaterial(new(materialIdentity, revision, "generated", [], [], [])
            { NativeRenderParameters = new(2, 3, 4, 0, 1, new(materialIdentity, 0, 4)) }, new Dictionary<int, RenderTexture> { [0] = texture });
        var volume = archive.Volumes[0];
        var extents = new SourceExtent[] { new(volume.RelativeName, 0, volume.Stamp.Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(volume.FilePath))).ToLowerInvariant()) };
        return new(revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, [placement],
            new Dictionary<ResourceIdentity, RenderMaterial> { [materialIdentity] = material }, placement.WorldBounds,
            [], [], new(1, 1, 0, 1, 1, 1), extents, budget);
    }

    private sealed class CancelOnWrite(Stream inner, CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { inner.Write(buffer); if (inner.Length > 64 * 1024) cancellation.Cancel(); }
    }

    private sealed class ChangedRevisions(ArchiveSnapshot snapshot) : ISourceRevisionProvider
    {
        internal bool Changed;
        public FileStamp Capture(string path)
        {
            var stamp = path == snapshot.IndexPath ? snapshot.IndexStamp : snapshot.Volumes.Single(item => item.FilePath == path).Stamp;
            return Changed && path != snapshot.IndexPath ? stamp with { LastWriteUtcTicks = stamp.LastWriteUtcTicks + 1 } : stamp;
        }
        public FileStamp Capture(SafeFileHandle handle, string path) => Capture(path);
    }
}
