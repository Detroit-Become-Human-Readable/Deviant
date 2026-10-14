using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class TypeRegistryDatasetReaderTests
{
    private static readonly SourceRevision Revision = new("generated-index", "generated-source-stamp");
    private const string ExecutableHash = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task LoadsMatchingCheckpointAndPreservesEvidenceAndCompetingNames()
    {
        var bytes = Checkpoint(Revision, ExecutableHash,
        [
            Definition("Archive", 17, "Primary name", "Unknown support note", "generated-parser-a"),
            Definition("Archive", 17, "Alternate name", "Unknown support note", "generated-parser-b"),
            Definition("Native", 17, "Native id 17", "Unknown native support", "generated-parser-c",
                fields: [new { Name = "sample", DataType = "uint32", Offset = "0", Units = (string?)null,
                    Evidence = "ObservedStructure", Meaning = (string?)null }])
        ]);
        var budget = new AssetMemoryBudget();
        using var lease = await Load(bytes, new(Revision, ExecutableHash), budget);

        Assert.Equal(Revision, lease.Revision);
        Assert.Equal(ExecutableHash, lease.ExecutableSha256);
        Assert.Equal(Hash(bytes), lease.DatasetSha256);
        Assert.Equal(3, lease.DefinitionCount);
        var entries = lease.Registry.Reconcile([]);
        Assert.Equal(2, entries.Count);
        var entry = Assert.Single(entries, item => item.Type.Domain == TypeDomain.Archive);
        Assert.Equal("Primary name", entry.Name);
        Assert.Equal(new[] { "Primary name", "Alternate name" }, entry.Definition.CompetingNames);
        Assert.Equal("Unknown support note", entry.Definition.Support.Structure);
        Assert.Equal(new[] { "generated-parser-a", "generated-parser-b" },
            entry.Definition.Evidence.Select(item => item.Source).Order().ToArray());
        var nativeEntry = Assert.Single(entries, item => item.Type.Domain == TypeDomain.Native);
        Assert.Equal("Native id 17", nativeEntry.Name);
        Assert.Equal(EvidenceLevel.ObservedStructure, Assert.Single(nativeEntry.Definition.Fields).Evidence);
        Assert.True(budget.UsedBytes > 0);
    }

    [Theory]
    [InlineData("other-index", "generated-source-stamp", ExecutableHash)]
    [InlineData("generated-index", "other-source-stamp", ExecutableHash)]
    [InlineData("generated-index", "generated-source-stamp", "2222222222222222222222222222222222222222222222222222222222222222")]
    public async Task RejectsCheckpointQualificationMismatch(string index, string stamp, string executable)
    {
        var bytes = Checkpoint(new(index, stamp), executable, [Definition("Native", 3, "Native type", "Known", "generated-source")]);
        var budget = new AssetMemoryBudget();
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(bytes, new(Revision, ExecutableHash), budget));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task LegacyArrayRequiresAndVerifiesExpectedDigest()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { Definition("SerializedClass", null, "ExampleClass", "Unknown", "generated-source", "EXAMPLE", 1) });
        var budget = new AssetMemoryBudget();
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(bytes, new(Revision, ExecutableHash), budget));
        Assert.Equal(0, budget.UsedBytes);

        using var lease = await Load(bytes, new(Revision, ExecutableHash, Hash(bytes)), budget);
        Assert.Equal(TypeDomain.SerializedClass, Assert.Single(lease.Registry.Reconcile([])).Type.Domain);
    }

    [Fact]
    public async Task RejectsNegativeOutOfRangeAndUnknownDomainValues()
    {
        var cases = new[]
        {
            Encoding.UTF8.GetBytes("[{\"Type\":{\"Domain\":\"Archive\",\"Id\":-1},\"Name\":\"x\",\"Category\":\"y\"}]"),
            Encoding.UTF8.GetBytes("[{\"Type\":{\"Domain\":\"Archive\",\"Id\":4294967296},\"Name\":\"x\",\"Category\":\"y\"}]"),
            Encoding.UTF8.GetBytes("[{\"Type\":{\"Domain\":2,\"Id\":1},\"Name\":\"x\",\"Category\":\"y\"}]")
        };
        foreach (var bytes in cases)
        {
            var budget = new AssetMemoryBudget();
            await Assert.ThrowsAnyAsync<Exception>(() => Load(bytes,
                new(Revision, ExecutableHash, Hash(bytes)), budget));
            Assert.Equal(0, budget.UsedBytes);
        }
    }

    [Fact]
    public async Task EnforcesRawDepthStringAndDefinitionLimits()
    {
        var smallBudget = new AssetMemoryBudget();
        var bytes = Checkpoint(Revision, ExecutableHash, [Definition("Native", 3, "Native type", "Known", "generated")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(bytes, new(Revision, ExecutableHash), smallBudget,
            new(MaximumRawBytes: 32)));
        Assert.Equal(0, smallBudget.UsedBytes);

        var stringDataset = Legacy(Definition("Native", 3, new string('x', 80), "Known", "generated"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(stringDataset,
            new(Revision, ExecutableHash, Hash(stringDataset)), new AssetMemoryBudget(),
            new(MaximumStringBytes: 64)));

        var definitions = Enumerable.Range(0, 2).Select(index => Definition("Archive", (uint)index,
            $"Type {index}", "Known", "generated")).ToArray();
        var countDataset = Checkpoint(Revision, ExecutableHash, definitions);
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(countDataset,
            new(Revision, ExecutableHash), new AssetMemoryBudget(), new(MaximumDefinitions: 1)));

        var fieldAndEvidence = Checkpoint(Revision, ExecutableHash,
            [Definition("Native", 3, "Native type", "Known", "generated",
                fields: [new { Name = "field", DataType = "u32", Offset = "0", Units = (string?)null,
                    Evidence = "ObservedStructure", Meaning = (string?)null }])]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(fieldAndEvidence,
            new(Revision, ExecutableHash), new AssetMemoryBudget(), new(MaximumFieldsAndEvidence: 1)));

        var nestedArray = Legacy(Definition("Archive", 1, "Generated", "Known", "generated",
            questions: ["one", "two"]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Load(nestedArray,
            new(Revision, ExecutableHash, Hash(nestedArray)), new AssetMemoryBudget(), new(MaximumArrayItems: 1)));

        await Assert.ThrowsAsync<InvalidDataException>(() => Load(bytes,
            new(Revision, ExecutableHash), new AssetMemoryBudget(), new(MaximumJsonTokens: 8)));

        var deep = Encoding.UTF8.GetBytes("{" + string.Concat(Enumerable.Repeat("\"a\":{", 34)) + "\"x\":1" + new string('}', 34) + "}");
        await Assert.ThrowsAnyAsync<Exception>(() => Load(deep, new(Revision, ExecutableHash),
            new AssetMemoryBudget(), new(MaximumDepth: 32)));
    }

    [Fact]
    public async Task BudgetPressureParseFailureAndCancellationReleaseReservations()
    {
        var bytes = Checkpoint(Revision, ExecutableHash, [Definition("Archive", 1, "Example", "Known", "generated")]);
        var constrained = new AssetMemoryBudget(1024);
        await Assert.ThrowsAnyAsync<ArchiveException>(() => Load(bytes, new(Revision, ExecutableHash), constrained));
        Assert.Equal(0, constrained.UsedBytes);

        var parseFailureBudget = new AssetMemoryBudget();
        await Assert.ThrowsAnyAsync<Exception>(() => Load("{"u8.ToArray(),
            new(Revision, ExecutableHash), parseFailureBudget));
        Assert.Equal(0, parseFailureBudget.UsedBytes);

        var cancelledBudget = new AssetMemoryBudget();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Load(bytes,
            new(Revision, ExecutableHash), cancelledBudget, token: cancellation.Token));
        Assert.Equal(0, cancelledBudget.UsedBytes);

        var midReadBudget = new AssetMemoryBudget();
        using var midReadCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TypeRegistryDatasetReader.LoadAsync(
            new CancellingReadStream(bytes, midReadCancellation), new(Revision, ExecutableHash),
            midReadBudget, token: midReadCancellation.Token));
        Assert.Equal(0, midReadBudget.UsedBytes);
    }

    [Fact]
    public async Task DisposalInvalidatesLeaseAccessAndReleasesSharedBudget()
    {
        var bytes = Checkpoint(Revision, ExecutableHash, [Definition("Native", 3, "Native type", "Known", "generated")]);
        var budget = new AssetMemoryBudget();
        var lease = await Load(bytes, new(Revision, ExecutableHash), budget);
        Assert.True(budget.UsedBytes > 0);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Registry);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.DatasetSha256);
    }

    [Fact]
    public async Task RetainsValidatedMetadataEstimateAndKeepsDefinitionCountAfterIntermediateRelease()
    {
        var bytes = Checkpoint(Revision, ExecutableHash,
            [Definition("Archive", 1, "Generated type", "Known", "generated-source")]);
        var budget = new AssetMemoryBudget(256L * 1024 * 1024);
        var lease = await Load(bytes, new(Revision, ExecutableHash), budget);
        Assert.Equal(1, lease.DefinitionCount);
        Assert.InRange(budget.UsedBytes, 1L, 64L * 1024 * 1024);

        using (budget.Reserve(200L * 1024 * 1024, "independent generated registry work"))
            Assert.True(budget.UsedBytes > 200L * 1024 * 1024);

        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task OversizedRevisionIsRejectedBeforeReadingAndWithoutBudgetRetention()
    {
        var bytes = Checkpoint(Revision, ExecutableHash,
            [Definition("Archive", 1, "Generated type", "Known", "generated-source")]);
        var oversized = new string('x', 4097);
        var revisions = new[]
        {
            new SourceRevision(oversized, Revision.PhysicalSourceStamp),
            new SourceRevision(Revision.IndexContentId, oversized)
        };

        foreach (var revision in revisions)
        {
            var budget = new AssetMemoryBudget();
            using var stream = new CountingReadStream(bytes);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => TypeRegistryDatasetReader.LoadAsync(
                stream, new(revision, ExecutableHash), budget));
            Assert.Equal(0, stream.ReadCount);
            Assert.Equal(0, budget.UsedBytes);
        }
    }

    private static async Task<TypeRegistryDatasetLease> Load(byte[] bytes,
        TypeRegistryDatasetExpectation expectation, AssetMemoryBudget budget,
        TypeRegistryDatasetLimits? limits = null, CancellationToken token = default)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await TypeRegistryDatasetReader.LoadAsync(stream, expectation, budget, limits, token);
    }

    private static byte[] Legacy(params object[] definitions) => JsonSerializer.SerializeToUtf8Bytes(definitions);

    private static byte[] Checkpoint(SourceRevision revision, string executable, object[] definitions) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Format = TypeRegistryDatasetReader.CheckpointFormat,
            IndexContentId = revision.IndexContentId,
            SourceRevision = new { revision.IndexContentId, revision.PhysicalSourceStamp },
            ExecutableSha256 = executable,
            Definitions = definitions
        });

    private static object Definition(string domain, uint? id, string name, string supportStructure,
        string evidenceSource, string? tag = null, uint? version = null,
        object[]? fields = null, string[]? questions = null) => new
    {
        Type = new { Domain = domain, Id = id, Tag = tag, Version = version },
        Name = name,
        Category = "Generated",
        Evidence = new[] { new { Source = evidenceSource, Statement = "Generated fixture evidence", Level = "ObservedStructure", ExecutableSha256 = (string?)null, FunctionRva = (long?)null } },
        Fields = fields ?? Array.Empty<object>(),
        Support = new { Structure = supportStructure, Properties = "Unknown", References = "Unknown", Placement = "Unknown", Visualization = "None", Export = "Original" },
        Questions = questions ?? ["Generated question"],
        Registered = false,
        CompetingNames = Array.Empty<string>(),
        Tags = Array.Empty<string>(),
        Notes = new { Domain = "UnknownDomain", Evidence = "UnknownEvidence", Level = "UnknownLevel" }
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation)
        : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Read(buffer.Span[..Math.Min(buffer.Length, 16)]);
            cancellation.Cancel();
            return ValueTask.FromResult(read);
        }
    }

    private sealed class CountingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

