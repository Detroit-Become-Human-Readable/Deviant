using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class SceneCatalogSessionTests
{
    [Fact]
    public async Task DiscoversLoadsNamesTransformsAndVerifiedCatalogNavigation()
    {
        using var fixture = new SceneArchiveFixture();
        fixture.WriteArchive();
        var snapshot = await fixture.OpenSnapshotAsync();
        var cache = fixture.CreateCache();
        var aliases = new AliasStore(fixture.LibraryRoot);
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, cache, aliases);

        await session.DiscoverMoreAsync();
        var page = await session.QueryAsync(limit: 10);
        var descriptor = Assert.Single(page.Entries);
        Assert.Equal("generated-catalog", descriptor.Label);
        Assert.Equal("scene.section", descriptor.Family);
        Assert.Equal("Section", descriptor.Metadata["scene.kind"]);

        var loaded = await session.LoadAsync(descriptor.Identity);
        var document = loaded.Result.Value!;
        var node = Assert.Single(document.Hierarchy.Nodes);
        Assert.Equal(SceneTransformStatus.Available, node.TransformStatus);
        Assert.NotNull(node.LocalTransform);
        Assert.NotNull(node.WorldTransform);
        Assert.Contains(loaded.NamesByNode.Values.SelectMany(value => value), name => name.Text == "generated-instance");

        var relationship = Assert.Single(loaded.ReferencesByNode.Values.SelectMany(value => value));
        Assert.True(relationship.Evidence.Verified);
        Assert.Equal(1, Assert.Single(relationship.Resolution.Candidates).Ordinal);
    }

    [Fact]
    public async Task ExportsRelativeSourceMetadataAndRevalidatesChangedSource()
    {
        using var fixture = new SceneArchiveFixture();
        fixture.WriteArchive();
        var snapshot = await fixture.OpenSnapshotAsync();
        var cache = fixture.CreateCache();
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, cache, new AliasStore(fixture.LibraryRoot));
        await session.DiscoverMoreAsync();
        var descriptor = Assert.Single((await session.QueryAsync(limit: 10)).Entries);
        var loaded = await session.LoadAsync(descriptor.Identity);
        var output = Path.Combine(fixture.OutputRoot, "scene.json");

        await session.ExportMetadataAsync(loaded, output);
        var json = await File.ReadAllTextAsync(output);
        Assert.Contains("deviant-scene-structure-v1", json, StringComparison.Ordinal);
        Assert.Contains("sample.dat", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);

        var sentinel = "keep-existing";
        await File.WriteAllTextAsync(output, sentinel);
        await Assert.ThrowsAsync<IOException>(() => session.ExportMetadataAsync(loaded, output));
        Assert.Equal(sentinel, await File.ReadAllTextAsync(output));
        var volume = await File.ReadAllBytesAsync(fixture.VolumePath);
        volume[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.VolumePath, volume);

        await Assert.ThrowsAsync<ArchiveException>(() => session.RevalidateAsync(loaded));
        Assert.Equal(sentinel, await File.ReadAllTextAsync(output));
        var changedOutput = Path.Combine(fixture.OutputRoot, "changed.json");
        await Assert.ThrowsAsync<ArchiveException>(() => session.ExportMetadataAsync(loaded, changedOutput));
        Assert.False(File.Exists(changedOutput));
        Assert.Empty(Directory.EnumerateFiles(fixture.OutputRoot, ".changed.json.*.tmp"));
    }

    [Fact]
    public async Task CancellationIsRecoverableAndDisposalDrainsBeforeCacheClear()
    {
        using var fixture = new SceneArchiveFixture();
        fixture.WriteArchive();
        var snapshot = await fixture.OpenSnapshotAsync();
        var cache = fixture.CreateCache();
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, cache, new AliasStore(fixture.LibraryRoot));
        using var cancellation = new CancellationTokenSource();
        session.Updated += (_, _) => { if (session.Progress.IsRunning) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DiscoverMoreAsync(cancellation.Token));

        await session.DiscoverMoreAsync();
        Assert.Single((await session.QueryAsync(limit: 10)).Entries);
        await session.DisposeAsync();
        await cache.ClearAsync();
    }

    [Fact]
    public async Task CachedRediscoveryLoadsTheVerifiedDescriptorAgain()
    {
        using var fixture = new SceneArchiveFixture();
        fixture.WriteArchive();
        var snapshot = await fixture.OpenSnapshotAsync();
        var cache = fixture.CreateCache();
        var aliases = new AliasStore(fixture.LibraryRoot);
        await using (var first = await SceneCatalogSession.OpenAsync(snapshot, cache, aliases))
        {
            await first.DiscoverMoreAsync();
            Assert.Single((await first.QueryAsync(limit: 10)).Entries);
        }
        await using var second = await SceneCatalogSession.OpenAsync(snapshot, cache, aliases);
        await second.DiscoverMoreAsync();
        var descriptor = Assert.Single((await second.QueryAsync(limit: 10)).Entries);
        Assert.Equal("generated-catalog", descriptor.Label);
        Assert.Equal(1, second.Progress.Available);
    }

    [Fact]
    public async Task SkipsTooLargeCandidateAndContinuesDiscovery()
    {
        using var fixture = new SceneArchiveFixture();
        fixture.WriteArchive(includeTooLargeCandidate: true);
        var snapshot = await fixture.OpenSnapshotAsync();
        var cache = fixture.CreateCache();
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, cache, new AliasStore(fixture.LibraryRoot),
            new SceneDiscoveryOptions(MaximumCandidatesPerBatch: 16, MaximumContainerStoredBytes: 4096));

        await session.DiscoverMoreAsync();
        var progress = session.Progress;
        Assert.Equal(2, progress.Visited);
        Assert.Equal(1, progress.Available);
        Assert.False(progress.HasMore);
        Assert.Single((await session.QueryAsync(limit: 10)).Entries);
    }

    private sealed class SceneArchiveFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantGeneratedSceneTests", Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "source");
        public string IndexPath => Path.Combine(SourceRoot, "sample.idx");
        public string VolumePath => Path.Combine(SourceRoot, "sample.dat");
        public string DependencyPath => Path.Combine(SourceRoot, "sample.dep");
        public string CacheRoot => Path.Combine(Root, "cache");
        public string SettingsRoot => Path.Combine(Root, "settings");
        public string LibraryRoot => Path.Combine(Root, "library");
        public string OutputRoot { get; } = Path.Combine(Path.GetTempPath(), "DeviantGeneratedSceneOutputs", Guid.NewGuid().ToString("N"));

        public void WriteArchive(bool includeTooLargeCandidate = false)
        {
            Directory.CreateDirectory(SourceRoot); Directory.CreateDirectory(OutputRoot);
            var decoded = BuildDecodedDirectory();
            var container = BuildSegs(decoded);
            var catalog = BuildCatalogResource();
            var catalogOffset = container.Length;
            var volume = container.Concat(catalog).ToArray();
            File.WriteAllBytes(VolumePath, volume);

            var rows = new List<(uint Type, uint Flags, uint Id, uint Offset, uint Size, uint Alt, uint Package)>
            {
                (29, 1, 7, 0, checked((uint)container.Length), 0, 0),
                (3, 1, 10, checked((uint)catalogOffset), checked((uint)catalog.Length), 0, 0),
                (2202, 1, 99, checked((uint)volume.Length), 0, 0, 0)
            };
            if (includeTooLargeCandidate)
                rows.Add((29, 1, 8, checked((uint)volume.Length), 17 * 1024 * 1024, 0, 0));
            WriteIndex(rows);
            WriteDependencies(rows);
        }

        public async Task<ArchiveSnapshot> OpenSnapshotAsync() => await new ArchiveReader().OpenAsync(IndexPath);

        public CacheManager CreateCache() => new(SettingsRoot, new(CacheRoot, 64 * 1024 * 1024, 1));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            if (Directory.Exists(OutputRoot)) Directory.Delete(OutputRoot, recursive: true);
        }

        private byte[] BuildDecodedDirectory()
        {
            var frames = new[]
            {
                (Type: 2002u, Id: 11u, Payload: NodePayload()),
                (Type: 3u, Id: 10u, Payload: InlineCatalogPayload()),
                (Type: 2207u, Id: 12u, Payload: EntityCatalogPayload()),
                (Type: 2202u, Id: 99u, Payload: EnvironmentPayload())
            };
            var body = new MemoryStream();
            foreach (var frame in frames)
            {
                WriteLittleEndian(body, (uint)frame.Payload.Length);
                body.WriteByte(0); WriteLittleEndian(body, 0); WriteLittleEndian(body, 0);
                body.Write(frame.Payload);
            }
            var dcInfoBodyLength = checked(8u + (uint)frames.Length * 8u);
            var directoryLength = checked(16 + (int)dcInfoBodyLength);
            var result = new MemoryStream();
            result.Write("DC_INFO "u8); WriteLittleEndian(result, 3); WriteLittleEndian(result, dcInfoBodyLength);
            WriteLittleEndian(result, 0); WriteLittleEndian(result, (uint)frames.Length);
            foreach (var frame in frames) { WriteLittleEndian(result, frame.Type); WriteLittleEndian(result, frame.Id); }
            result.Write("DC_DATA "u8); WriteLittleEndian(result, 23); WriteLittleEndian(result, (uint)body.Length);
            result.Write(body.ToArray());
            return result.ToArray();
        }

        private static byte[] BuildSegs(byte[] decoded)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(decoded);
            var data = compressed.ToArray();
            var result = new byte[32 + data.Length];
            result[0] = (byte)'s'; result[1] = (byte)'e'; result[2] = (byte)'g'; result[3] = (byte)'s';
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)decoded.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(16), checked((ushort)data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18), checked((ushort)decoded.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), 1);
            data.CopyTo(result, 32);
            return result;
        }

        private static byte[] NodePayload()
        {
            var bytes = new byte[196]; Put(bytes, 0, 1); "NODE    "u8.CopyTo(bytes.AsSpan(4)); Put(bytes, 12, 5); Put(bytes, 16, 8);
            Single(bytes, 20, 2); Single(bytes, 24, 3); Single(bytes, 28, 4);
            WriteMatrix(bytes, 32, Matrix4x4.CreateTranslation(2, 3, 4));
            WriteMatrix(bytes, 96, Matrix4x4.Identity); Put(bytes, 168, uint.MaxValue);
            Put(bytes, 188, 0x12345678); Put(bytes, 192, 0x87654321);
            return bytes;
        }

        private static byte[] InlineCatalogPayload()
        {
            var bytes = new byte[41]; Put(bytes, 0, 2); "COM_CONT"u8.CopyTo(bytes.AsSpan(4)); Put(bytes, 12, 6); Put(bytes, 16, 1);
            Put(bytes, 20, 2202); Put(bytes, 24, 99); bytes[28] = 1; "LOADCONT"u8.CopyTo(bytes.AsSpan(29)); Put(bytes, 37, 2); return bytes;
        }

        private static byte[] EntityCatalogPayload()
        {
            const string instance = "generated-instance"; const string catalog = "generated-catalog";
            var bytes = new byte[81 + 12 + 4 + Encoding.ASCII.GetByteCount(instance) + 4 + Encoding.ASCII.GetByteCount(catalog) + 8];
            Put(bytes, 0, 4); "COM_CONT"u8.CopyTo(bytes.AsSpan(4)); Put(bytes, 12, 6); Put(bytes, 16, 0); "LOADCONT"u8.CopyTo(bytes.AsSpan(20)); Put(bytes, 28, 2);
            "ENTITY  "u8.CopyTo(bytes.AsSpan(32)); Put(bytes, 40, 7); Put(bytes, 44, 2002); Put(bytes, 48, 11); "ENTCATLG"u8.CopyTo(bytes.AsSpan(81)); Put(bytes, 89, 2);
            var cursor = 93; Put(bytes, cursor, (uint)instance.Length); cursor += 4; Encoding.ASCII.GetBytes(instance).CopyTo(bytes, cursor); cursor += instance.Length;
            Put(bytes, cursor, (uint)catalog.Length); cursor += 4; Encoding.ASCII.GetBytes(catalog).CopyTo(bytes, cursor); cursor += catalog.Length; Put(bytes, cursor, 3); Put(bytes, cursor + 4, 10); return bytes;
        }

        private static byte[] EnvironmentPayload()
        {
            var bytes = new byte[167]; Put(bytes, 0, 1); "SCN_GLBL"u8.CopyTo(bytes.AsSpan(4)); Put(bytes, 12, 17); return bytes;
        }

        private static byte[] BuildCatalogResource()
        {
            var bytes = new byte[29]; "COM_CONT"u8.CopyTo(bytes); Put(bytes, 8, 6); Put(bytes, 12, 13); Put(bytes, 16, 1);
            Put(bytes, 20, 2202); Put(bytes, 24, 99); bytes[28] = 1; return bytes;
        }

        private void WriteIndex(IReadOnlyList<(uint Type, uint Flags, uint Id, uint Offset, uint Size, uint Alt, uint Package)> rows)
        {
            var index = new byte[IndexLayout.HeaderLength + rows.Count * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
            PutBig(index, 20, 18); PutBig(index, 101, (uint)rows.Count);
            for (var i = 0; i < rows.Count; i++) { var row = rows[i]; var offset = IndexLayout.HeaderLength + i * IndexLayout.RecordLength; PutBig(index, offset, row.Type); PutBig(index, offset + 4, row.Flags); PutBig(index, offset + 8, row.Id); PutBig(index, offset + 12, row.Offset); PutBig(index, offset + 16, row.Size); PutBig(index, offset + 20, row.Alt); PutBig(index, offset + 24, row.Package); }
            File.WriteAllBytes(IndexPath, index);
        }

        private void WriteDependencies(IReadOnlyList<(uint Type, uint Flags, uint Id, uint Offset, uint Size, uint Alt, uint Package)> rows)
        {
            using var stream = new MemoryStream(); stream.Write("QUANTICDREAMTABINDEX"u8); PutBig(stream, 18); PutBig(stream, (uint)rows.Count);
            foreach (var row in rows) { PutBig(stream, row.Type); PutBig(stream, row.Flags); PutBig(stream, row.Id); PutBig(stream, row.Type == 29 ? 1u : 0u); if (row.Type == 29) { PutBig(stream, 2202); PutBig(stream, 99); PutBig(stream, 0); PutBig(stream, 0); PutBig(stream, 0); } }
            File.WriteAllBytes(DependencyPath, stream.ToArray());
        }

        private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        private static void PutBig(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
        private static void PutBig(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
        private static void Single(byte[] bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        private static void WriteLittleEndian(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
        private static void WriteMatrix(byte[] bytes, int offset, Matrix4x4 matrix) { var source = new[] { matrix.M11,matrix.M12,matrix.M13,matrix.M14,matrix.M21,matrix.M22,matrix.M23,matrix.M24,matrix.M31,matrix.M32,matrix.M33,matrix.M34,matrix.M41,matrix.M42,matrix.M43,matrix.M44 }; for (var i = 0; i < 16; i++) Single(bytes, offset + i * 4, source[15 - i]); }
    }
}
