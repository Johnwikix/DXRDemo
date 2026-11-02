// 诊断专用：独立于实际追踪结果验证三级覆盖、跨层混合与身份/遮挡守卫。
cbuffer Frame : register(b0) {
    float4 CameraOrigin,CameraForward,CameraRight,CameraUp,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,PreviousSize;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
#include "RasterWorldCache.hlsli"
StructuredBuffer<float4> Queries : register(t0);
RWStructuredBuffer<float4> Results : register(u0);
[numthreads(8,8,1)]
void Query(uint3 id:SV_DispatchThreadID) {
    if(id.y!=0||id.x>=(uint)Control.x)return;
    Results[id.x]=float4(CacheIrradiance(Queries[id.x*2].xyz,Queries[id.x*2+1].xyz),1);
}
