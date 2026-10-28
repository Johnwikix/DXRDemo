cbuffer Display : register(b0) { float4 SizeExposure; float4 OutputParameters; }
Texture2D<float4> LinearColor : register(t0);
RWTexture2D<float4> Encoded : register(u0);

[numthreads(8, 8, 1)]
void Encode(uint3 tid : SV_DispatchThreadID)
{
    if (any(tid.xy >= uint2(SizeExposure.xy))) return;
    float3 color = max(LinearColor[tid.xy].rgb, 0) * exp2(SizeExposure.w);
    if (OutputParameters.x != 0)
    {
        if (SizeExposure.z != 0) color /= 1 + color / max(OutputParameters.z / OutputParameters.y, 1);
        color = float3(dot(color, float3(0.627404, 0.329283, 0.043313)),
                       dot(color, float3(0.069097, 0.919540, 0.011362)),
                       dot(color, float3(0.016391, 0.088013, 0.895595)));
        float3 y = pow(min(color * OutputParameters.y, OutputParameters.z) / 10000, 0.1593017578125);
        color = pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375);
    }
    else
    {
        if (SizeExposure.z != 0) color = saturate((color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14));
        color = pow(color, 1.0 / 2.2);
    }
    Encoded[tid.xy] = float4(color, 1);
}
