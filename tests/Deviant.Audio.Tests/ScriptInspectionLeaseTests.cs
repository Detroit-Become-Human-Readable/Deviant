using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class ScriptInspectionLeaseTests
{
    private const uint ScriptResourceType = 63;
    private const uint SyntheticResourceId = 731;
    private static readonly byte[] LuaHeader = [27, 76, 117, 97, 81, 0, 1, 4, 4, 4, 4, 0];

    [Fact]
    public async Task OwnsOuterBundleAndRepeatedSelectionDoesNotAccumulateLuaBudget()
    {
        using var fixture = CreateFixture(Bundle(
            new Chunk(0x11, LuaChunk("first", [ReturnWord]), []),
            new Chunk(0x22, LuaChunk("second", [ReturnWord, ReturnWord]),
                [new Metadata(0x33, [0xde, 0xad, 0xbe, 0xef])])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var resource = Resource(snapshot);
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, resource, budget);

        Assert.NotNull(lease.Bundle.Value);
        Assert.Null(lease.SelectedChunk);
        Assert.Null(lease.LuaChunk);
        var source = Assert.Single(lease.Bundle.Value!.OriginalPhysicalExtents);
        Assert.Equal(snapshot.Records[(int)resource.Ordinal].StoredSize, source.Length);
        Assert.NotNull(source.PayloadSha256);
        var bundleBudget = budget.UsedBytes;

        var selected = lease.SelectChunk(1);
        Assert.Equal(0x22UL, lease.SelectedChunk!.OpaqueKey);
        Assert.Equal(selected.Value!.Identity, lease.SelectedChunk.Identity);
        Assert.Equal("second", selected.Value.RootPrototype.SourceName.DisplayText);
        Assert.Equal(2, selected.Value.RootPrototype.Code.Count);
        Assert.Equal(0x33UL, Assert.Single(lease.SelectedChunk.Metadata).OpaqueKey);
        var selectedBudget = budget.UsedBytes;
        Assert.True(selectedBudget > bundleBudget);

        lease.SelectChunk(0);
        Assert.Equal(selectedBudget, budget.UsedBytes);
        Assert.Equal("first", lease.LuaChunk!.Value!.RootPrototype.SourceName.DisplayText);
        lease.SelectChunk(1);
        Assert.Equal(selectedBudget, budget.UsedBytes);
        lease.EnsureUnchanged();

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Bundle);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.SelectedChunk);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.LuaChunk);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task SelectMetadataUsesRetainedBundleProvenanceAndAlternatesWithLua()
    {
        var payload = BooleanPropertyValue(0x12345678, 1);
        using var fixture = CreateFixture(Bundle(
            new Chunk(0x11, LuaChunk("first", [ReturnWord]), []),
            new Chunk(0x22, LuaChunk("second", [ReturnWord]), [new Metadata(0x33, payload)])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);
        var outer = lease.Bundle.Value!;
        var bundleBudget = budget.UsedBytes;

        var parsed = lease.SelectMetadata(1, 0);
        var selected = outer.Chunks[1].Metadata[0];
        var value = Assert.IsType<QdPropertyValueDocument>(parsed.Value);
        Assert.Equal(selected.Identity, lease.SelectedMetadata!.Identity);
        Assert.Equal(selected.Identity, parsed.Identity);
        Assert.Equal(selected.PayloadExtent, value.InputExtent);
        Assert.Equal(payload, value.RawBytes.ToArray());
        Assert.Equal(outer.OriginalPhysicalExtents, value.OriginalPhysicalExtents);
        Assert.Equal(snapshot.PhysicalSourceStamp, parsed.Revision.PhysicalSourceStamp);
        Assert.Null(lease.LuaChunk);
        Assert.Equal(parsed, lease.PropertyValues);
        var selectedBudget = budget.UsedBytes;
        Assert.True(selectedBudget > bundleBudget);

        lease.SelectChunk(0);
        Assert.Null(lease.PropertyValues);
        Assert.Null(lease.SelectedMetadata);
        Assert.NotNull(lease.LuaChunk);
        Assert.Equal(selectedBudget, budget.UsedBytes);

        lease.SelectMetadata(1, 0);
        Assert.Null(lease.LuaChunk);
        Assert.NotNull(lease.PropertyValues);
        Assert.Equal(selectedBudget, budget.UsedBytes);
        lease.EnsureUnchanged();

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = lease.SelectedMetadata);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.PropertyValues);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MetadataCancellationAndParseFailureReleasePriorSelection()
    {
        using var fixture = CreateFixture(Bundle(new Chunk(0x44, LuaChunk("metadata", [ReturnWord]),
            [new Metadata(0x55, [0x80])])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);
        var bundleBudget = budget.UsedBytes;
        lease.SelectChunk(0);
        Assert.NotNull(lease.LuaChunk);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => lease.SelectMetadata(0, 0, cancellation.Token));
        Assert.Null(lease.LuaChunk);
        Assert.Null(lease.PropertyValues);
        Assert.Null(lease.SelectedChunk);
        Assert.Equal(bundleBudget, budget.UsedBytes);

        var failed = lease.SelectMetadata(0, 0);
        Assert.Null(failed.Value);
        Assert.NotEmpty(failed.Diagnostics);
        Assert.NotNull(lease.SelectedMetadata);
        Assert.Null(lease.LuaChunk);
        Assert.Equal(bundleBudget + 4 * 1024, budget.UsedBytes);

        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task RejectsCrossIndexWrongTypeAndOversizedResourcesWithoutReservingMemory()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(ScriptResourceType, SyntheticResourceId, Bundle(new Chunk(0x44, LuaHeader, [])));
        fixture.AddResource(64, 732, Bundle(new Chunk(0x45, LuaHeader, [])));
        fixture.WriteIndex();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var row = Assert.Single(snapshot.Records, record => record.Type == ScriptResourceType);

        await Assert.ThrowsAsync<ArchiveException>(() => ScriptInspectionLease.OpenAsync(snapshot,
            new ResourceIdentity("other-index", row.Ordinal), budget));
        await Assert.ThrowsAsync<ArchiveException>(() => ScriptInspectionLease.OpenAsync(snapshot,
            new ResourceIdentity(snapshot.IndexContentId, snapshot.Records.Single(record => record.Type == 64).Ordinal), budget));

        var oversized = snapshot with
        {
            Records = snapshot.Records.Select(record => record.Ordinal == row.Ordinal
                ? record with { StoredSize = 16L * 1024 * 1024 + 1 }
                : record).ToArray()
        };
        await Assert.ThrowsAsync<ArchiveException>(() => ScriptInspectionLease.OpenAsync(oversized,
            new ResourceIdentity(oversized.IndexContentId, row.Ordinal), budget));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MalformedOuterResultRemainsInspectableAndDisposalReleasesBudget()
    {
        using var fixture = CreateFixture([0, 1, 2, 3]);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);

        Assert.Null(lease.Bundle.Value);
        Assert.NotEmpty(lease.Bundle.Diagnostics);
        Assert.Null(lease.SelectedChunk);
        Assert.Null(lease.LuaChunk);
        Assert.Throws<InvalidOperationException>(() => lease.SelectChunk(0));
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task HeaderOnlyLuaFailureKeepsOuterBundleDiagnosticsAndRawMetadata()
    {
        using var fixture = CreateFixture(Bundle(
            new Chunk(0x56, LuaHeader, []),
            new Chunk(0x56, LuaHeader, [new Metadata(0x57, [0xca, 0xfe])])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);
        var bundleBytes = lease.Bundle.Value!.RawBytes.ToArray();

        var inner = lease.SelectChunk(1);

        Assert.NotNull(lease.Bundle.Value);
        Assert.Contains("DuplicateScriptChunkKey: 1", lease.Bundle.Diagnostics);
        Assert.Equal(bundleBytes, lease.Bundle.Value!.RawBytes.ToArray());
        Assert.Equal(0x56UL, lease.SelectedChunk!.OpaqueKey);
        Assert.Equal(0x57UL, Assert.Single(lease.SelectedChunk.Metadata).OpaqueKey);
        Assert.Null(inner.Value);
        Assert.Contains(inner.Diagnostics, diagnostic => diagnostic.Contains("TruncatedData", StringComparison.Ordinal));

        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndStaleSourceFailureReleaseReadReservations()
    {
        using var fixture = CreateFixture(Bundle(new Chunk(0x55, LuaHeader, [])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var resource = Resource(snapshot);
        var budget = new AssetMemoryBudget();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ScriptInspectionLease.OpenAsync(snapshot, resource, budget, cancelled.Token));
        Assert.Equal(0, budget.UsedBytes);

        var stale = snapshot with { IndexStamp = snapshot.IndexStamp with { LastWriteUtcTicks = snapshot.IndexStamp.LastWriteUtcTicks + 1 } };
        var failure = await Assert.ThrowsAsync<ArchiveException>(() =>
            ScriptInspectionLease.OpenAsync(stale, new ResourceIdentity(stale.IndexContentId, resource.Ordinal), budget));
        Assert.Equal("SourceChanged", failure.Code);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task SourceStampChangeBeforeSelectionRejectsAndReleasesLuaReservation()
    {
        using var fixture = CreateFixture(Bundle(new Chunk(0x88, LuaChunk("stamp", [ReturnWord]), [])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);
        var bundleBudget = budget.UsedBytes;
        var volume = Assert.Single(snapshot.Volumes);
        File.SetLastWriteTimeUtc(volume.FilePath, DateTime.UtcNow.AddMinutes(5));

        var error = Assert.Throws<ArchiveException>(() => lease.SelectChunk(0));

        Assert.Equal("SourceChanged", error.Code);
        Assert.Null(lease.SelectedChunk);
        Assert.Null(lease.LuaChunk);
        Assert.Equal(bundleBudget, budget.UsedBytes);
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task SourceStampChangeBeforeMetadataSelectionRejectsAndReleasesReservation()
    {
        using var fixture = CreateFixture(Bundle(new Chunk(0x99, LuaHeader,
            [new Metadata(0xaa, BooleanPropertyValue(0x01020304, 0))])));
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget = new AssetMemoryBudget();
        var lease = await ScriptInspectionLease.OpenAsync(snapshot, Resource(snapshot), budget);
        var bundleBudget = budget.UsedBytes;
        var volume = Assert.Single(snapshot.Volumes);
        File.SetLastWriteTimeUtc(volume.FilePath, DateTime.UtcNow.AddMinutes(5));

        var error = Assert.Throws<ArchiveException>(() => lease.SelectMetadata(0, 0));

        Assert.Equal("SourceChanged", error.Code);
        Assert.Null(lease.SelectedChunk);
        Assert.Null(lease.SelectedMetadata);
        Assert.Null(lease.PropertyValues);
        Assert.Null(lease.LuaChunk);
        Assert.Equal(bundleBudget, budget.UsedBytes);
        lease.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    private static ResourceIdentity Resource(ArchiveSnapshot snapshot)
    {
        var row = Assert.Single(snapshot.Records, record => record.Type == ScriptResourceType);
        return new(snapshot.IndexContentId, row.Ordinal);
    }

    private static FormatsFixtureBuilder CreateFixture(byte[] payload)
    {
        var fixture = FormatsFixtureBuilder.CreateEmpty();
        fixture.AddResource(ScriptResourceType, SyntheticResourceId, payload);
        fixture.WriteIndex();
        return fixture;
    }

    private static byte[] Bundle(params Chunk[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("QZIP\0SBCHK___"u8);
        writer.Write(1U);
        writer.Write(0U);
        writer.Write((uint)chunks.Length);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Key);
            writer.Write((uint)chunk.Bytecode.Length);
            writer.Write(chunk.Bytecode);
            writer.Write((uint)chunk.Metadata.Length);
            foreach (var metadata in chunk.Metadata)
            {
                writer.Write(metadata.Key);
                writer.Write(19000U);
                writer.Write(1U);
                writer.Write("NOCHK___"u8);
                writer.Write(1U);
                writer.Write(metadata.Flag0);
                writer.Write(metadata.Flag1);
                writer.Write((uint)metadata.Payload.Length);
                writer.Write(metadata.Payload);
            }
        }
        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), (uint)(bytes.Length - 21));
        return bytes;
    }

    private static byte[] LuaChunk(string sourceName, IReadOnlyList<uint> code)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(LuaHeader);
        var source = Encoding.UTF8.GetBytes(sourceName);
        writer.Write((uint)(source.Length + 1));
        writer.Write(source);
        writer.Write((byte)0);
        writer.Write(0U); writer.Write(0U);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)2);
        writer.Write((uint)code.Count);
        foreach (var instruction in code) writer.Write(instruction);
        writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BooleanPropertyValue(uint propertyHash, byte value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x80);
        writer.Write(propertyHash);
        writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }

    private static uint ReturnWord => 30u | (1u << 23);

    private sealed record Chunk(ulong Key, byte[] Bytecode, Metadata[] Metadata);
    private sealed record Metadata(ulong Key, byte[] Payload, byte Flag0 = 0, byte Flag1 = 0);
}
