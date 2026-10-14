using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NativeNodeArrayBodyBoundaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsForgedCachedPrefixProperties(int field)
    {
        var prefix = Prefix(); var owner = prefix.Value!; var node = owner.InheritedNode;
        var forged = field switch
        {
            0 => node with { Flags = node.Flags ^ 1 },
            1 => node with { Pivot = new Vector3(99, 98, 97) },
            2 => node with { StoredLocalTransform = node.StoredLocalTransform with { Elements = new float[16] } },
            _ => node with { Parent = new(IdentifierNamespace.Native, "31", owner.Identity, 2002) }
        };
        var result = new NativeNodeArrayBodyReader().Read(prefix with { Value = owner with { InheritedNode = forged } });
        Assert.Null(result.Value);
        Assert.Contains("NodeArrayBodyPrefixMismatch", result.Diagnostics);
        Assert.Equal(prefix.Identity, result.Identity);
    }

    [Fact]
    public void CompactRecordMetadataIsReservedBeforeCreatingEntries()
    {
        var result = new NativeNodeArrayBodyReader().Read(Prefix(1000),
            new(MaximumMetadataBytes: 70 * 1024, MaximumDiagnostics: 1));
        Assert.Null(result.Value);
        Assert.Contains("NodeArrayBodyMemoryLimit", result.Diagnostics);
    }

    [Fact]
    public void ReportedAccountingIncludesFinalOutputReservation()
    {
        var prefix = Prefix(); var reader = new NativeNodeArrayBodyReader();
        var value = Assert.IsType<NativeNodeArrayBodyDocument>(reader.Read(prefix, new(MaximumDiagnostics: 1)).Value);
        Assert.NotNull(reader.Read(prefix, new(MaximumMetadataBytes: value.AccountedMetadataBytes,
            MaximumDiagnostics: 1)).Value);
    }

    [Fact]
    public void FixedValidationScratchIsReservedBeforePrefixCallbacks()
    {
        var prefix = Prefix(); var owner = prefix.Value!;
        var elements = new CallbackList<float>(16, _ => throw new InvalidOperationException(),
            () => throw new InvalidOperationException("Budget must reject before prefix access."));
        prefix = prefix with { Value = owner with { InheritedNode = owner.InheritedNode with
        { StoredLocalTransform = owner.InheritedNode.StoredLocalTransform with { Elements = elements } } } };
        var result = new NativeNodeArrayBodyReader().Read(prefix, new(MaximumMetadataBytes: 100));
        Assert.Null(result.Value);
        Assert.Contains("NodeArrayBodyMemoryLimit", result.Diagnostics);
    }

    [Fact]
    public void CancellationDuringCachedTransformComparisonIsPropagated()
    {
        var prefix = Prefix(); var owner = prefix.Value!;
        using var stop = new CancellationTokenSource();
        var elements = new CallbackList<float>(16, _ => { stop.Cancel(); return 99; });
        prefix = prefix with { Value = owner with { InheritedNode = owner.InheritedNode with
        { StoredLocalTransform = owner.InheritedNode.StoredLocalTransform with { Elements = elements } } } };
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayBodyReader().Read(prefix, token: stop.Token));
        Assert.Equal(1, elements.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMalformedOptionalProvenanceHashes(bool step)
    {
        var prefix = Prefix();
        prefix = step ? prefix with { Steps = [new("generated", "1", "", new string('z', 64))] }
            : prefix with { Extents = [new("sample.dat", 0, 1, new string('z', 64))] };
        var result = new NativeNodeArrayBodyReader().Read(prefix);
        Assert.Null(result.Value);
        Assert.Contains(step ? "NodeArrayBodyStepInvalid" : "NodeArrayBodyPhysicalExtentInvalid", result.Diagnostics);
    }

    [Fact]
    public void RejectsOversizedPrecedingStepText()
    {
        var result = new NativeNodeArrayBodyReader().Read(Prefix() with
        { Steps = [new("generated", "1", new string('x', 4097))] });
        Assert.Null(result.Value);
        Assert.Contains("NodeArrayBodyStepInvalid", result.Diagnostics);
    }

    [Fact]
    public void MetadataJsonOmitsPayloadGraphsAndNonFiniteDisplayViews()
    {
        var value = Assert.IsType<NativeNodeArrayBodyDocument>(new NativeNodeArrayBodyReader().Read(
            Prefix(nonfinite: true), new(MaximumDiagnostics: 2)).Value);
        Assert.Equal(0x7fc01234u, value.Nodes[0].Components.GetBits(0));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.False(json.RootElement.TryGetProperty("Owner", out _));
        Assert.False(json.RootElement.TryGetProperty("RawPayload", out _));
        var components = json.RootElement.GetProperty("Nodes")[0].GetProperty("Components");
        Assert.False(components.TryGetProperty("RawBytes", out _));
        Assert.False(components.TryGetProperty("NativeFirstBlock", out _));
        Assert.False(components.TryGetProperty("VectorAt16", out _));
        Assert.False(components.TryGetProperty("VectorAt32", out _));
        Assert.False(json.RootElement.GetProperty("OptionalObject").TryGetProperty("RawBytes", out _));
        Assert.All(json.RootElement.GetProperty("RemainingTables").EnumerateArray(),
            table => Assert.False(table.TryGetProperty("RawBytes", out _)));
        Assert.Equal(value.Identity.MemberPath, json.RootElement.GetProperty("Identity").GetProperty("MemberPath").GetString());
        Assert.Equal(0x7fc01234u, value.Nodes[0].Components.GetBits(0));
    }

    private static DecodeResult<NativeNodeArrayDocument> Prefix(int compactCount = 0, bool nonfinite = false)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var node = SceneFixture.Frame(0, 7, Matrix4x4.Identity).RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(node, 2);
        writer.Write(node); writer.Write("NODEAR  "u8); writer.Write(4u); writer.Write(14u);
        writer.Write(nonfinite ? 1u : 0u); writer.Write(0u);
        if (nonfinite)
        {
            writer.Write(0u); writer.Write(0x7fc01234u);
            for (var i = 1; i < 10; i++) writer.Write((float)i);
            writer.Write(0u); writer.Write(uint.MaxValue); writer.Write(0u); writer.Write(0u);
            writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u);
        }
        writer.Write(0u); writer.Write(compactCount);
        for (var i = 0; i < compactCount; i++) { writer.Write((uint)i); writer.Write((ushort)0); writer.Write((ushort)0); }
        writer.Write(1u); writer.Write((byte)0); writer.Write(0u); writer.Write(2u); writer.Write(3u); writer.Write(0u);
        var bytes = stream.ToArray(); var container = SceneFixture.Container;
        var frame = new NativeObjectFrame(new(container.IndexContentId, container.Ordinal, "dc-object/0/2138/7"),
            0, 2138, 7, new(container, 24, 8), new(container, 512, 13), new(container, 525, bytes.Length),
            new byte[13], bytes, 0, 0, 0, 2);
        return new NativeNodeArrayReader().Read(frame, SceneFixture.Revision, SceneFixture.Physical);
    }

    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? readCount = null) : IReadOnlyList<T>
    {
        public int Count => readCount?.Invoke() ?? count;
        public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Indexed comparison is required.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
