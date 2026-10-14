using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class RenderTextureRowTests
{
    [Fact]
    public void GraphSlotsNavigateTheirDecodedTexturesWithoutInventingBindings()
    {
        var identity = new ResourceIdentity("generated-index", 1);
        var normal = new MaterialTextureBinding(0, new(IdentifierNamespace.Native, "normal", identity), MaterialTextureRole.Normal, 0,
            new(1, -1), Vector2.Zero, new(true, "generated", "Normal"));
        var asset = new MaterialAsset(identity, new("generated-index", "revision"), "generated", [normal], [], [])
        {
            SurfaceGraph = new(Colour(Sample(1)), Channel(Sample(2), "a"), Channel(Sample(3), "r"), [MaterialInput.Uv0], [1, 2, 3], [])
        };
        var textures = new Dictionary<int, RenderTexture>();
        foreach (var slot in new[] { 0, 1, 2, 3, 8 })
            textures.Add(slot, new(new("generated-index", 2, "texture/" + slot), Surface(), "generated-pixels", new("generated-index", 10 + slot), new("generated-index", 20)));
        var rows = SceneView.BuildTextureRows([new(asset, textures)]);
        Assert.Equal(new[] { 0, 1, 2, 3, 8 }, rows.Select(row => row.SlotOrdinal));
        Assert.Equal(new[] { "Normal", "Colour", "Opacity", "Opacity", "Texture" }, rows.Select(row => row.Label.Split(" · ")[0]));
        Assert.Same(normal, rows[0].Binding);
        Assert.All(rows.Skip(1), row => Assert.Null(row.Binding));
        Assert.All(rows, row =>
        {
            Assert.Same(textures[row.SlotOrdinal], row.Texture);
            Assert.Equal(identity, row.Material);
            Assert.Equal(10 + row.SlotOrdinal, row.Texture!.SourceIdentity!.Ordinal);
            Assert.Equal(20, row.Texture!.CatalogIdentity!.Ordinal);
        });
    }

    [Fact]
    public void OneDecodedSlotUsedByColourAndOpacityProducesOneRow()
    {
        var identity = new ResourceIdentity("generated-index", 1);
        var sample = Sample(4);
        var asset = new MaterialAsset(identity, new("generated-index", "revision"), "generated", [], [], [])
        { SurfaceGraph = new(Colour(sample), Channel(sample, "a"), null, [MaterialInput.Uv0], [4], []) };
        var texture = new RenderTexture(new("generated-index", 2), Surface(), "generated-pixels");
        var row = Assert.Single(SceneView.BuildTextureRows([new(asset, new Dictionary<int, RenderTexture> { [4] = texture })]));
        Assert.StartsWith("Colour / Opacity", row.Label);
        Assert.Equal(4, row.SlotOrdinal);
        Assert.Null(row.Binding);
        Assert.Same(texture, row.Texture);
    }

    [Fact]
    public void MissingDecodedGraphSlotRemainsVisibleWithItsFailure()
    {
        var identity = new ResourceIdentity("generated-index", 1);
        var asset = new MaterialAsset(identity, new("generated-index", "revision"), "generated", [], [], ["Texture source unavailable"])
        { SurfaceGraph = new(Colour(Sample(2)), null, null, [MaterialInput.Uv0], [2], []) };
        var row = Assert.Single(SceneView.BuildTextureRows([new(asset, new Dictionary<int, RenderTexture>())]));
        Assert.Equal(2, row.SlotOrdinal); Assert.Null(row.Texture);
        Assert.Contains("Unavailable", row.Label); Assert.Contains("Texture source unavailable", row.Details);
        Assert.Equal(2, Assert.Single(asset.SurfaceGraph.TextureSlots));
        Assert.Equal("Texture source unavailable", Assert.Single(asset.Diagnostics));
    }

    private static MaterialExpression Sample(int slot) => new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
        [new(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0)], TextureSlot: slot);
    private static MaterialExpression Colour(MaterialExpression sample) => new(MaterialExpressionOperation.Swizzle, MaterialValueType.Vector3, [sample], Components: "xyz");
    private static MaterialExpression Channel(MaterialExpression sample, string component) => new(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [sample], Components: component);
    private static TextureSurface Surface()
    {
        var bytes = new byte[152]; "DDS "u8.CopyTo(bytes);
        Put(4,124); Put(8,0x1007); Put(12,1); Put(16,1); Put(76,32); Put(80,4);
        "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108,0x1000); Put(128,28); Put(132,3); Put(140,1);
        new byte[] { 10,20,30,255 }.CopyTo(bytes,148);
        return new DdsTextureDecoder().DecodeMip(new DdsTextureReader().Read(bytes));
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4), value);
    }
}
