// Exercise the production environment sampler without geometry visibility or BSDFs.
#define RayGen UnusedSceneRayGen
#include "SceneTrace.hlsl"
#undef RayGen

[shader("raygeneration")]
void RayGen() {
    uint2 pixel = DispatchRaysIndex().xy;
    uint rng = Hash32(pixel.x + pixel.y * (uint)Size.x);
    float4 u = float4(Random(rng), Random(rng), Random(rng), Random(rng));
    float pdf;
    float3 direction = SampleEnvironment(u, pdf);
    Raw[pixel] = float4(Sky(direction) / pdf, 1);
    NormalRoughness[pixel] = float4(direction, pdf);
}
