using System.IO;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.Direct3D11;
using System.Numerics;

namespace Deviant.Desktop;

public sealed record ViewportMaterial(PhongMaterial Material, int TextureCoordinateSet, bool NormalMapped)
{
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public static class ViewportMaterials
{
    public static ViewportMaterial Create(RenderMaterial? source, Func<RenderTexture, TextureModel> textureProvider,
        IReadOnlySet<int>? availableNormalUvSets = null, bool normalFrameAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(textureProvider);
        var material = new PhongMaterial
        {
            DiffuseColor = new Color4(.62f, .65f, .68f, 1), AmbientColor = new Color4(.4f, .4f, .4f, 1),
            SpecularColor = new Color4(.08f, .08f, .08f, 1), SpecularShininess = 24,
            EnableAutoTangent = false, RenderDiffuseMap = false, RenderNormalMap = true
        };
        // Unavailable decoded maps keep their native references in the source material.
        var normal = source?.Asset.Bindings.SingleOrDefault(x => x.Role == MaterialTextureRole.Normal &&
            source.TexturesBySlot.ContainsKey(x.SlotOrdinal));
        if (normal is null) return new(material, 0, false);
        if (!normalFrameAvailable)
            return new(material, 0, false) { Diagnostics = ["Normal: The tangent frame is unavailable."] };
        if (availableNormalUvSets is not null && !availableNormalUvSets.Contains(normal.TextureCoordinateSet))
            return new(material, 0, false)
            { Diagnostics = [$"Normal: UV set {normal.TextureCoordinateSet} is unavailable."] };
        if (!normal.Evidence.Verified) throw new InvalidDataException("The material binding is unavailable.");
        material.NormalMap = textureProvider(source!.TexturesBySlot[normal.SlotOrdinal]);
        material.UVTransform = new UVTransform(0, normal.UvScale, normal.UvOffset);
        material.DiffuseMapSampler = AssetViewportService.SurfaceSampler(source.TexturesBySlot[normal.SlotOrdinal].Sampling);
        return new(material, normal.TextureCoordinateSet, true);
    }
}
