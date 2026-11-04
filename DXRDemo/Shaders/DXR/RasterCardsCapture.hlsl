#define RASTER_LOCAL_CAPTURE 1
#include "Raster.hlsl"
cbuffer CardCapture : register(b2) {
    uint4 CardKey;
    float4 CardCenter,CardU,CardV,CardN;
}
StructuredBuffer<uint> CardIndices : register(t11);
Varyings CardVertex(uint id:SV_VertexID) {
    Varyings result=LoadVertex(id);
    float3 local=Vertices[Instances[NodeIndex].mesh.x+id].position.xyz-CardCenter.xyz;
    result.position=float4(dot(local,CardU.xyz)/CardU.w,dot(local,CardV.xyz)/CardV.w,.5+.5*dot(local,CardN.xyz)/CardN.w,1);
    return result;
}
struct CardCaptureOutput {
    float4 base:SV_Target0; float4 normal:SV_Target1; float4 emission:SV_Target2;
    float4 position:SV_Target3; float4 geometry:SV_Target4;
};
CardCaptureOutput CardPixel(Varyings input,bool front:SV_IsFrontFace,uint primitive:SV_PrimitiveID) {
    AlphaTest(input,front);Surface s=Evaluate(input,front);
    Material material=Materials[Instances[NodeIndex].mesh.z];
    // 混合/透射不以一个 opaque texel 冒充覆盖，沿原有命中材质路径处理。
    if(material.flags.x==2||s.transmission>0)discard;
    Instance node=Instances[NodeIndex];uint index=node.mesh.y+primitive*3;
    float3 a=Vertices[node.mesh.x+CardIndices[index]].position.xyz;
    float3 b=Vertices[node.mesh.x+CardIndices[index+1]].position.xyz;
    float3 c=Vertices[node.mesh.x+CardIndices[index+2]].position.xyz;
    float3 g=normalize(mul(float4(normalize(cross(b-a,c-a)),0),node.normal).xyz);
    if(!ObjectFront(front))g=-g;
    CardCaptureOutput result;
    result.base=float4(min(s.base,65000),s.metallic);result.normal=float4(s.n,s.roughness);
    result.emission=float4(min(s.emission,65000),s.unlit!=0?-1:s.ao);
    result.position=float4(s.p,(primitive+1.0)*(ObjectFront(front)?1:-1));
    result.geometry=float4(g,s.transmission);return result;
}
