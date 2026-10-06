using Deviant.Archives;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Tests;

public sealed class IdentityAndNativeTests
{
    [Fact]
    public void FramedDomainSeparatedHashesAreUnambiguous()
    {
        Assert.NotEqual(SourceIdentity.HashFields("a", ["ab", "c"]), SourceIdentity.HashFields("a", ["a", "bc"]));
        Assert.NotEqual(SourceIdentity.HashFields("a", ["x"]), SourceIdentity.HashFields("b", ["x"]));
    }
    [Fact]
    public async Task UnreliableStampsReuseOnlyRawRowsAndRefreshRangeValidity()
    {
        using var f = new ArchiveFixture(); f.WriteIndex((1, 1, 1, 0, 2, 0, 0)); f.WriteVolume(0, [1, 2]);
        var reader = new ArchiveReader(new UnreliableProvider()); var first = await reader.OpenAsync(f.Index);
        Assert.False(first.ReliableVolumeStamps); var cache = Path.Combine(f.Root, "cache");
        using (var c = CatalogService.Open(first, cache)) Assert.Equal(ResourceValidity.Valid, c.Query(new())[0].Validity);
        f.WriteVolume(0, [1]); var second = await reader.OpenAsync(f.Index);
        using var reopened = CatalogService.Open(second, cache); Assert.True(reopened.ReusedIndexRows);
        Assert.Equal(ResourceValidity.InvalidRange, reopened.Query(new())[0].Validity); Assert.NotEqual(first.PhysicalSourceStamp, second.PhysicalSourceStamp);
    }
    private sealed class UnreliableProvider : ISourceRevisionProvider
    {
        private readonly FileRevisionProvider inner = new();
        public FileStamp Capture(string path) => inner.Capture(path) with { Reliable = false, FileIdentity = "", ChangeTimeUtcTicks = 0 };
        public FileStamp Capture(SafeFileHandle handle, string path) => inner.Capture(handle, path) with { Reliable = false, FileIdentity = "", ChangeTimeUtcTicks = 0 };
    }
    [Fact]
    public void NativeSqliteIsBeyondKnownVulnerableVersion()
    {
        using var database = new SqliteConnection("Data Source=:memory:"); database.Open(); using var command = database.CreateCommand();
        command.CommandText = "SELECT sqlite_version()"; var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"Native SQLite {version} is outdated.");
    }
    [Fact]
    public async Task ExtendedAliasCacheInsideSourceIsRejectedBeforeWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ArchiveFixture(); f.WriteIndex(); var snapshot = await new ArchiveReader().OpenAsync(f.Index);
        var cache = Path.Combine(f.Source, "cache");
        Assert.Equal("UnsafeCache", Assert.Throws<ArchiveException>(() => CatalogService.Open(snapshot, "\\\\?\\" + cache)).Code);
        Assert.False(Directory.Exists(cache));
    }
}
