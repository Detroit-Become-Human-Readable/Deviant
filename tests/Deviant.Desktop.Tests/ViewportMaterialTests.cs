using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using HelixToolkit.SharpDX;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ViewportMaterialTests
{
    [Fact]
    public void MissingDecodedNormalKeepsNeutralGeometryMaterialAndNativeReference() => Run(() =>
    {
        var identity = new ResourceIdentity("index", 1);
        var reference = new ResourceReference(IdentifierNamespace.Native, "texture", identity);
        var binding = new MaterialTextureBinding(7, reference, MaterialTextureRole.Normal, 2,
            new Vector2(1, -1), Vector2.Zero, new(true, "shader", "Tangent-space normal"));
        var source = new RenderMaterial(new(identity, new("index", "revision"), "normal", [binding],
            [reference], ["Texture format unavailable"]), new Dictionary<int, RenderTexture>());
        var prepared = ViewportMaterials.Create(source, _ => throw new InvalidOperationException("An unavailable texture must not be requested."));
        Assert.False(prepared.NormalMapped);
        Assert.Null(prepared.Material.NormalMap);
        Assert.False(prepared.Material.RenderDiffuseMap);
        Assert.Equal(0, prepared.TextureCoordinateSet);
        Assert.Equal(Vector2.One, prepared.Material.UVTransform.Scaling);
        Assert.Same(reference, Assert.Single(source.Asset.References));
        Assert.Equal("Texture format unavailable", Assert.Single(source.Asset.Diagnostics));
    });

    [Fact]
    public void MaterialWithoutARecognizedNormalProfileUsesNeutralLighting() => Run(() =>
    {
        var identity = new ResourceIdentity("index", 1);
        var source = new RenderMaterial(new(identity, new("index", "revision"), "neutral", [], [], []),
            new Dictionary<int, RenderTexture>());
        var prepared = ViewportMaterials.Create(source, _ => throw new InvalidOperationException("No texture is required."));
        Assert.False(prepared.NormalMapped);
        Assert.Equal(1f, prepared.Material.DiffuseColor.Alpha);
        Assert.True(prepared.Material.DiffuseColor.Red > 0);
        Assert.True(prepared.Material.AmbientColor.Red > 0);
        Assert.False(prepared.Material.EnableAutoTangent);
    });

    [Fact]
    public void UnsupportedNormalUvDoesNotRequestPixelsOrRemoveSurfaceGraph() => Run(() =>
    {
        var identity = new ResourceIdentity("index", 1); var revision = new SourceRevision("index", "revision");
        var reference = new ResourceReference(IdentifierNamespace.Native, "texture", identity);
        var binding = new MaterialTextureBinding(7, reference, MaterialTextureRole.Normal, 1, Vector2.One,
            Vector2.Zero, new(true, "shader", "Tangent-space normal"));
        var graph = new MaterialSurfaceGraph(new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3,
            [], Constants: [.2f, .4f, .6f]), null, null, [], [], []);
        var source = new RenderMaterial(new(identity, revision, "surface-expression", [binding], [reference], [])
            { SurfaceGraph = graph }, new Dictionary<int, RenderTexture> { [7] = Texture(identity, revision) });
        var prepared = ViewportMaterials.Create(source, _ => throw new InvalidOperationException("Unsupported UV pixels must not be uploaded."),
            new HashSet<int> { 0 });
        Assert.False(prepared.NormalMapped); Assert.Null(prepared.Material.NormalMap);
        Assert.Single(prepared.Diagnostics); Assert.Contains("UV set 1", prepared.Diagnostics[0]);
        Assert.Same(graph, source.Asset.SurfaceGraph);
        Assert.True(MaterialGraphCompiler.Compile(graph).HasBaseColor);
    });

    [Fact]
    public void SupportedNormalUvKeepsItsExactAffineTransform() => Run(() =>
    {
        var identity = new ResourceIdentity("index", 1); var revision = new SourceRevision("index", "revision");
        var binding = new MaterialTextureBinding(7, new(IdentifierNamespace.Native, "texture", identity),
            MaterialTextureRole.Normal, 0, new(1, -1), new(.25f, .5f), new(true, "shader", "Tangent-space normal"));
        var texture = Texture(identity, revision);
        var model = new TextureModel(Guid.NewGuid(), new AssetViewportService.RawTextureLoader(texture.Surface.Rgba8.ToArray(), 1, 1, false));
        var source = new RenderMaterial(new(identity, revision, "normal", [binding], [], []), new Dictionary<int, RenderTexture> { [7] = texture });
        var prepared = ViewportMaterials.Create(source, _ => model, new HashSet<int> { 0 });
        Assert.True(prepared.NormalMapped); Assert.Same(model, prepared.Material.NormalMap);
        Assert.Equal(new Vector2(1, -1), prepared.Material.UVTransform.Scaling);
        Assert.Equal(new Vector2(.25f, .5f), prepared.Material.UVTransform.Translation);
        Assert.Empty(prepared.Diagnostics);
    });

    [Fact]
    public void MissingTangentFrameDisablesOnlyTheNormalBinding() => Run(() =>
    {
        var identity = new ResourceIdentity("index", 1); var revision = new SourceRevision("index", "revision");
        var binding = new MaterialTextureBinding(7, new(IdentifierNamespace.Native, "texture", identity),
            MaterialTextureRole.Normal, 0, Vector2.One, Vector2.Zero, new(true, "shader", "Tangent-space normal"));
        var source = new RenderMaterial(new(identity, revision, "normal", [binding], [], []),
            new Dictionary<int, RenderTexture> { [7] = Texture(identity, revision) });
        var prepared = ViewportMaterials.Create(source, _ => throw new InvalidOperationException("Missing tangent frame must not upload a normal map."),
            new HashSet<int> { 0 }, normalFrameAvailable: false);
        Assert.False(prepared.NormalMapped); Assert.Null(prepared.Material.NormalMap);
        Assert.Single(prepared.Diagnostics); Assert.Contains("tangent frame", prepared.Diagnostics[0]);
    });

    private static RenderTexture Texture(ResourceIdentity identity, SourceRevision revision)
    {
        byte[] bytes = new byte[152]; "DDS "u8.CopyTo(bytes);
        Write(4, 124); Write(8, 0x1007); Write(12, 1); Write(16, 1); Write(76, 32); Write(80, 4);
        "DX10"u8.CopyTo(bytes.AsSpan(84)); Write(108, 0x1000); Write(128, 28); Write(132, 3); Write(140, 1);
        bytes[148] = 128; bytes[149] = 128; bytes[150] = 255; bytes[151] = 255;
        var surface = new DdsTextureDecoder().DecodeMip(new DdsTextureReader().Read(bytes, identity, revision));
        return new(identity, surface, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(surface.Rgba8.Span)));
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
