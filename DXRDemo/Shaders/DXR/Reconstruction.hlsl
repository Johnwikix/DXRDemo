// Linear Rec.709 SR path. The SDK consumes projection jitter; ray sampling uses its negative.
#include "NRD/NRD.hlsli"
cbuffer FrameConstants : register(b0)
{
    float4 CameraOrigin;
    float4 CameraForward;
    float4 CameraRight;
    float4 CameraUp;
    float4 PreviousOrigin;
    float4 PreviousForward;
    float4 PreviousRight;
    float4 PreviousUp;
    float4 Size;             // input width, height, output width, height
    float4 Jitter;           // current and previous ray sample offsets, in input pixels
    float4 Control;          // history reset, denoiser mode, history length, unused
    float4 OutputParameters;// HDR, SDR white nits, peak nits, unused
    float4 Projection;      // tan(vertical FOV / 2), near, far, PBR scene
    float4 Lighting;        // separate primary direct light in the packaged mesh path
};

Texture2D<float4> Raw : register(t0);
Texture2D<float4> Normals : register(t1);
Texture2D<float4> Surfaces : register(t2); // world position + ray distance; sky direction + -1
Texture2D<float4> History : register(t3);  // linear radiance + previous view depth (-1 for sky)
Texture2D<float4> Reconstructed : register(t5);
Texture2D<float4> PbrDiffuse : register(t6);
Texture2D<float4> PbrSpecular : register(t7);
Texture2D<float4> PbrAlbedo : register(t8);
Texture2D<float4> PbrUnfiltered : register(t9); // Primary analytic direct lighting + emission; preserve hard shadows.
Texture2D<float4> SpecularReconstructed : register(t10);
Texture2D<float4> PbrSpecularGuide : register(t11); // integrated reflectance RGB, mirror ray world hit distance A
Texture2D<float4> FloatNormalRoughness : register(t12);
Texture2D<float4> PbrDiffuseSh : register(t13);
Texture2D<float4> PbrSpecularSh : register(t14);
Texture2D<float4> PbrGlassSurface : register(t15);
Texture2D<float4> PbrDiffuseFactor : register(t16);
Texture2D<float4> PbrSpecularFactor : register(t17);
Texture2D<float4> DiffuseShReconstructed : register(t18);
Texture2D<float4> SpecularShReconstructed : register(t19);
RWTexture2D<float4> Color : register(u0);
RWTexture2D<float> Depth : register(u1);
RWTexture2D<float2> Motion : register(u2);
RWTexture2D<float> Reactive : register(u3);
RWTexture2D<float4> NextHistory : register(u4);
RWTexture2D<float4> Encoded : register(u5);
RWTexture2D<float4> PackedSpecular : register(u6);
RWTexture2D<float> SpecularHitDistance : register(u7);
RWTexture2D<float4> PackedDiffuseSh : register(u8);
RWTexture2D<float4> PackedSpecularSh : register(u9);

[numthreads(8, 8, 1)]
void PrepareRr(uint3 tid : SV_DispatchThreadID)
{
    if (any(tid.xy >= uint2(Size.xy))) return;
    float4 guide = FloatNormalRoughness[tid.xy];
    PackedSpecular[tid.xy] = float4(normalize(guide.xyz), saturate(guide.w));
    SpecularHitDistance[tid.xy] = max(PbrSpecularGuide[tid.xy].a, 0);
}

float2 Project(float3 direction, float3 forward, float3 right, float3 up)
{
    float z = max(dot(direction, forward), 0.00001);
    float2 ndc = float2(dot(direction, right) / (z * (Size.x / Size.y) * Projection.x),
                        dot(direction, up) / (z * Projection.x));
    return float2((ndc.x + 1) * 0.5, (1 - ndc.y) * 0.5) * Size.xy;
}

bool ReprojectHistory(float2 previousPixel, float previousZ, bool sky, out float3 color)
{
    // Convert the previous jittered raster position to texel-center coordinates.
    // Rounding each frame's jitter delta to one texel introduces a directional drift.
    float2 position = previousPixel - Jitter.zw - 0.5;
    int2 base = int2(floor(position));
    float2 fraction = frac(position);
    float3 sum = 0;
    float weights = 0;
    [unroll] for (int y = 0; y < 2; y++) [unroll] for (int x = 0; x < 2; x++)
    {
        int2 q = base + int2(x, y);
        if (any(q < 0) || any(q >= int2(Size.xy))) continue;
        float4 tap = History[q];
        bool valid = sky ? tap.w < 0 : tap.w > 0 && abs(tap.w - previousZ) < max(0.01, previousZ * 0.02);
        if (!valid) continue;
        // Reject disoccluded taps individually, then renormalize the remaining footprint.
        float weight = (x == 0 ? 1 - fraction.x : fraction.x) * (y == 0 ? 1 - fraction.y : fraction.y);
        sum += tap.rgb * weight;
        weights += weight;
    }
    color = sum / max(weights, 0.00001);
    return weights > 0.00001;
}

