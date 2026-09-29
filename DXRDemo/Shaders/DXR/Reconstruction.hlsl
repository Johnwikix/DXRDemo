// Linear Rec.709 SR path. The SDK consumes projection jitter; ray sampling uses its negative.
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
    float4 Control;          // history reset, denoiser mode, history length, spatial level
    float4 OutputParameters;// HDR, SDR white nits, peak nits, unused
};

Texture2D<float4> Raw : register(t0);
Texture2D<float4> Normals : register(t1);
Texture2D<float4> Surfaces : register(t2); // world position + ray distance; sky direction + -1
Texture2D<float4> History : register(t3);  // linear radiance + previous view depth (-1 for sky)
Texture2D<float4> FilterInput : register(t4);
Texture2D<float4> Reconstructed : register(t5);
RWTexture2D<float4> Color : register(u0);
RWTexture2D<float> Depth : register(u1);
RWTexture2D<float2> Motion : register(u2);
RWTexture2D<float> Reactive : register(u3);
RWTexture2D<float4> NextHistory : register(u4);
RWTexture2D<float4> Encoded : register(u5);

float2 Project(float3 direction, float3 forward, float3 right, float3 up)
{
    float z = max(dot(direction, forward), 0.00001);
    float2 ndc = float2(dot(direction, right) / (z * (Size.x / Size.y) * 0.5),
                        dot(direction, up) / (z * 0.5));
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
    Depth[p] = sky ? 1 : saturate(1000.0 / 999.99 - 10.0 / (999.99 * max(currentZ, 0.01)));
    Reactive[p] = 0; // All current meshes are opaque and have valid camera motion.

    float3 value = max(Raw[p].rgb, 0);
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
    if (Control.y != 0 && Control.x == 0 && previousZ > 0)
    {
        float3 old;
        if (ReprojectHistory(previousPixel, previousZ, sky, old))
            value = lerp(clamp(old, lo, hi), value, 1.0 / min(Control.z + 1, 32));
    }
    NextHistory[p] = float4(value, sky ? -1 : currentZ);
    Color[p] = float4(min(value, 65000), variance);
}

[numthreads(8, 8, 1)]
void Filter(uint3 tid : SV_DispatchThreadID)
{
    int2 p = tid.xy;
    if (any(p >= int2(Size.xy))) return;
    float4 center = FilterInput[p];
    float4 n = Normals[p];
    float distance = Surfaces[p].w;
    float3 normal = n.xyz * 2 - 1;
    float luminance = dot(center.rgb, float3(0.2126, 0.7152, 0.0722));
    float4 sum = center;
    float weights = 1;
    int stepSize = 1 << (int)Control.w;
    for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
    {
        if (x == 0 && y == 0) continue;
        int2 q = p + int2(x, y) * stepSize;
        if (any(q < 0) || any(q >= int2(Size.xy))) continue;
        float4 otherNormal = Normals[q];
        if (abs(n.w - otherNormal.w) > 0.5 / 255.0) continue;
        float4 tap = FilterInput[q];
        float dl = abs(dot(tap.rgb, float3(0.2126, 0.7152, 0.0722)) - luminance);
        float weight = exp(-dl / (sqrt(max(center.w, 0.0001)) + 0.0001));
        if (distance >= 0)
        {
            float dn = 1 - dot(normal, otherNormal.xyz * 2 - 1);
            float dd = abs(Surfaces[q].w - distance) / max(distance, 0.5);
            weight *= exp(-dn * dn / 0.045 - dd / (0.7 * pow(1.5, Control.w)));
        }
        sum += tap * weight; weights += weight;
    }
    Color[p] = sum / weights;
}

[numthreads(8, 8, 1)]
void Encode(uint3 tid : SV_DispatchThreadID)
{
    if (any(tid.xy >= uint2(Size.zw))) return;
    float3 color = max(Reconstructed[tid.xy].rgb, 0);
    if (OutputParameters.x != 0)
    {
        color = float3(dot(color, float3(0.627404, 0.329283, 0.043313)),
                       dot(color, float3(0.069097, 0.919540, 0.011362)),
                       dot(color, float3(0.016391, 0.088013, 0.895595)));
        float3 y = pow(min(color * OutputParameters.y, OutputParameters.z) / 10000, 0.1593017578125);
        color = pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375);
    }
    else color = pow(color, 1.0 / 2.2);
    Encoded[tid.xy] = float4(color, 1);
}
