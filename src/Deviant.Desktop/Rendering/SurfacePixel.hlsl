// HIDDEN_GRID_HELPER
Texture2D<float3> texNormalMap : register(t1);
SamplerState samplerSurface : register(s0);
Texture2D<float> surfaceSceneDepth : register(t63);

float ReadSceneViewDepth(float2 pixel)
{
    float depth = surfaceSceneDepth.Load(int3(int2(pixel), 0));
    return mProjection._43 / (depth + mProjection._33);
}

float3 EncodeSrgb(float3 value)
{
    value = saturate(value);
    return float3(value.r <= 0.0031308 ? value.r * 12.92 : 1.055 * pow(value.r, 1.0 / 2.4) - 0.055,
        value.g <= 0.0031308 ? value.g * 12.92 : 1.055 * pow(value.g, 1.0 / 2.4) - 0.055,
        value.b <= 0.0031308 ? value.b * 12.92 : 1.055 * pow(value.b, 1.0 / 2.4) - 0.055);
}

float4 main(PSInput input) : SV_Target
{
    if (input.c2.a > 0.5) return DrawHiddenGrid(input);
    float3 normal = normalize(input.n);
    if (bHasNormalMap)
    {
        float2 normalUv = mul(float2x4(uvTransformR1, uvTransformR2), float4(input.t, 0, 1)).xy;
        float3 signedNormal = 2.0 * texNormalMap.Sample(samplerSurface, normalUv).rgb - 1.0;
        normal = normalize(normal * signedNormal.z + normalize(input.t1) * signedNormal.x
            + normalize(input.t2) * signedNormal.y);
    }
    // MATERIAL_BODY
    if (surfaceUnlit) return float4(EncodeSrgb(surfaceColor), surfaceOpacity);
    float3 color = vLightAmbient.rgb * surfaceColor + input.c2.rgb;
    float3 view = normalize(input.vEye.xyz);
    for (int i = 0; i < NumLights; ++i)
    {
        float3 direction;
        float attenuation = 1.0;
        if (Lights[i].iLightType == 1)
            direction = normalize(Lights[i].vLightDir.xyz);
        else
        {
            direction = Lights[i].vLightPos.xyz - input.wp.xyz;
            float distanceToLight = length(direction);
            if (distanceToLight > Lights[i].vLightAtt.w) continue;
            direction /= max(distanceToLight, 0.00001);
            attenuation = 1.0 / dot(Lights[i].vLightAtt.xyz,
                float3(1.0, distanceToLight, distanceToLight * distanceToLight));
        }
        float amount = saturate(dot(normal, direction));
        float specular = amount > 0 ? pow(saturate(dot(normal, normalize(view + direction))), sMaterialShininess) : 0;
        color += (surfaceColor * amount + vMaterialSpecular.rgb * specular) * Lights[i].vLightColor.rgb * attenuation;
    }
    return float4(EncodeSrgb(color), surfaceOpacity);
}
