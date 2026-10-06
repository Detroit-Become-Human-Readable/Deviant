using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.ShaderManager;
using HelixToolkit.SharpDX.Utilities;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;

namespace Deviant.Desktop;

internal sealed class SurfaceInstancingModel(string technique) : InstancingMeshGeometryModel3D
{
    protected override SceneNode OnCreateSceneNode() => new SurfaceInstancingNode(technique);
    private sealed class SurfaceInstancingNode(string technique) : InstancingMeshNode
    {
        protected override IRenderTechnique? OnCreateRenderTechnique(IEffectsManager effectsManager) => effectsManager[technique];
        protected override IAttachableBufferModel OnCreateBufferModel(Guid modelGuid, Geometry3D? geometry)
            => EffectsManager?.GeometryBufferManager?.Register<ViewportMeshBuffer>(modelGuid, geometry)
                ?? EmptyGeometryBufferModel.Empty;
    }
}

internal sealed class SurfacePhongMaterial : PhongMaterial
{
    private readonly IReadOnlyList<TextureModel> textures;
    public SurfacePhongMaterial(PhongMaterial template, IReadOnlyList<TextureModel> textures)
    {
        this.textures = textures;
        AmbientColor = template.AmbientColor; DiffuseColor = template.DiffuseColor;
        SpecularColor = template.SpecularColor; SpecularShininess = template.SpecularShininess;
        NormalMap = template.NormalMap; RenderNormalMap = template.RenderNormalMap;
        EnableAutoTangent = false; UVTransform = template.UVTransform;
        DiffuseMapSampler = template.DiffuseMapSampler;
    }

    protected override MaterialCore OnCreateCore() => new SurfaceMaterialCore(textures)
    {
        AmbientColor = AmbientColor, DiffuseColor = DiffuseColor, SpecularColor = SpecularColor,
        SpecularShininess = (float)SpecularShininess, NormalMap = NormalMap, RenderNormalMap = RenderNormalMap,
        EnableAutoTangent = false, UVTransform = UVTransform, DiffuseMapSampler = DiffuseMapSampler,
        RenderDiffuseMap = false
    };

    private sealed class SurfaceMaterialCore(IReadOnlyList<TextureModel> textures) : PhongMaterialCore
    {
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique)
            => new SurfaceMaterialVariables(manager, technique, this, textures);
    }

    private sealed class SurfaceMaterialVariables : PhongMaterialVariables
    {
        private readonly List<ShaderResourceViewProxy> resources = [];
        private SamplerStateProxy? sampler;
        public SurfaceMaterialVariables(IEffectsManager manager, IRenderTechnique technique,
            PhongMaterialCore core, IReadOnlyList<TextureModel> textures) : base(manager, technique, core)
        {
            try
            {
                foreach (var texture in textures)
                    resources.Add(manager.MaterialTextureManager?.Register(texture)
                        ?? throw new InvalidOperationException("The material texture could not be uploaded."));
                sampler = manager.StateManager?.Register(core.DiffuseMapSampler)
                    ?? throw new InvalidOperationException("The material sampler is unavailable.");
            }
            catch
            {
                foreach (var resource in resources) resource.Dispose();
                resources.Clear(); sampler?.Dispose(); sampler = null;
                throw;
            }
        }

        public override bool BindMaterialResources(RenderContext context, DeviceContextProxy deviceContext, ShaderPass shaderPass)
        {
            if (!base.BindMaterialResources(context, deviceContext, shaderPass)) return false;
            for (int index = 0; index < resources.Count; index++)
                shaderPass.PixelShader.BindTexture(deviceContext, 64 + index, resources[index]);
            shaderPass.PixelShader.BindSampler(deviceContext, 10, sampler);
            return true;
        }

        protected override void OnDispose(bool disposeManagedResources)
        {
            foreach (var resource in resources) resource.Dispose();
            resources.Clear(); sampler?.Dispose(); sampler = null;
            base.OnDispose(disposeManagedResources);
        }
    }
}
