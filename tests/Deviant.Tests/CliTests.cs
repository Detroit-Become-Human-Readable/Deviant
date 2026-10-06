using Deviant.Cli;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class CliTests
{
    [Fact]
    public void ParsesSharedOptionsAndRepeatedCommaSeparatedOrdinals()
    {
        var options = Program.Options.Parse([
            "inspect", "--input", "synthetic.idx", "--type", "0x2A", "--id", "42", "--package", "3",
            "--search", "sample-volume", "--offset", "5", "--limit", "12", "--ordinal", "1,2", "--ordinal", "8", "--json"
        ]);

        Assert.Equal("inspect", options.Command);
        Assert.Equal("synthetic.idx", options.Input);
        Assert.Equal((uint)42, options.Type);
        Assert.Equal((uint)42, options.Id);
        Assert.Equal((uint)3, options.Package);
        Assert.Equal("sample-volume", options.Search);
        Assert.Equal(5, options.Offset);
        Assert.Equal(12, options.Limit);
        Assert.Equal(new long[] { 1, 2, 8 }, options.Ordinals);
        Assert.True(options.Json);
    }

    [Fact]
    public void RequiresExplicitExtractScopeAndOutput()
    {
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["extract", "--input", "synthetic.idx", "--output", "out"]));
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["extract", "--input", "synthetic.idx", "--all"]));
        var broad = Program.Options.Parse(["extract", "--input", "synthetic.idx", "--output", "out", "--all"]);
        Assert.True(broad.All);
    }

    [Fact]
    public void RejectsInvalidLimitAndMissingInput()
    {
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["list", "--input", "synthetic.idx", "--limit", "10001"]));
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["list", "--input", "synthetic.idx", "--limit", "999999999999999999999999"]));
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["list", "--input", "synthetic.idx", "--cancel-after", "2147483648"]));
        Assert.ThrowsAny<Exception>(() => Program.Options.Parse(["verify", "--full-content"]));
    }

    [Fact]
    public void ExportExitCodeDistinguishesCancellationSkippedAndFailures()
    {
        Assert.Equal(130, Program.ExportExitCode(Manifest(true, Entry(ExportStatus.Cancelled))));
        Assert.Equal(0, Program.ExportExitCode(Manifest(false, Entry(ExportStatus.Skipped))));
        Assert.Equal(2, Program.ExportExitCode(Manifest(false, Entry(ExportStatus.Failed))));
    }

    private static ExportManifest Manifest(bool cancelled, params ExportEntry[] entries) => new(
        1, "synthetic.idx", "index-content", "physical-stamp", "test", DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, cancelled, entries);

    private static ExportEntry Entry(ExportStatus status) => new(0, 1, 2, 0, "sample.dat", 0, 4,
        "p00/sample.bin", status);
}
