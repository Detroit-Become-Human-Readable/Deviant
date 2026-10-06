using Deviant.Archives;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class ArchiveReaderTests
{
    [Fact]
    public async Task VariableCountsFlagsDuplicateKeysAndUnknownFieldsAreAccepted()
    {
        using var f = new ArchiveFixture();
        f.WriteIndex((17, 0, 9, 0, 3, 123, 107), (17, uint.MaxValue, 9, 3, 0, 0, 107)); f.WriteVolume(107, [1, 2, 3]);
        var snapshot = await new ArchiveReader().OpenAsync(f.Index);
        Assert.Equal(2, snapshot.Records.Count); Assert.Single(snapshot.Volumes); Assert.All(snapshot.Records, r => Assert.Equal(ResourceValidity.Valid, r.Validity));
        Assert.Equal("sample.d107", snapshot.Volumes[0].RelativeName); Assert.Equal(uint.MaxValue, snapshot.Records[1].Flags);
        Assert.Equal(123u, snapshot.Records[0].AlternativeSize); Assert.Equal(42u, snapshot.UnknownHeaderWord);
        Assert.Equal(snapshot.Records[0].Id, snapshot.Records[1].Id); Assert.NotEqual(snapshot.Records[0].Ordinal, snapshot.Records[1].Ordinal);
    }
    [Fact] public async Task EmptyCatalogIsValid() { using var f = new ArchiveFixture(); f.WriteIndex(); var s = await new ArchiveReader().OpenAsync(f.Source); Assert.Empty(s.Records); Assert.Empty(s.Volumes); }
    [Theory]
    [InlineData(0, "TruncatedHeader")]
    [InlineData(104, "TruncatedHeader")]
    [InlineData(106, "RecordCountMismatch")]
    [InlineData(132, "RecordCountMismatch")]
    public async Task TruncationAndTrailingBytesAreRejected(int length, string code)
    {
        using var f = new ArchiveFixture(); f.WriteIndex((2, 1, 5, 0, 1, 0, 0));
        var bytes = File.ReadAllBytes(f.Index); Array.Resize(ref bytes, length); File.WriteAllBytes(f.Index, bytes);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveReader().OpenAsync(f.Index)); Assert.Equal(code, error.Code);
    }
    [Theory] [InlineData(19u)] [InlineData(17u)]
    public async Task UnsupportedVersionsAreExplicit(uint version)
    {
        using var f = new ArchiveFixture(); f.WriteIndex(); var bytes = File.ReadAllBytes(f.Index); ArchiveFixture.Put(bytes, 20, version); File.WriteAllBytes(f.Index, bytes);
        Assert.Equal("UnsupportedVersion", (await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveReader().OpenAsync(f.Index))).Code);
    }
    [Fact]
    public async Task SignatureAndDeclaredCountAreValidated()
    {
        using var f = new ArchiveFixture(); f.WriteIndex(); var bytes = File.ReadAllBytes(f.Index); bytes[0] = 0; File.WriteAllBytes(f.Index, bytes);
        Assert.Equal("InvalidSignature", (await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveReader().OpenAsync(f.Index))).Code);
        f.WriteIndex(); bytes = File.ReadAllBytes(f.Index); ArchiveFixture.Put(bytes, 101, uint.MaxValue); File.WriteAllBytes(f.Index, bytes);
        Assert.Equal("RecordCountMismatch", (await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveReader().OpenAsync(f.Index))).Code);
    }
    [Fact]
    public async Task MissingVolumeAndOverflowingRangeRemainBrowsable()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 1, 0, 7), (2, 1, 2, uint.MaxValue, uint.MaxValue, 0, 0), (3, 1, 3, 1, 0, 0, 0));
        f.WriteVolume(0, [7]); var s = await new ArchiveReader().OpenAsync(f.Index);
        Assert.Equal(ResourceValidity.MissingVolume, s.Records[0].Validity); Assert.Equal(ResourceValidity.InvalidRange, s.Records[1].Validity);
        Assert.Equal(ResourceValidity.Valid, s.Records[2].Validity); Assert.Equal(3, s.Records.Count);
    }
    [Fact]
    public async Task LargeSparseVolumeAndUnsignedOffsetAreAccepted()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((99, 7, uint.MaxValue, uint.MaxValue - 16, 8, 0, 0));
        f.SparseVolume(0, (long)uint.MaxValue + 32); var s = await new ArchiveReader().OpenAsync(f.Index);
        Assert.Equal(ResourceValidity.Valid, Assert.Single(s.Records).Validity); Assert.True(s.Volumes[0].Stamp.Length > uint.MaxValue);
    }
    [Fact]
    public async Task AmbiguousDirectoryNeedsExplicitIndex()
    {
        using var f = new ArchiveFixture(); f.WriteIndex(); File.Copy(f.Index, Path.Combine(f.Source, "other.idx"));
        Assert.Equal("AmbiguousInput", (await Assert.ThrowsAsync<ArchiveException>(() => new ArchiveReader().OpenAsync(f.Source))).Code);
    }
    [Fact]
    public async Task CancellationIsHonored()
    {
        using var f = new ArchiveFixture(); f.WriteIndex(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchiveReader().OpenAsync(f.Index, cancellationToken: stop.Token));
    }
    [Fact]
    public async Task IndexIdentityIsIndependentOfExecutableAndDetectsReservedByteEdit()
    {
        using var f = new ArchiveFixture(); f.WriteIndex(); var a = await new ArchiveReader().OpenAsync(f.Index);
        File.WriteAllBytes(Path.Combine(f.Source, "program.exe"), [1, 9]); var b = await new ArchiveReader().OpenAsync(f.Index); Assert.Equal(a.IndexContentId, b.IndexContentId);
        var bytes = File.ReadAllBytes(f.Index); bytes[40] = 42; File.WriteAllBytes(f.Index, bytes); var c = await new ArchiveReader().OpenAsync(f.Index); Assert.NotEqual(a.IndexContentId, c.IndexContentId);
    }
    [Fact]
    public async Task FullContentIdentityIncludesMiddleBytesAndSidecarPresence()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((3, 1, 7, 0, 20000, 0, 0)); var data = new byte[20000]; f.WriteVolume(0, data);
        var a = await ArchiveVerifier.VerifyContentAsync(await new ArchiveReader().OpenAsync(f.Index));
        data[10000] = 3; f.WriteVolume(0, data); var b = await ArchiveVerifier.VerifyContentAsync(await new ArchiveReader().OpenAsync(f.Index)); Assert.NotEqual(a.ArchiveContentId, b.ArchiveContentId);
        File.WriteAllBytes(Path.ChangeExtension(f.Index, ".dep"), [1]); var c = await ArchiveVerifier.VerifyContentAsync(await new ArchiveReader().OpenAsync(f.Index)); Assert.NotEqual(b.ArchiveContentId, c.ArchiveContentId);
    }
}
