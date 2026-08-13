// SceneData.hlsli - DXR shared structures and helpers
#ifndef SCENE_DATA_HLSLI
#define SCENE_DATA_HLSLI

struct SceneConstants
{
    float2 Resolution;
    float2 Mouse;
    float  Time;
    float  CameraDistance;
    int    Frame;
    int    _pad;
};

struct SphereData
{
    float3 Center;
    float  Radius;
    float3 Albedo;
    int    MaterialType;  // 0 = Lambertian, 1 = Metal, 2 = Dielectric
    float  MaterialParam; // roughness (Metal) or IOR (Dielectric)
    float3 _pad;
};

struct Attributes
{
    uint dummy;
};

// Sphere buffer (StructuredBuffer) - registered at t1 in RayTrace.hlsl
StructuredBuffer<SphereData> Spheres : register(t1);

// Fresnel-Schlick approximation
float Schlick(float cosine, float ior)
{
    float r0 = (1.0 - ior) / (1.0 + ior);
    r0 = r0 * r0;
    return r0 + (1.0 - r0) * pow(max(1.0 - cosine, 0.0), 5.0);
}

#endif // SCENE_DATA_HLSLI