struct SurfaceVertexInput
{
    float4 p : POSITION;
    float3 n : NORMAL;
    float3 t1 : TANGENT;
    float3 t2 : BINORMAL;
    float4 c : COLOR;
    float2 t : TEXCOORD0;
    float2 uv1 : TEXCOORD6;
    float2 uv2 : TEXCOORD7;
    float2 uv3 : TEXCOORD8;
    float4 mr0 : TEXCOORD1;
    float4 mr1 : TEXCOORD2;
    float4 mr2 : TEXCOORD3;
    float4 mr3 : TEXCOORD4;
    float2 tOffset : TEXCOORD5;
    float4 diffuseC : COLOR1;
    float4 emissiveC : COLOR2;
};

PSInput main(SurfaceVertexInput input)
{
    PSInput output = (PSInput)0;
    output.wp = mul(input.p, mWorld);
    output.n = normalize(mul(input.n, (float3x3)mWorld));
    output.t1 = mul(input.t1, (float3x3)mWorld);
    output.t2 = mul(input.t2, (float3x3)mWorld);
    if (bHasInstances)
    {
        matrix occurrence = { input.mr0, input.mr1, input.mr2, input.mr3 };
        output.wp = mul(output.wp, occurrence);
        output.n = normalize(mul(output.n, (float3x3)occurrence));
        output.t1 = mul(output.t1, (float3x3)occurrence);
        output.t2 = mul(output.t2, (float3x3)occurrence);
    }
    output.vEye = float4(normalize(vEyePos - output.wp.xyz), length(vEyePos - output.wp.xyz));
    output.p = mul(output.wp, mViewProjection);
    output.sp = mul(output.wp, mView);
    output.t = input.t;
    output.uv1 = input.uv1;
    output.uv2 = input.uv2;
    output.uv3 = input.uv3;
    output.c = input.c;
    output.cDiffuse = bHasInstanceParams ? input.diffuseC : vMaterialDiffuse;
    output.c2 = bHasInstanceParams ? input.emissiveC : vMaterialEmissive;
    return output;
}
