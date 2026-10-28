#include "SceneEnvironment.hlsli"
float EnvironmentPdf(float3 direction) {
    if(EnvironmentMap.x==0) return 1/(4*PI);
    int2 pixel=(int2)(EnvironmentUV(direction)*EnvironmentMap.xy);
    return EnvironmentTexels[EnvironmentIndex(pixel)].radiance.w;
}
float3 SampleEnvironment(float4 u,out float pdf) {
    if(EnvironmentMap.x==0) {
        float y=1-2*u.y,phi=2*PI*u.z,r=sqrt(max(0,1-y*y)); pdf=1/(4*PI);
        return float3(r*cos(phi),y,r*sin(phi));
    }
    uint count=(uint)(EnvironmentMap.x*EnvironmentMap.y);
    uint index=min((uint)(u.x*count),count-1);
    float4 alias=EnvironmentTexels[index].sampling;
    if(u.y>=alias.x) index=(uint)alias.y;
    uint2 pixel=uint2(index%(uint)EnvironmentMap.x,index/(uint)EnvironmentMap.x);
    float y=lerp(cos(PI*pixel.y/EnvironmentMap.y),cos(PI*(pixel.y+1)/EnvironmentMap.y),u.w);
    float phi=2*PI*(pixel.x+u.z)/EnvironmentMap.x+EnvironmentMap.z,r=sqrt(max(0,1-y*y));
    pdf=EnvironmentTexels[index].radiance.w;
    return float3(r*cos(phi),y,r*sin(phi));
}
