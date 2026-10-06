float4 DrawHiddenGrid(PSInput input)
{
    float3 axis = abs(input.n);
    float2 plane = axis.y >= axis.x && axis.y >= axis.z ? input.wp.xz
        : axis.z >= axis.x ? input.wp.xy : input.wp.yz;
    float2 coordinate = plane * 2.0 + frac(TimeStamp * float2(0.21, 0.13));
    float2 distanceToLine = abs(frac(coordinate + 0.5) - 0.5);
    float2 antialias = max(fwidth(coordinate), 0.001);
    float2 coverage = 1.0 - smoothstep(0.04 - antialias, 0.04 + antialias, distanceToLine);
    float gridCoverage = max(coverage.x, coverage.y);
    clip(gridCoverage - 0.2);
    float selected = saturate(dot(input.c2.rgb, float3(5.0, 5.0, 5.0)));
    float3 color = lerp(float3(0.96, 0.43, 0.055), float3(1.0, 0.76, 0.28), selected);
    return float4(color * lerp(0.65, 1.0, gridCoverage), 1.0);
}
