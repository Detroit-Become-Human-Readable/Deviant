using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.ShaderManager;
using HelixToolkit.SharpDX.Utilities;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.Direct3D11;

namespace Deviant.Desktop;

internal interface ISceneDepthMaterial
{
    bool RequiresSceneDepth { get; }
}

internal sealed class SurfaceInstancingModel(string technique) : InstancingMeshGeometryModel3D
{
    protected override SceneNode OnCreateSceneNode() => new SurfaceInstancingNode(technique);
    private sealed class SurfaceInstancingNode(string technique) : InstancingMeshNode
    {
        protected override RenderCore OnCreateRenderCore() => new SurfaceInstancingRenderCore { ParameterBuffer = instanceParamBuffer };
        protected override IRenderTechnique? OnCreateRenderTechnique(IEffectsManager effectsManager) => effectsManager[technique];
        protected override IAttachableBufferModel OnCreateBufferModel(Guid modelGuid, Geometry3D? geometry)
            => (geometry is ViewportMeshGeometry { Animated: true }
                ? EffectsManager?.GeometryBufferManager?.Register<ViewportAnimatedMeshBuffer>(modelGuid, geometry)
                : EffectsManager?.GeometryBufferManager?.Register<ViewportMeshBuffer>(modelGuid, geometry))
                ?? EmptyGeometryBufferModel.Empty;
    }

    private sealed class SurfaceInstancingRenderCore : InstancingMeshRenderCore
    {
        protected override void OnRenderDepth(RenderContext context, DeviceContextProxy deviceContext, ShaderPass? customPass)
        {
            if (customPass is { IsNULL: false }) { base.OnRenderDepth(context, deviceContext, customPass); return; }
            var previous = context.CustomPassName;
            try { context.CustomPassName = SurfacePhongMaterial.DepthPassMarker; base.OnRender(context, deviceContext); }
            finally { context.CustomPassName = previous; }
        }

        protected override void OnRender(RenderContext context, DeviceContextProxy deviceContext)
        {
            try { base.OnRender(context, deviceContext); }
            finally
            {
                if (MaterialVariables is ISceneDepthMaterial { RequiresSceneDepth: true })
                    MaterialVariables.GetPass(RenderType, context).PixelShader.BindTexture(deviceContext, 63, null);
            }
        }
    }
}

internal sealed class SurfacePhongMaterial : PhongMaterial
{
    internal const string DepthPassMarker = "SurfaceDepthPrepass";
    private readonly IReadOnlyList<TextureModel> textures;
    private readonly IReadOnlyList<SamplerStateDescription> samplers;
    private readonly IReadOnlyList<int> samplerSlots;
    private readonly bool requiresSceneDepth;
    public SurfacePhongMaterial(PhongMaterial template, IReadOnlyList<TextureModel> textures,
        IReadOnlyList<SamplerStateDescription>? samplers = null, IReadOnlyList<int>? samplerSlots = null,
        bool requiresSceneDepth = false)
    {
        this.textures = textures;
        this.samplers = samplers ?? Enumerable.Repeat(template.DiffuseMapSampler, textures.Count).ToArray();
        this.samplerSlots = samplerSlots ?? Enumerable.Repeat(10, textures.Count).ToArray();
        this.requiresSceneDepth = requiresSceneDepth;
        if (this.samplers.Count != textures.Count || this.samplerSlots.Count != textures.Count ||
            this.samplerSlots.Any(slot => slot is < 0 or > 15))
            throw new ArgumentException("The surface texture and sampler bindings do not match.");
        AmbientColor = template.AmbientColor; DiffuseColor = template.DiffuseColor;
        SpecularColor = template.SpecularColor; SpecularShininess = template.SpecularShininess;
        NormalMap = template.NormalMap; RenderNormalMap = template.RenderNormalMap;
        EnableAutoTangent = false; UVTransform = template.UVTransform;
        DiffuseMapSampler = template.DiffuseMapSampler;
    }

