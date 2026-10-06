using Deviant.Core;
using Deviant.Export;
using Xunit;

namespace Deviant.Tests;

public sealed class OutputSafetyTests
{
    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("folder/../../escape.bin")]
    [InlineData("/absolute.bin")]
    [InlineData("NUL.bin")]
    [InlineData("folder/COM1.bin")]
    [InlineData("file:stream")]
    [InlineData("folder./file.bin")]
    public void UnsafeRelativeNamesAreRejected(string name)
    { using var f = new ArchiveFixture(); Assert.Equal("UnsafeOutput", Assert.Throws<ArchiveException>(() => OutputSafety.ResolveOutputPath(f.Output, name)).Code); }
    [Fact] public void SourceDirectoryCannotReceiveExports() { using var f = new ArchiveFixture(); Assert.Throws<ArchiveException>(() => OutputSafety.ValidateOutputRoot(Path.Combine(f.Source, "exports"), f.Source)); }
    [Fact]
    public void SymbolicLinkDestinationIsRejected()
    {
        using var f = new ArchiveFixture(); Directory.CreateDirectory(f.Output); var link = Path.Combine(f.Output, "alias");
        Directory.CreateSymbolicLink(link, f.Source);
        Assert.Equal("UnsafeOutput", Assert.Throws<ArchiveException>(() => OutputSafety.ResolveOutputPath(f.Output, "alias/file.bin")).Code);
        Directory.Delete(link);
    }
    [Fact]
    public void ExtendedPathAliasCannotBypassSourceDirectoryGuard()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ArchiveFixture();
        Assert.Throws<ArchiveException>(() => OutputSafety.ValidateOutputRoot("\\\\?\\" + Path.Combine(f.Source, "exports"), f.Source));
    }
    [Fact]
    public void DriveRootSourceStillContainsItsDescendants()
    {
        using var f = new ArchiveFixture();
        Assert.Throws<ArchiveException>(() => OutputSafety.ValidateOutputRoot(f.Output, Path.GetPathRoot(f.Source)!));
    }
}