[numthreads(8, 8, 1)]
void Prepare(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float4 surface = Surfaces[p];
    bool sky = surface.w < 0;
    float3 currentDirection = sky ? surface.xyz : surface.xyz - CameraOrigin.xyz;
    float3 previousDirection = sky ? surface.xyz : surface.xyz - PreviousOrigin.xyz;
    float currentZ = dot(currentDirection, CameraForward.xyz);
    float previousZ = dot(previousDirection, PreviousForward.xyz);
    float2 currentPixel = Project(currentDirection, CameraForward.xyz, CameraRight.xyz, CameraUp.xyz);
    float2 previousPixel = Project(previousDirection, PreviousForward.xyz, PreviousRight.xyz, PreviousUp.xyz);
    float2 motion = previousPixel - currentPixel; // remove jitter by projecting both unjittered cameras
    if (Control.x != 0 || previousZ <= 0) motion = 0;
    Motion[p] = clamp(motion, -32700, 32700);
    // Conventional finite perspective depth: near=0.01, far=1000, FOV=2*atan(0.5).
    Depth[p] = sky ? 1 : saturate(Projection.z / (Projection.z-Projection.y) - Projection.z*Projection.y / ((Projection.z-Projection.y) * max(currentZ, Projection.y)));
    Reactive[p] = Projection.w != 0 ? PbrAlbedo[p].a : 0;

    float3 value = max(Raw[p].rgb, 0);
    if (Control.y != 1) {
        NextHistory[p] = float4(value, sky ? -1 : currentZ);
        Color[p] = float4(min(value, 65000), 1);
        return;
    }
    float3 lo = value, hi = value;
    float mean = 0, square = 0, count = 0;
    for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
    {
        int2 q = clamp(p + int2(x, y), 0, int2(Size.xy) - 1);
        float3 tap = max(Raw[q].rgb, 0);
        lo = min(lo, tap); hi = max(hi, tap);
        float l = dot(tap, float3(0.2126, 0.7152, 0.0722));
        mean += l; square += l*l; count++;
    }
    float variance = max(square / count - (mean / count) * (mean / count), 0.0001);
    if (Control.y == 1 && Control.x == 0 && previousZ > 0)
    {
        float3 old;
        if (ReprojectHistory(previousPixel, previousZ, sky, old))
            value = lerp(clamp(old, lo, hi), value, 1.0 / min(Control.z + 1, 32));
    }
    NextHistory[p] = float4(value, sky ? -1 : currentZ);
    Color[p] = float4(min(value, 65000), variance);
}

[numthreads(8, 8, 1)]
void PrepareGuides(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float4 surface = PbrGlassSurface[p];
    bool glass = surface.w < -1;
    bool sky = surface.w == -1;
    float3 currentDirection = sky ? surface.xyz : surface.xyz - CameraOrigin.xyz;
    float3 previousDirection = sky ? surface.xyz : surface.xyz - PreviousOrigin.xyz;
    float previousZ = dot(previousDirection, PreviousForward.xyz);
    float2 motion = Project(previousDirection, PreviousForward.xyz, PreviousRight.xyz, PreviousUp.xyz)
        - Project(currentDirection, CameraForward.xyz, CameraRight.xyz, CameraUp.xyz);
    Motion[p] = Control.x != 0 || previousZ <= 0 ? 0 : clamp(motion, -32700, 32700);
    // Keep the opaque background depth, patch closest-glass MV after composition as in NRD-Sample.
    // Deterministically traced first reflection/refraction lobes can retain SR history.
    Reactive[p] = glass ? 0.1 : PbrAlbedo[p].a;
}

float3 DiffuseAlbedo(int2 pixel)
{
    return Normals[pixel].w > 0.5 / 255.0 ? float3(0.7, 0.7, 0.7) : float3(0.65, 0.3, 0.12);
}

