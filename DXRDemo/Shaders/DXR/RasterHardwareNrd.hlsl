#include "NRD/NRD.hlsli"
cbuffer Frame : register(b0) {
    float4 Origin,Forward,Right,Up,PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,Size,Jitter;
}
Texture2D<float4> Base : register(t0);
Texture2D<float4> Normal : register(t1);
Texture2D<float> Depth : register(t2);
Texture2D<float4> Diffuse : register(t3);
Texture2D<float4> Specular : register(t4);
Texture2D<float4> DenoisedDiffuse : register(t5);
Texture2D<float4> DenoisedSpecular : register(t6);
Texture2D<float4> Direct : register(t7);
RWTexture2D<float4> PackedDiffuse : register(u0);
RWTexture2D<float4> PackedSpecular : register(u1);
RWTexture2D<float4> PackedNormal : register(u2);
RWTexture2D<float> ViewZ : register(u3);
RWTexture2D<float2> Motion : register(u4);
RWTexture2D<float4> Color : register(u5);
float LinearZ(float d) {return Origin.w*Right.w/(Origin.w+d*(Right.w-Origin.w));}
float3 World(int2 p) {
    float2 ndc=(p+.5+Jitter.xy)/Size.xy*2-1;
    float3 ray=Forward.xyz+Right.xyz*ndc.x*(Size.x/Size.y)*Forward.w-Up.xyz*ndc.y*Forward.w;
    return Origin.xyz+ray*LinearZ(Depth[p]);
}
float2 Project(float3 p,float3 origin,float4 forward,float3 right,float3 up) {
    float3 delta=p-origin;float z=max(dot(delta,forward.xyz),1e-6);
    return float2(dot(delta,right)/(z*forward.w*Size.x/Size.y),-dot(delta,up)/(z*forward.w))*Size.xy*.5;
}
void Factors(int2 p,out float3 diffuse,out float3 specular) {
    float4 a=Base[p],n=Normal[p];float3 v=normalize(Origin.xyz-World(p));
    NRD_MaterialFactors(normalize(n.xyz),v,a.rgb*(1-a.w),lerp(.04,a.rgb,a.w),n.w,diffuse,specular);
}
[numthreads(8,8,1)]
void Prepare(uint3 tid:SV_DispatchThreadID) {
    int2 p=tid.xy;if(any(p>=(int2)Size.xy))return;
    if(Depth[p]==0) {
        PackedDiffuse[p]=0;PackedSpecular[p]=0;
        PackedNormal[p]=NRD_FrontEnd_PackNormalAndRoughness(float3(0,1,0),1,0);
        ViewZ[p]=-max(1e6,Right.w*2);Motion[p]=0;return;
    }
    bool sky=false;float4 n=Normal[p];
    float3 world=World(p),d,s;Factors(p,d,s);
    float4 diffuse=Diffuse[p],specular=Specular[p];
    float4 a=Base[p];float3 sourceFactor=a.rgb*(1-a.w)*.96;
    PackedDiffuse[p]=RELAX_FrontEnd_PackRadianceAndHitDist(sky?0:diffuse.rgb*sourceFactor/d,sky?0:diffuse.w,true);
    PackedSpecular[p]=RELAX_FrontEnd_PackRadianceAndHitDist(sky?0:specular.rgb/s,sky?0:specular.w,true);
    PackedNormal[p]=NRD_FrontEnd_PackNormalAndRoughness(sky?float3(0,1,0):normalize(n.xyz),sky?1:n.w,0);
    ViewZ[p]=sky?-max(1e6,Right.w*2):-LinearZ(Depth[p]);
    Motion[p]=sky||Size.z!=0||dot(world-PreviousOrigin.xyz,PreviousForward.xyz)<=0?0:
        Project(world,PreviousOrigin.xyz,PreviousForward,PreviousRight.xyz,PreviousUp.xyz)
        -Project(world,Origin.xyz,Forward,Right.xyz,Up.xyz);
}
[numthreads(8,8,1)]
void Composite(uint3 tid:SV_DispatchThreadID) {
    int2 p=tid.xy;if(any(p>=(int2)Size.xy))return;
    if(Depth[p]==0||LinearZ(Depth[p])>=Right.w) {Color[p]=Direct[p];return;}
    float3 d,s;Factors(p,d,s);
    float3 diffuse=RELAX_BackEnd_UnpackRadiance(DenoisedDiffuse[p]).rgb;
    float3 specular=RELAX_BackEnd_UnpackRadiance(DenoisedSpecular[p]).rgb;
    Color[p]=float4(min(max(Direct[p].rgb+diffuse*d+specular*s,0),65000),1);
}
