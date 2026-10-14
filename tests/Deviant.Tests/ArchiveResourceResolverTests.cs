using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class ArchiveResourceResolverTests
{
    [Fact]
    public async Task SequentialOrdinalsReadTheSelectedResourceWindow()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 9, 0, 2, 0, 0), (17, 0, 9, 2, 2, 0, 0));
        fixture.WriteVolume(0, [1, 2, 3, 4]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        var resolver = new ArchiveResourceResolver(snapshot);

        Assert.Equal(new byte[] { 4 }, await resolver.ReadAsync(new(snapshot.IndexContentId, 1), 1, 1));
        Assert.Equal("InvalidRange", (await Assert.ThrowsAsync<ArchiveException>(() =>
            resolver.ReadAsync(new(snapshot.IndexContentId, 1), 1, 2))).Code);
    }

    [Fact]
    public async Task SparseReorderedOrdinalsKeepTheGeneralLookupAndMissingResourceResult()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteIndex((17, 0, 9, 0, 2, 0, 0), (17, 0, 10, 2, 2, 0, 0));
        fixture.WriteVolume(0, [1, 2, 3, 4]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        snapshot = snapshot with { Records = [snapshot.Records[1] with { Ordinal = 42 }, snapshot.Records[0] with { Ordinal = 9 }] };
        var resolver = new ArchiveResourceResolver(snapshot);

        Assert.Equal(new byte[] { 3, 4 }, await resolver.ReadAsync(new(snapshot.IndexContentId, 42), 0, 2));
        Assert.Equal(new byte[] { 1, 2 }, await resolver.ReadAsync(new(snapshot.IndexContentId, 9), 0, 2));
        Assert.Equal("MissingResource", (await Assert.ThrowsAsync<ArchiveException>(() =>
            resolver.ReadAsync(new(snapshot.IndexContentId, 0), 0, 1))).Code);
    }
}
