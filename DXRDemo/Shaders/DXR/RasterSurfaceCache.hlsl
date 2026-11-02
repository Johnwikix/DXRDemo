cbuffer Frame : register(b0) {
    float4 CameraOrigin,CameraForward,CameraRight,CameraUp,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,PreviousSize;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
struct SurfaceSample {uint4 key;float4 radiance,position;};
RWStructuredBuffer<SurfaceSample> SurfaceA : register(u0);
RWStructuredBuffer<SurfaceSample> SurfaceB : register(u1);
RWStructuredBuffer<uint> Statistics : register(u2);
[numthreads(8,8,1)]
void Initialize(uint3 tid:SV_DispatchThreadID) {
    if(tid.y!=0||tid.x>=(uint)Lighting.z)return;
    SurfaceA[tid.x]=(SurfaceSample)0;SurfaceB[tid.x]=(SurfaceSample)0;
}
[numthreads(8,8,1)]
void ResetBudget(uint3 tid:SV_DispatchThreadID) {
    if(tid.y==0&&tid.x<4)Statistics[tid.x]=0;
}
