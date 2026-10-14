using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using HelixToolkit.SharpDX;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class CubeTextureLoaderTests
{
    private static readonly ResourceIdentity Identity = new(new string('a', 64), 1);
    private static readonly SourceRevision Revision = new(new string('a', 64), new string('b', 64));

    [Fact]
    public void PreservesHdrCubeFacesMipsAndCompressedBytes()
    {
        var document = Document();
        using var loader = Create(document, Revision);
        var info = loader.Load(Guid.NewGuid());
        Assert.True(info.IsCompressed);
        Assert.False(info.GenerateMipMaps);
        Assert.NotNull(info.Texture);
        using var stream = new MemoryStream();
        info.Texture.CopyTo(stream);
        Assert.Equal(document.OriginalBytes.ToArray(), stream.ToArray());
        loader.Complete(Guid.NewGuid(), info, true);
        loader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loader.Load(Guid.NewGuid()));
    }

    [Fact]
    public void RejectsDifferentSourceRevision()
    {
        Assert.Throws<InvalidDataException>(() => Create(Document(), Revision with { PhysicalSourceStamp = new string('c', 64) }));
    }

    [Fact]
    public void RejectsNonHdrCube()
    {
        Assert.Throws<InvalidDataException>(() => Create(Document(dxgi: 77), Revision));
    }

    [Fact]
    public void RejectsModifiedTextureBytes()
    {
        var document = Document();
        Assert.True(MemoryMarshal.TryGetArray(document.OriginalBytes, out var bytes));
        Assert.NotNull(bytes.Array);
        bytes.Array[bytes.Offset + 148] ^= 1;
        Assert.Throws<InvalidDataException>(() => Create(document, Revision));
    }

    [Fact]
    public void CubeAliasesShareOneUploadUntilTheFinalLeaseCloses()
    {
        var document = Document();
        var pool = new SharedTexturePool<CubeUpload>();
        var budget = new AssetMemoryBudget();
        long gpuBytes = 0;
        int uploads = 0;
        var key = TextureContentKey.CreateCube(document, Revision);
        var first = pool.Acquire(key, CreateUpload);
        var alias = pool.Acquire(TextureContentKey.CreateCube(Document(), Revision),
            () => throw new InvalidOperationException("Cube aliases must reuse the upload."));
        Assert.Same(first.Resource, alias.Resource);
        Assert.Equal(1, uploads);
        Assert.True(key.IsCube); Assert.Equal(4, key.MipCount);
        Assert.Equal(TextureStorageFormat.Bc6Unsigned, key.EncodedFormat);
        Assert.Equal(document.OriginalBytes.Length, budget.UsedBytes);
        Assert.Equal(document.Info.EncodedLength, gpuBytes);
        first.Dispose();
        Assert.Equal(1, pool.Count);
        alias.Dispose(); alias.Dispose();
        Assert.Equal(0, pool.Count); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, gpuBytes);
        Assert.Throws<ObjectDisposedException>(() => first.Resource.Loader.Load(Guid.NewGuid()));

        CubeUpload CreateUpload()
        {
            uploads++; gpuBytes += document.Info.EncodedLength;
            return new(AssetViewportService.RawTextureLoader.CreateCube(document, Revision),
                budget.Reserve(document.OriginalBytes.Length, "cube upload"), () => gpuBytes -= document.Info.EncodedLength);
        }
    }

    [Fact]
    public void RejectsChangedCubeFaceValues()
    {
        var document = Document();
        var faces = Assert.IsType<CubeFace[]>(document.Info.CubeFaces);
        faces[0] = (CubeFace)42;
        Assert.Throws<InvalidDataException>(() => Create(document, Revision));
    }

    [Fact]
    public void RejectsChangedMipMetadata()
    {
        var document = Document();
        var mips = Assert.IsType<TextureMipLevel[]>(document.MipLevels);
        mips[0] = mips[0] with { EncodedLength = 1 };
        Assert.Throws<InvalidDataException>(() => Create(document, Revision));
    }

    private sealed class CubeUpload(AssetViewportService.RawTextureLoader loader, IDisposable reservation,
        Action releaseGpu) : IDisposable
    {
        public AssetViewportService.RawTextureLoader Loader { get; } = loader;
        public void Dispose() { Loader.Dispose(); reservation.Dispose(); releaseGpu(); }
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 1, true)]
    public void RejectsArraysMultipleCubesAndTrailingBytes(bool cube, int arrays, bool trailing)
    {
        Assert.Throws<InvalidDataException>(() => Create(Document(cube, arrays, trailing), Revision));
    }

    private static AssetViewportService.RawTextureLoader Create(DdsTextureDocument document, SourceRevision revision)
    {
        var method = typeof(AssetViewportService.RawTextureLoader).GetMethod("CreateCube",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { return Assert.IsType<AssetViewportService.RawTextureLoader>(method.Invoke(null, [document, revision])); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static DdsTextureDocument Document(bool cube = true, int arrays = 1, bool trailing = false, uint dxgi = 95)
    {
        const int width = 8, mips = 4;
        int layers = arrays * (cube ? 6 : 1);
        const int faceBytes = 64 + 16 + 16 + 16;
        var bytes = new byte[148 + layers * faceBytes + (trailing ? 1 : 0)];
        "DDS "u8.CopyTo(bytes);
        Put(4, 124); Put(8, 0xA1007); Put(12, width); Put(16, width);
        Put(20, 64); Put(28, mips); Put(76, 32); Put(80, 4);
        "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108, 0x401008);
        Put(112, cube ? 0xFE00u : 0); Put(128, dxgi); Put(132, 3);
        Put(136, cube ? 4u : 0); Put(140, (uint)arrays);
        return new DdsTextureReader().Read(bytes, Identity, Revision);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
