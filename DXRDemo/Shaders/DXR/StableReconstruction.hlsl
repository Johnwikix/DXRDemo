#include "NRD/NRD.hlsli"
cbuffer Frame : register(b0) {
    float4 Origin,Forward,Right,Up,PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,Size,Jitter;
}
Texture2D<float4> Input : register(t0);
Texture2D<float4> Surface : register(t1);
Texture2D<float4> Normal : register(t2);
Texture2D<float4> TransmissionSurface : register(t3);
Texture2D<float4> ReflectionDenoised : register(t4);
Texture2D<float4> TransmissionDenoised : register(t5);
Texture2D<float4> Primary : register(t6);
RWTexture2D<float4> Packed : register(u0);
RWTexture2D<float4> PackedNormal : register(u1);
RWTexture2D<float> ViewZ : register(u2);
RWTexture2D<float2> Motion : register(u3);
RWTexture2D<float4> Output : register(u4);
float2 Project(float3 position,float3 origin,float3 forward,float3 right,float3 up,float tanHalfFov) {
    float3 d=position-origin; float z=max(dot(d,forward),1e-6);
    float2 ndc=float2(dot(d,right)/(z*tanHalfFov*Size.x/Size.y),-dot(d,up)/(z*tanHalfFov));
    return (ndc*.5+.5)*Size.xy;
}
[numthreads(8,8,1)]
void PrepareStable(uint3 tid:SV_DispatchThreadID) {
    uint2 p=tid.xy; if(any(p>=(uint2)Size.xy)) return;
    float4 surface=Surface[p],normal=Normal[p],value=Input[p];
    bool valid=surface.w>0;
    float z=dot(surface.xyz-Origin.xyz,Forward.xyz);
    ViewZ[p]=valid?-z:-max(1e6,Size.z*2);
    Packed[p]=RELAX_FrontEnd_PackRadianceAndHitDist(valid?value.rgb:0,valid?value.w:0,true);
    PackedNormal[p]=NRD_FrontEnd_PackNormalAndRoughness(normalize(normal.xyz),normal.w,0);
    Motion[p]=valid?Project(surface.xyz,PreviousOrigin.xyz,PreviousForward.xyz,PreviousRight.xyz,PreviousUp.xyz,PreviousForward.w)
        -Project(surface.xyz,Origin.xyz,Forward.xyz,Right.xyz,Up.xyz,Forward.w):0;
}
[numthreads(8,8,1)]
void ComposeStable(uint3 tid:SV_DispatchThreadID) {
    uint2 p=tid.xy; if(any(p>=(uint2)Size.xy)) return;
    float4 r=Surface[p],t=TransmissionSurface[p];
    bool validR=r.w>0 && dot(r.xyz-Origin.xyz,Forward.xyz)<Size.z;
    bool validT=t.w>0 && dot(t.xyz-Origin.xyz,Forward.xyz)<Size.z;
    // The compose table aliases Input/Normal to the two unfiltered plane signals.
    float3 reflection=validR?RELAX_BackEnd_UnpackRadiance(ReflectionDenoised[p]).rgb:Input[p].rgb;
    float3 transmission=validT?RELAX_BackEnd_UnpackRadiance(TransmissionDenoised[p]).rgb:Normal[p].rgb;
    Output[p]=float4(max(Primary[p].rgb+reflection+transmission,0),1);
}
