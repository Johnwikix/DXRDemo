// RTXPT's effect-separated, per-vertex sampling adapted to the C# scene ABI.
#include "RTXPT/NoiseAndSequences.hlsli"

// Uniform solid-angle sampling of the distant solar disk. W stores 1-cos(radius),
// computed with sin on the CPU to retain precision at the real sun's small angle.
float3 SampleSun(float2 u) {
    float h=u.x*SunDirection.w;
    float cosine=1-h,sine=sqrt(max(h*(2-h),0)),s,c;
    sincos(6.28318530718*u.y,s,c);
    float3 axis=SunDirection.xyz;
    float3 tangent=normalize(cross(abs(axis.y)<.99?float3(0,1,0):float3(1,0,0),axis));
    return axis*cosine+(tangent*c+cross(axis,tangent)*s)*sine;
}
// Li/pdf for a disk normalized to the input perpendicular irradiance:
// integral_disk cos(theta) dOmega = pi*sin(radius)^2.
float SunSampleWeight() { return 2/(2-SunDirection.w); }

uint PathSeed(uint2 pixel, uint vertex, uint effect) {
    return Hash32Combine(Hash32Combine(Hash32(pixel.x + 0x035f9f29u), pixel.y), vertex * 8u + effect);
}
float3 ScatterSamples(uint2 pixel, uint sampleIndex, uint vertex) {
    return burley_shuffled_scrambled_sobol_3D(sampleIndex, PathSeed(pixel, vertex, 1));
}
float RouletteSample(uint2 pixel, uint sampleIndex, uint vertex) {
    return burley_shuffled_scrambled_sobol_1D(sampleIndex, PathSeed(pixel, vertex, 6));
}
float2 CameraSample(uint2 pixel, uint sampleIndex) {
    return burley_shuffled_scrambled_sobol_2D(sampleIndex, PathSeed(pixel, 0, 0)) - .5;
}