[numthreads(8, 8, 1)]
void PrepareNrd(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float4 surface = Surfaces[p];
    bool sky = surface.w < 0;
    float viewZ = dot(surface.xyz - CameraOrigin.xyz, CameraForward.xyz);
    // NRD linear view Z matches the right-handed camera matrix (forward is -Z).
    Depth[p] = sky ? -max(1000000, Projection.z * 2) : -viewZ;
    if (Projection.w != 0)
    {
        float4 d = PbrDiffuse[p], s = PbrSpecular[p];
        float4 shD, shS;
        Color[p] = RELAX_FrontEnd_PackSh(sky ? 0 : d.rgb, sky ? 0 : d.a,
            PbrDiffuseSh[p].xyz / max(_NRD_Luminance(d.rgb), 1e-8), shD, true);
        PackedSpecular[p] = RELAX_FrontEnd_PackSh(sky ? 0 : s.rgb, sky ? 0 : s.a,
            PbrSpecularSh[p].xyz / max(_NRD_Luminance(s.rgb), 1e-8), shS, true);
        PackedDiffuseSh[p] = sky ? 0 : shD;
        PackedSpecularSh[p] = sky ? 0 : shS;
    }
    else {
        float3 diffuse = Raw[p].rgb - (Lighting.x != 0 ? PbrUnfiltered[p].rgb : 0);
        Color[p] = sky ? 0 : RELAX_FrontEnd_PackRadianceAndHitDist(max(diffuse,0) / DiffuseAlbedo(p), Raw[p].w, true);
    }
}

[numthreads(8, 8, 1)]
void ComposeNrd(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float4 surface = Surfaces[p];
    float viewZ = dot(surface.xyz - CameraOrigin.xyz, CameraForward.xyz);
    // NRD leaves pixels beyond denoisingRange unwritten, including sky.
    float3 color;
    if (Projection.w != 0) {
        NRD_SG diffuse = RELAX_BackEnd_UnpackSh(Reconstructed[p], DiffuseShReconstructed[p].xyz);
        NRD_SG specular = RELAX_BackEnd_UnpackSh(SpecularReconstructed[p], SpecularShReconstructed[p].xyz);
        float4 guide = NRD_FrontEnd_UnpackNormalAndRoughness(FloatNormalRoughness[p]);
        float3 N = guide.xyz, V = normalize(CameraOrigin.xyz - surface.xyz);
        float3 d = NRD_SG_ResolveDiffuse(diffuse, N, V, guide.w);
        float3 s = NRD_SG_ResolveSpecular(specular, N, V, guide.w);
        if (Lighting.z != 0) {
            int2 e = min(p + int2(1, 0), int2(Size.xy) - 1), w = max(p - int2(1, 0), 0);
            int2 n = min(p + int2(0, 1), int2(Size.xy) - 1), south = max(p - int2(0, 1), 0);
            float2 scale = NRD_SG_ReJitter(diffuse, specular, V, guide.w, History[p].x,
                History[e].x, History[w].x, History[n].x, History[south].x, N,
                NRD_FrontEnd_UnpackNormalAndRoughness(FloatNormalRoughness[e]).xyz,
                NRD_FrontEnd_UnpackNormalAndRoughness(FloatNormalRoughness[w]).xyz,
                NRD_FrontEnd_UnpackNormalAndRoughness(FloatNormalRoughness[n]).xyz,
                NRD_FrontEnd_UnpackNormalAndRoughness(FloatNormalRoughness[south]).xyz);
            d *= scale.x; s *= scale.y;
        }
        if (PbrAlbedo[p].a > 0.5) { d = NRD_SG_ExtractColor(diffuse); s = NRD_SG_ExtractColor(specular); }
        color = d * PbrDiffuseFactor[p].rgb + s * PbrSpecularFactor[p].rgb + PbrUnfiltered[p].rgb;
    }
    else {
        color = RELAX_BackEnd_UnpackRadiance(Reconstructed[p]).rgb * DiffuseAlbedo(p);
        if (Lighting.x != 0) color += PbrUnfiltered[p].rgb;
    }
    if (surface.w < 0 || viewZ >= Projection.z) color = Raw[p].rgb;
    Color[p] = float4(max(color, 0), 1);
}

[numthreads(8, 8, 1)]
void Encode(uint3 tid : SV_DispatchThreadID)
{
    if (any(tid.xy >= uint2(Size.zw))) return;
    float3 color = max(Reconstructed[tid.xy].rgb, 0) * exp2(OutputParameters.w);
    if (OutputParameters.x != 0)
    {
        if (Projection.w != 0) color /= 1 + color / max(OutputParameters.z / OutputParameters.y, 1);
        color = float3(dot(color, float3(0.627404, 0.329283, 0.043313)),
                       dot(color, float3(0.069097, 0.919540, 0.011362)),
                       dot(color, float3(0.016391, 0.088013, 0.895595)));
        float3 y = pow(min(color * OutputParameters.y, OutputParameters.z) / 10000, 0.1593017578125);
        color = pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375);
    }
    else
    {
        if (Projection.w != 0) color = saturate((color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14));
        color = pow(color, 1.0 / 2.2);
    }
    Encoded[tid.xy] = float4(color, 1);
}
