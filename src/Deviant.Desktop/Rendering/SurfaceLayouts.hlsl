// HelixToolkit 3.1.2 shader buffer and vertex layouts, adapted for direct TBN lighting.
// Copyright (c) 2023 Helix Toolkit contributors. Licensed under the MIT License.
// https://github.com/helix-toolkit/helix-toolkit/blob/v3.1.2/LICENSE
#pragma pack_matrix(row_major)
#define LIGHTS 8
#define MESH

struct LightStruct
{
    int iLightType; //4
    float3 paddingL;
	// the light direction is here the vector which looks towards the light
    float4 vLightDir; //8
    float4 vLightPos; //12
    float4 vLightAtt; //16
    float4 vLightSpot; //(outer angle , inner angle, falloff, free), 20
    float4 vLightColor; //24
    matrix mLightView; //40
    matrix mLightProj; //56
};

struct PSInput
{
	float4 p : SV_POSITION;
    float4 vEye : POSITION0;
    float3 n : NORMAL; // normal
    float4 wp : POSITION1;
	float4 sp : TEXCOORD1;
	float2 t : TEXCOORD0; // tex coord
    float2 uv1 : TEXCOORD2;
    float2 uv2 : TEXCOORD3;
    float2 uv3 : TEXCOORD4;
	float3 t1 : TANGENT; // tangent
	float3 t2 : BINORMAL; // bi-tangent
	float4 c : COLOR; // solid color (for debug)
    float4 c2 : COLOR1; //vMaterialEmissive
    float4 cDiffuse : COLOR2; //vMaterialDiffuse
};

cbuffer cbTransforms : register(b0)
{
    float4x4 mView;
    float4x4 mProjection;
    float4x4 mViewProjection;
	// camera frustum:
	// [fov,asepct-ratio,near,far]
    float4 vFrustum;
	// viewport:
	// [w,h,1/w,1/h]
    float4 vViewport;
    // [x, y, w, h]
    float4 vResolution;
	// camera position
    float3 vEyePos;
    bool SSAOEnabled;
    float SSAOBias;
    float SSAOIntensity;
    float TimeStamp; // by seconds
    bool IsPerspective;
    float OITPower;
    float OITSlope;
    int OITWeightMode;
    float DpiScale;
};

cbuffer cbMesh : register(b1)
{
// Common Parameters
    float4x4 mWorld;
    bool bInvertNormal = false;
    bool bHasInstances = false;
    bool bHasInstanceParams = false;
    bool bHasBones = false;
    float4 vParams = float4(0, 0, 0, 0); //Shared with models
    float4 vColor = float4(1, 1, 1, 1); //Shared with models
    float4 wireframeColor;
    bool3 bParams; // Shared with models for enable/disable features
    bool bBatched = false;

// Material Parameters changable
	float minTessDistance = 1;
	float maxTessDistance = 100;
	float minTessFactor = 4;
	float maxTessFactor = 1;

    float4 vMaterialDiffuse = 0.5f; //Kd := surface material's diffuse coefficient
    float4 vMaterialAmbient = 0.25f; //Ka := surface material's ambient coefficient.
    float4 vMaterialEmissive = 0.0f; //Ke := surface material's emissive coefficient
#if !defined(PBR)
    float4 vMaterialSpecular = 0.0f; //Ks := surface material's specular coefficient. If using PBR, vMaterialReflect = float4(ConstantAO, ConstantRoughness, ConstantMetallic, ConstantReflectance);
    float4 vMaterialReflect = 0.0f; //Kr := surface material's reflectivity coefficient. If using PBR, vMaterialSpecular = float4(ClearCoat, ClearCoatRoughness, 0, 0)
#endif
#if defined(PBR)
    float ConstantAO;
    float ConstantRoughness;
    float ConstantMetallic;
    float ConstantReflectance;
    float ClearCoat;
    float ClearCoatRoughness;
    float padding1;
    bool bHasAOMap;
#endif
    bool bHasDiffuseMap = false;
    bool bHasNormalMap = false;
    bool bHasCubeMap = false;
    bool bRenderShadowMap = false;
    bool bHasEmissiveMap = false;
#if !defined(PBR)
    bool bHasAlphaMap = false; // If using PBR, this is used as HasRMAMap.
    bool bHasSpecularMap;
#endif
#if defined(PBR)
    bool bHasRMMap;
    bool bHasIrradianceMap;
#endif
    bool bAutoTengent;
    bool bHasDisplacementMap = false;
    bool bRenderPBR = false;
    bool bRenderFlat = false; //Enable flat normal rendering
    float sMaterialShininess = 1.0f; //Ps := surface material's shininess

    float4 displacementMapScaleMask = float4(0, 0, 0, 1);
    float4 uvTransformR1;
    float4 uvTransformR2;
    float vertColorBlending;
    float3 padding4;
};

cbuffer cbLights : register(b3)
{
    LightStruct Lights[LIGHTS];
    float4 vLightAmbient = float4(0.2f, 0.2f, 0.2f, 1.0f);
    int NumLights;
    bool bHasEnvironmentMap;
    int NumEnvironmentMapMipLevels;
    float padding;
};
