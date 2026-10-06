using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeDirectContainerTests
{
    [Fact]
    public async Task OpensDirectDirectoryAndGpuPackageWithExactPhysicalOffset()
    {
        using var fixture = new DirectContainerArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath); var budget = new AssetMemoryBudget();
        using (var lease = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 0), budget))
        {
            Assert.Null(lease.Segments); Assert.Single(lease.SourceExtents);
            var directory = lease.Directory; var remainder = directory.RemainderExtent!;
            Assert.Equal(fixture.SourceOffset + 5 + remainder.Offset, lease.DataLayout.RawSuffixExtent!.Offset);
            Assert.Equal(remainder.Length, lease.DataLayout.RawSuffixLength);
            using var gpu = NativeGpuReader.Read(lease, Assert.Single(directory.Objects).Identity);
            Assert.Equal(new byte[] { 11, 22, 33, 44 }, gpu.Decoded.Value!.DecodedPrefix);
            Assert.Equal(fixture.SourceOffset + 5 + remainder.Offset, gpu.PhysicalExtent.Offset);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Bytes.AsSpan(checked((int)(5 + remainder.Offset))))).ToLowerInvariant(), gpu.PhysicalExtent.PayloadSha256);
            lease.EnsureUnchanged();
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task OwnerAndBorrowedLeaseCleanupHonorsCancellationAndUnknownMagic()
    {
        using var fixture = new DirectContainerArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath); var budget = new AssetMemoryBudget();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeContainerLease.OpenAsync(
            snapshot, new(snapshot.IndexContentId, 0), budget, cancelled.Token));
        Assert.Equal(0, budget.UsedBytes);
        var lease = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 0), budget);
        var borrowed = lease.AcquireLease(); lease.Dispose();
        lease.EnsureUnchanged(); Assert.NotNull(lease.Directory);
        borrowed.Dispose(); Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => lease.EnsureUnchanged());
        using var invalid = new DirectContainerArchive(invalidMagic: true);
        var invalidSnapshot = await new ArchiveReader().OpenAsync(invalid.IndexPath);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => NativeContainerLease.OpenAsync(
            invalidSnapshot, new(invalidSnapshot.IndexContentId, 0), budget));
        Assert.Equal("NativeContainerSignature", error.Code); Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class DirectContainerArchive : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantDirectContainerTests", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");
        public byte[] Bytes { get; }
        public int SourceOffset => 64;
        public DirectContainerArchive(bool invalidMagic = false)
        {
            Directory.CreateDirectory(root);
            var gpu = new byte[36]; "segs"u8.CopyTo(gpu);
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(gpu.AsSpan(8), 4); BinaryPrimitives.WriteUInt32LittleEndian(gpu.AsSpan(12), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(16), 4); BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(18), 4);
            new byte[] { 11, 22, 33, 44 }.CopyTo(gpu, 32);
            var frame = new byte[52]; BinaryPrimitives.WriteUInt32LittleEndian(frame, 16); frame[4] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(36), (uint)gpu.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(40), 4);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write("QZIP\0"u8); writer.Write("DC_INFO "u8); writer.Write(3u); writer.Write(16u); writer.Write(3u); writer.Write(1u);
            writer.Write(2133u); writer.Write(10u); writer.Write("DC_DATA "u8); writer.Write(23u);
            writer.Write((uint)(frame.Length + 16 + gpu.Length)); writer.Write(frame);
            writer.Write(1u); writer.Write("TEST_TAG"u8); writer.Write(1u); writer.Write(gpu);
            Bytes = stream.ToArray(); if (invalidMagic) Bytes[0] = 0;
            var volume = new byte[SourceOffset + Bytes.Length]; Bytes.CopyTo(volume, SourceOffset);
            File.WriteAllBytes(Path.ChangeExtension(IndexPath, ".dat"), volume);
            var index = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
            Put(20, IndexLayout.SupportedVersion); Put(101, 1); Put(IndexLayout.HeaderLength, 29);
            Put(IndexLayout.HeaderLength + 8, 1); Put(IndexLayout.HeaderLength + 12, (uint)SourceOffset);
            Put(IndexLayout.HeaderLength + 16, (uint)Bytes.Length); File.WriteAllBytes(IndexPath, index);
            void Put(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(at), value);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
