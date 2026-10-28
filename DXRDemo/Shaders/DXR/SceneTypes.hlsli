// Explicit scene ABI. C# layouts live in SceneAsset.cs and Rendering/GpuScene.cs.
static const float PI = 3.14159265359;
struct Vertex { float4 position, normal, tangent, uv, color; };
struct Binding { float4 info, transform, extra; };
struct Material { float4 baseColor, emissive, factors, flags; Binding baseMap, mrMap, normalMap, aoMap, emissiveMap;
    float4 transmission, attenuation; Binding transmissionMap, thicknessMap; };
struct Instance { uint4 mesh; row_major float4x4 world, normal; float4 emissionSampling; };
struct Light { float4 positionType, directionRange, colorIntensity, spot; };
struct Emitter { float4 p0, e1, e2, meta; };

float2 UV(Binding b, float4 uv) {
    float2 value = (b.info.y == 0 ? uv.xy : uv.zw) * b.transform.zw;
    float s, c; sincos(b.extra.x, s, c);
    return float2(c * value.x - s * value.y, s * value.x + c * value.y) + b.transform.xy;
}
