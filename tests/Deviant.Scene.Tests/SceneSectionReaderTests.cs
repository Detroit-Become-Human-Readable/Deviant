using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class SceneSectionReaderTests
{
    [Fact]
    public void ReadsDynamicCommonReferencesAndRetainsUnknownObjects()
    {
        var frames = Frames();
        var instance = frames[2];
        Assert.True(new NativeCommonPrefixReader().TryRead(instance, out var prefix, out _));
        Assert.Equal(41, prefix!.NextClassOffset);
        var reference = Assert.Single(prefix.References);
        Assert.Equal((77u, 88u, (byte)9), (reference.Type, reference.Id, reference.Trailer));
        var section = Assert.IsType<SceneSectionDocument>(Read(frames).Value);
        Assert.Equal("generated-instance", Assert.Single(section.CatalogInstances).InstanceName);
        Assert.Equal("generated-catalog", section.CatalogInstances[0].CatalogName);
        Assert.Equal(5, section.Objects.Count);
        Assert.Equal(new byte[] { 5, 6, 7 }, section.Objects[^1].RawPayload.ToArray());
        Assert.Single(section.Hierarchy.Roots);
        Assert.Single(section.Entities);
        Assert.Empty(section.Diagnostics);
        Assert.Equal("scene-section", section.Identity.MemberPath);
        Assert.Equal(SceneFixture.Container, section.SourceContainer);
    }

    [Fact]
    public void ObjectCollectionsAndUnplacedInstancesDoNotPassSectionAdmission()
    {
        var frames = Frames();
        Assert.Contains("SceneEnvironmentMissing", Read(frames.Where(f => f.NativeType != 2202).ToArray()).Diagnostics);
        Assert.Contains("SceneEnvironmentAmbiguous", Read([..frames, Make(5, 2202, 91, Environment())]).Diagnostics);
        Assert.Contains("SceneCatalogInstancesUnavailable", Read(frames.Where(f => f.NativeType != 2207).ToArray()).Diagnostics);
        var bytes = frames[2].RawPayload.ToArray();
        Put(bytes, 57, 999);
        frames[2] = frames[2] with { RawPayload = bytes };
        Assert.Contains("SceneInstancePlacementUnavailable", Read(frames).Diagnostics);
    }

    [Fact]
    public void PrefixRejectsTruncationHugeCountsAndWrongProvenance()
    {
        var frame = Frames()[2];
        var reader = new NativeCommonPrefixReader();
        var bytes = frame.RawPayload.ToArray(); Put(bytes, 16, uint.MaxValue);
        Assert.False(reader.TryRead(frame with { RawPayload = bytes }, out _, out var limit));
        Assert.Equal("NativeCommonReferenceLimit", limit);
        Assert.False(reader.TryRead(frame with { RawPayload = frame.RawPayload[..30],
            PayloadExtent = frame.PayloadExtent with { Length = 30 } }, out _, out _));
        Assert.False(reader.TryRead(frame with { PayloadExtent = frame.PayloadExtent with
            { Container = new("another-index", 7) } }, out _, out var provenance));
        Assert.Equal("NativePrefixSourceMismatch", provenance);
    }

    [Fact]
    public void CatalogNamesAndExactEndAreBounded()
    {
        var frame = Frames()[2];
        Assert.True(new SceneEntityReader().TryRead(frame, out var entity, out _));
        var reader = new SceneCatalogInstanceReader();
        var bytes = frame.RawPayload.ToArray(); Put(bytes, entity!.NextClassOffset + 12, uint.MaxValue);
        Assert.False(reader.TryRead(frame with { RawPayload = bytes }, entity, out _, out var name));
        Assert.Equal("CatalogInstanceNameInvalid", name);
        var extended = frame.RawPayload.ToArray().Append((byte)0).ToArray();
        Assert.False(reader.TryRead(frame with { RawPayload = extended,
            PayloadExtent = frame.PayloadExtent with { Length = extended.Length } }, entity, out _, out var boundary));
        Assert.Equal("CatalogInstanceBoundaryMismatch", boundary);
    }

    [Fact]
    public void EnvironmentRetainsTypedReferencesAndValidatesProfileAndExtent()
    {
        var frame = Frames()[3]; var bytes = frame.RawPayload.ToArray();
        Put(bytes, 151, 700); Put(bytes, 155, 33);
        var reader = new SceneEnvironmentReader();
        Assert.True(reader.TryRead(frame with { RawPayload = bytes }, out var environment, out _));
        Assert.Equal(700u, Assert.Single(environment!.References).ResourceType);
        Assert.Equal("33", environment.References[0].Id);
        Assert.False(reader.TryRead(frame with { PayloadExtent = frame.PayloadExtent with { Length = 1 } }, out _, out var source));
        Assert.Equal("SceneEnvironmentSourceMismatch", source);
        Put(bytes, 12, 18);
        Assert.False(reader.TryRead(frame with { RawPayload = bytes }, out _, out var version));
        Assert.Equal("SceneEnvironmentProfileUnsupported", version);
    }

    [Fact]
    public void RetainsMalformedKnownEntitiesWithTheirDiagnosticIdentity()
    {
        var bytes = Instance(); Put(bytes, 49, 99);
        var unsupported = Make(5, 2207, 13, bytes);
        var section = Assert.IsType<SceneSectionDocument>(Read([.. Frames(), unsupported]).Value);
        Assert.Contains(section.Diagnostics, item => item.Identity == unsupported.Identity && item.Code == "EntityVersionUnsupported");
        Assert.Contains(section.Objects, frame => frame.Identity == unsupported.Identity);
    }

    [Fact]
    public void CancellationPropagatesAcrossSectionParsing()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SceneSectionReader().Read(Input(Frames()), cancel.Token));
    }

    private static DecodeResult<SceneSectionDocument> Read(NativeObjectFrame[] frames) => new SceneSectionReader().Read(Input(frames));
    private static DecodeResult<NativeObjectDirectoryDocument> Input(NativeObjectFrame[] frames) =>
        new(SceneFixture.Directory(frames), SceneFixture.Container, SceneFixture.Revision, SceneFixture.Physical, [], [], [], new Dictionary<string, string>());
    private static NativeObjectFrame[] Frames() =>
    [
        SceneFixture.Frame(0, 11, Matrix4x4.CreateTranslation(1, 2, 3)),
        Make(1, 3, 10, Common(2, [])),
        Make(2, 2207, 12, Instance()),
        Make(3, 2202, 90, Environment()),
        Make(4, 9999, 91, [5, 6, 7], 0)
    ];
    private static byte[] Instance()
    {
        using var stream = new MemoryStream(); stream.Write(Common(4, [(77u, 88u, (byte)9)]));
        stream.Write("ENTITY  "u8); Word(stream, 7);
        var body = new byte[37]; Put(body, 0, 2002); Put(body, 4, 11); stream.Write(body);
        stream.Write("ENTCATLG"u8); Word(stream, 2);
        foreach (var name in new[] { "generated-instance", "generated-catalog" })
        { var text = Encoding.ASCII.GetBytes(name); Word(stream, (uint)text.Length); stream.Write(text); }
        Word(stream, 3); Word(stream, 10); return stream.ToArray();
    }
    private static byte[] Common(uint classes, (uint Type, uint Id, byte Trailer)[] references)
    {
        using var stream = new MemoryStream(); Word(stream, classes); stream.Write("COM_CONT"u8); Word(stream, 6);
        Word(stream, (uint)references.Length);
        foreach (var item in references) { Word(stream, item.Type); Word(stream, item.Id); stream.WriteByte(item.Trailer); }
        stream.Write("LOADCONT"u8); Word(stream, 2); return stream.ToArray();
    }
    private static byte[] Environment()
    { var bytes = new byte[167]; Put(bytes, 0, 1); "SCN_GLBL"u8.CopyTo(bytes.AsSpan(4)); Put(bytes, 12, 17); return bytes; }
    private static NativeObjectFrame Make(int ordinal, uint type, uint id, byte[] bytes, uint? classes = null)
    {
        var frame = SceneFixture.Frame(ordinal, id, Matrix4x4.Identity);
        return frame with { Identity = frame.Identity with { MemberPath = $"dc-object/{ordinal}/{type}/{id}" },
            NativeType = type, RawPayload = bytes, PayloadExtent = frame.PayloadExtent with { Length = bytes.Length },
            ClassCount = classes ?? BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
    }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Word(Stream stream, uint value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
}