    protected override MaterialCore OnCreateCore() => new SurfaceMaterialCore(textures, samplers, samplerSlots, requiresSceneDepth)
    {
        AmbientColor = AmbientColor, DiffuseColor = DiffuseColor, SpecularColor = SpecularColor,
        SpecularShininess = (float)SpecularShininess, NormalMap = NormalMap, RenderNormalMap = RenderNormalMap,
        EnableAutoTangent = false, UVTransform = UVTransform, DiffuseMapSampler = DiffuseMapSampler,
        RenderDiffuseMap = false
    };

    private sealed class SurfaceMaterialCore(IReadOnlyList<TextureModel> textures,
        IReadOnlyList<SamplerStateDescription> samplers, IReadOnlyList<int> samplerSlots, bool requiresSceneDepth) : PhongMaterialCore
    {
        public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique)
            => new SurfaceMaterialVariables(manager, technique, this, textures, samplers, samplerSlots, requiresSceneDepth);
    }

    private sealed class SurfaceMaterialVariables : PhongMaterialVariables, ISceneDepthMaterial
    {
        private readonly List<ShaderResourceViewProxy> resources = [];
        private readonly List<SamplerStateProxy> samplers = [];
        private readonly IReadOnlyList<int> samplerSlots;
        private readonly IRenderTechnique technique;
        private readonly bool requiresSceneDepth;
        public bool RequiresSceneDepth => requiresSceneDepth;
        public SurfaceMaterialVariables(IEffectsManager manager, IRenderTechnique technique,
            PhongMaterialCore core, IReadOnlyList<TextureModel> textures,
            IReadOnlyList<SamplerStateDescription> descriptions, IReadOnlyList<int> samplerSlots, bool requiresSceneDepth) : base(manager, technique, core)
        {
            this.samplerSlots = samplerSlots;
            this.technique = technique;
            this.requiresSceneDepth = requiresSceneDepth;
            try
            {
                foreach (var texture in textures)
                    resources.Add(manager.MaterialTextureManager?.Register(texture)
                        ?? throw new InvalidOperationException("The material texture could not be uploaded."));
                foreach (var description in descriptions)
                    samplers.Add(manager.StateManager?.Register(description)
                        ?? throw new InvalidOperationException("The material sampler is unavailable."));
            }
            catch
            {
                foreach (var resource in resources) resource.Dispose();
                resources.Clear(); foreach (var sampler in samplers) sampler.Dispose(); samplers.Clear();
                throw;
            }
        }

        public override ShaderPass GetPass(RenderType renderType, RenderContext context)
            => context.CustomPassName == DepthPassMarker
                ? technique[DefaultPassNames.DepthPrepass] : base.GetPass(renderType, context);

        public override bool BindMaterialResources(RenderContext context, DeviceContextProxy deviceContext, ShaderPass shaderPass)
        {
            if (!base.BindMaterialResources(context, deviceContext, shaderPass)) return false;
            if (requiresSceneDepth)
            {
                var buffer = context.RenderHost.RenderBuffer;
                if (buffer is null || !buffer.HasMSAA || buffer.DepthStencilBufferNoMSAA?.TextureView is null)
                    throw new InvalidOperationException("The scene-depth buffer is unavailable.");
                shaderPass.PixelShader.BindTexture(deviceContext, 63, buffer.DepthStencilBufferNoMSAA);
            }
            for (int index = 0; index < resources.Count; index++)
                shaderPass.PixelShader.BindTexture(deviceContext, 64 + index, resources[index]);
            for (int index = 0; index < samplers.Count; index++)
                shaderPass.PixelShader.BindSampler(deviceContext, samplerSlots[index], samplers[index]);
            return true;
        }

        protected override void OnDispose(bool disposeManagedResources)
        {
            foreach (var resource in resources) resource.Dispose();
            resources.Clear(); foreach (var sampler in samplers) sampler.Dispose(); samplers.Clear();
            base.OnDispose(disposeManagedResources);
        }
    }
}
