using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class SceneDependencyReaderTests
{
    [Fact]
    public void PreservesEveryWordAndFindsUniqueAmbiguousAndMissingCandidates()
    {
        var archive = Archive((40, 7), (50, 8), (60, 9), (60, 9));
        var bytes = Dependencies(archive, [new(50, 8, 123, 456, 789), new(60, 9, 321, 654, 987), new(70, 10, 111, 222, 333)]);
        var result = Inspect(bytes, archive);
        var inspection = Assert.IsType<SceneDependencyInspection>(result.Value);
        Assert.Equal(ReferenceStatus.Unique, inspection.References[0].TopLevelMatch.Status);
        Assert.Equal(ReferenceStatus.Ambiguous, inspection.References[1].TopLevelMatch.Status);
        Assert.Equal(2, inspection.References[1].TopLevelMatch.Candidates.Count);
        Assert.Equal(ReferenceStatus.Missing, inspection.References[2].TopLevelMatch.Status);
        Assert.Equal(new DependencyWords(50, 8, 123, 456, 789), inspection.References[0].Words);
        Assert.Equal(44, inspection.References[0].Extent.Offset);
        Assert.Equal(20, inspection.References[0].Extent.Length);
        Assert.Equal(3, result.UnresolvedReferences.Count);
        Assert.All(inspection.References, r => Assert.False(r.Relationship.Evidence.Verified));
        Assert.All(inspection.References, r => Assert.Null(r.Relationship.NavigationTarget));
        Assert.Equal("revision-a", result.Revision.PhysicalSourceStamp);
        Assert.NotNull(result.Extents[0].PayloadSha256);
        Assert.Equal(3, inspection.TableReferences);
        Assert.Equal(4u, inspection.TableRows);
    }

    [Fact]
    public void ZeroReferencesIsValidAndKeepsOwnerExtent()
    {
        var archive = Archive((40, 7));
        var value = Inspect(Dependencies(archive, []), archive).Value!;
        Assert.Empty(value.References);
        Assert.Equal(28, value.OwnerExtent.Offset);
        Assert.Equal(16, value.OwnerExtent.Length);
    }

    [Fact]
    public void ValidatesUnselectedOwnerKeys()
    {
        var archive = Archive((40, 7), (50, 8));
        var bytes = Dependencies(archive, []);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), 999);
        Assert.Contains("DependencyOwnerKeyMismatch", Inspect(bytes, archive).Diagnostics);
    }

    [Fact]
    public void RejectsTruncationAndTrailingData()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, [new(1, 2, 3, 4, 5)]);
        Assert.Null(Inspect(bytes[..^1], archive).Value);
        Assert.Contains("TruncatedDependencyReferences", Inspect(bytes[..^1], archive).Diagnostics);
        Assert.Contains("DependencyTrailingBytes", Inspect([.. bytes, 0], archive).Diagnostics);
        Assert.Contains("TruncatedDependencyHeader", Inspect(bytes[..20], archive).Diagnostics);
    }

    [Fact]
    public void RejectsUnsupportedVersionCountAndSelectedReferenceLimit()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, [new(1, 2, 3, 4, 5)]);
        var limited = new SceneDependencyReader().Inspect(bytes, archive, new("index-a", 0), "sample.dep", new(MaximumSelectedReferences: 0));
        Assert.Contains("SelectedDependencyReferenceLimit", limited.Diagnostics);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 2);
        Assert.Contains("DependencyOwnerCountMismatch", Inspect(bytes, archive).Diagnostics);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 19);
        Assert.Contains("UnsupportedDependencyVersion", Inspect(bytes, archive).Diagnostics);
    }

    [Fact]
    public void RejectsDifferentArchiveAndNonportableExtentNames()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, []);
        var reader = new SceneDependencyReader();
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, archive, new("other-index", 0), "sample.dep"));
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, archive, new("index-a", 0), "../sample.dep"));
        Assert.Throws<ArgumentException>(() => reader.Inspect(bytes, archive, new("index-a", 0, "member"), "sample.dep"));
    }

    [Fact]
    public void EnforcesByteRowAndReferenceBudgets()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, [new(1, 2, 3, 4, 5)]);
        var reader = new SceneDependencyReader();
        Assert.Contains("DependencyByteLimit", reader.Inspect(bytes, archive, new("index-a", 0),
            "sample.dep", new(MaximumBytes: 28)).Diagnostics);
        Assert.Contains("DependencyRowLimit", reader.Inspect(bytes, archive, new("index-a", 0),
            "sample.dep", new(MaximumRows: 0)).Diagnostics);
        Assert.Contains("DependencyReferenceLimit", reader.Inspect(bytes, archive, new("index-a", 0),
            "sample.dep", new(MaximumReferences: 0)).Diagnostics);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), uint.MaxValue);
        Assert.Contains("DependencyReferenceLimit", Inspect(bytes, archive).Diagnostics);
    }

    [Fact]
    public void InvalidFramingRetainsTheSourceExtent()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, []);
        bytes[0] = 0;
        var result = Inspect(bytes, archive);
        Assert.Null(result.Value);
        Assert.Contains("InvalidDependencySignature", result.Diagnostics);
        Assert.Equal(bytes.Length, result.Extents.Single().Length);
    }

    [Fact]
    public void HonorsCancellation()
    {
        var archive = Archive((40, 7));
        var bytes = Dependencies(archive, []);
        Assert.Throws<OperationCanceledException>(() => new SceneDependencyReader().Inspect(bytes, archive,
            new("index-a", 0), "sample.dep", token: new CancellationToken(true)));
    }

    private static DecodeResult<SceneDependencyInspection> Inspect(byte[] bytes, ArchiveSnapshot archive) =>
        new SceneDependencyReader().Inspect(bytes, archive, new("index-a", 0), "sample.dep");

    private static ArchiveSnapshot Archive(params (uint Type, uint Id)[] keys)
    {
        var stamp = new FileStamp(true, "file-a", 0, 0, 0, true);
        return new("sample.idx", "index-a", 18, 0, [], (uint)keys.Length,
            keys.Select((k, i) => new ResourceRecord(i, k.Type, 1, k.Id, 0, 0, 0, 0, "sample.dat")).ToArray(),
            [], stamp, "revision-a", []);
    }

    private static byte[] Dependencies(ArchiveSnapshot archive, DependencyWords[] references)
    {
        using var stream = new MemoryStream();
        stream.Write(IndexLayout.Magic); Write(stream, 18); Write(stream, (uint)archive.Records.Count);
        foreach (var row in archive.Records)
        {
            Write(stream, row.Type); Write(stream, row.Flags); Write(stream, row.Id);
            Write(stream, row.Ordinal == 0 ? (uint)references.Length : 0);
            if (row.Ordinal != 0) continue;
            foreach (var r in references)
            {
                Write(stream, r.Word0); Write(stream, r.Word1); Write(stream, r.Word2); Write(stream, r.Word3); Write(stream, r.Word4);
            }
        }
        return stream.ToArray();
    }

    private static void Write(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes);
    }
}
