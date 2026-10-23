// RTXPT's effect-separated, per-vertex sampling adapted to the C# scene ABI.
#include "RTXPT/NoiseAndSequences.hlsli"

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
