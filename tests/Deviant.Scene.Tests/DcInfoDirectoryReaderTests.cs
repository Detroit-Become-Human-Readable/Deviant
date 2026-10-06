using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class DcInfoDirectoryReaderTests
{
    private static readonly ResourceIdentity Container = new("index-a", 4);
    private static readonly SourceRevision Revision = new("index-a", "revision-a");
    private static readonly SourceExtent Physical = new("sample.dat", 100, 200, "physical-hash");

    [Fact]
    public void RetainsRawPairsDuplicatesUnknownWordAndBothExtentDomains()
    {
        var bytes = Prefix(765, (40, 7), (50, 8), (40, 7));
        var preceding = new DecodeStep("ContainerDecoder", "1", "segment", "segment-hash");
        var result = new DcInfoDirectoryReader().Inspect([.. bytes, 10, 20], Container, Revision, [Physical],
            decodedOffset: 1000, precedingSteps: [preceding]);
        var value = Assert.IsType<DcInfoDirectoryInspection>(result.Value);
        Assert.Equal(765u, value.UnknownWord);
        Assert.Equal(3u, value.DeclaredEntryCount);
        Assert.Equal(32u, value.DeclaredBodyLength);
        Assert.Equal(new DecodedExtent(Container, 1000, 48), value.Extent);
        Assert.Equal(new DecodedExtent(Container, 1024, 8), value.Entries[0].Extent);
        Assert.Equal(40u, value.Entries[0].Word0);
        Assert.Equal(7u, value.Entries[0].Word1);
        Assert.Equal(new long[] { 0, 2 }, value.Duplicates.Single().EntryOrdinals);
        Assert.Equal(new DecodedExtent(Container, 1048, 2), value.UnparsedRemainder);
        Assert.Equal(Physical, result.Extents.Single());
        Assert.Equal(Revision, result.Revision);
        Assert.Equal(preceding, result.Steps[0]);
        Assert.NotNull(result.Steps[1].PayloadSha256);
        Assert.Empty(result.UnresolvedReferences);
    }

    [Fact]
    public void ZeroEntriesIsAValidDirectory()
    {
        var result = Inspect(Prefix(2));
        Assert.Empty(result.Value!.Entries);
        Assert.Empty(result.Value.Duplicates);
        Assert.Null(result.Value.UnparsedRemainder);
        Assert.Equal(24, result.Value.Extent.Length);
    }

    [Fact]
    public void RejectsUnknownVersionsAndBadSignature()
    {
        var bytes = Prefix(2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4);
        Assert.Contains("UnsupportedDirectoryVersion", Inspect(bytes).Diagnostics);
        bytes[0] = 0;
        Assert.Contains("InvalidDirectorySignature", Inspect(bytes).Diagnostics);
    }

    [Fact]
    public void RejectsTruncatedHeadersBodiesAndEntries()
    {
        var bytes = Prefix(2, (40, 7));
        Assert.Contains("TruncatedDirectoryHeader", Inspect(bytes[..15]).Diagnostics);
        Assert.Contains("TruncatedDirectoryBody", Inspect(bytes[..23]).Diagnostics);
        var result = Inspect(bytes[..^1]);
        Assert.Contains("TruncatedDirectoryEntries", result.Diagnostics);
        Assert.Equal(Physical, result.Extents.Single());
        Assert.Null(result.Value);
    }

    [Fact]
    public void RejectsCountLengthMismatchAndEnforcesBudgets()
    {
        var bytes = Prefix(2, (40, 7));
        Assert.Contains("DirectoryByteLimit", new DcInfoDirectoryReader().Inspect(bytes, Container, Revision,
            [Physical], limits: new(MaximumBytes: 24)).Diagnostics);
        Assert.Contains("DirectoryEntryLimit", new DcInfoDirectoryReader().Inspect(bytes, Container, Revision,
            [Physical], limits: new(MaximumEntries: 0)).Diagnostics);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0);
        Assert.Contains("DirectoryCountLengthMismatch", Inspect(bytes).Diagnostics);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), uint.MaxValue);
        Assert.Contains("DirectoryEntryLimit", Inspect(bytes).Diagnostics);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0);
        Assert.Contains("InvalidDirectoryBodyLength", Inspect(bytes).Diagnostics);
    }

    [Fact]
    public void RejectsNonportableOrOverflowingPhysicalProvenance()
    {
        var bytes = Prefix(2);
        var reader = new DcInfoDirectoryReader();
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, Container, Revision,
            [new("../sample.dat", 0, 1)]));
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, Container, Revision,
            [new("sample.dat", long.MaxValue, 1)]));
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, Container, Revision,
            [new("sample.dat", -1, 1)]));
    }

    [Fact]
    public void RejectsMismatchedRevisionBadOffsetsAndCancellation()
    {
        var bytes = Prefix(2);
        var reader = new DcInfoDirectoryReader();
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, Container, new("other", "r"), [Physical]));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Inspect(bytes, Container, Revision, [Physical], decodedOffset: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Inspect(bytes, Container, Revision, [Physical], decodedOffset: long.MaxValue));
        Assert.Throws<OperationCanceledException>(() => reader.Inspect(bytes, Container, Revision, [Physical], token: new(true)));
    }

    private static DecodeResult<DcInfoDirectoryInspection> Inspect(byte[] bytes) =>
        new DcInfoDirectoryReader().Inspect(bytes, Container, Revision, [Physical]);

    private static byte[] Prefix(uint unknown, params (uint Word0, uint Word1)[] entries)
    {
        var bytes = new byte[24 + entries.Length * 8];
        "DC_INFO "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(8 + entries.Length * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), unknown);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + i * 8), entries[i].Word0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + i * 8), entries[i].Word1);
        }
        return bytes;
    }
}
