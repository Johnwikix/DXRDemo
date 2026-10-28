cbuffer Reconstruction : register(b0) { float4 Size; float4 Jitter; float4 Projection; }
Texture2D<float4> Color : register(t0);
Texture2D<float> Depth : register(t1);
Texture2D<float2> Motion : register(t2);
Texture2D<float> Reactive : register(t3);
Texture2D<float4> History : register(t4);
Texture2D<float> HistoryDepth : register(t5);
RWTexture2D<float4> Output : register(u0);
RWTexture2D<float4> NextHistory : register(u1);
RWTexture2D<float> NextDepth : register(u2);

[numthreads(8, 8, 1)]
void SdkDepth(uint3 tid : SV_DispatchThreadID)
{
    if (any(tid.xy >= uint2(Size.xy))) return;
    NextDepth[tid.xy] = 1 - Depth[tid.xy];
}
float ViewZ(float d) { return d == 0 ? 0 : Projection.x * Projection.y / (Projection.x + d * (Projection.y - Projection.x)); }
[numthreads(8, 8, 1)]
void NativeTaa(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float3 current = max(Color[p].rgb, 0), lo = current, hi = current;
    [unroll] for (int y = -1; y <= 1; y++) [unroll] for (int x = -1; x <= 1; x++) {
        float3 tap = Color[clamp(p + int2(x, y), 0, int2(Size.xy) - 1)].rgb;
        lo = min(lo, tap); hi = max(hi, tap);
    }
    float2 previous = p + Motion[p] + Jitter.xy - Jitter.zw;
    float z = ViewZ(Depth[p]); float3 result = current;
    if (Size.z == 0 && all(previous >= 0) && all(previous < Size.xy - 1)) {
        int2 q = (int2)floor(previous); float2 f = frac(previous);
        float oldZ = HistoryDepth[q];
        if ((z == 0 && oldZ == 0) || (z > 0 && oldZ > 0 && abs(z - oldZ) < max(.01, z * .02))) {
            float3 old = lerp(lerp(History[q].rgb, History[q + int2(1,0)].rgb, f.x),
                              lerp(History[q + int2(0,1)].rgb, History[q + 1].rgb, f.x), f.y);
            result = lerp(current, clamp(old, lo, hi), .9 * (1 - saturate(Reactive[p])));
        }
    }
    Output[p] = NextHistory[p] = float4(min(result, 65000), 1); NextDepth[p] = z;
}
