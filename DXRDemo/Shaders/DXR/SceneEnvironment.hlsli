// Latitude-longitude HDR lookup and exact texel solid-angle importance sampling.
struct EnvironmentTexel { float4 radiance, sampling; };
StructuredBuffer<EnvironmentTexel> EnvironmentTexels : register(t7);
float2 EnvironmentUV(float3 direction) {
    return float2(frac((atan2(direction.z,direction.x)-EnvironmentMap.z)/(2*PI)),acos(clamp(direction.y,-1,1))/PI);
}
uint EnvironmentIndex(int2 pixel) {
    int width=(int)EnvironmentMap.x;
    return clamp(pixel.y,0,(int)EnvironmentMap.y-1)*width+(pixel.x%width+width)%width;
}
float3 Sky(float3 direction) {
    if(EnvironmentMap.x==0) return lerp(float3(.45,.55,.7),float3(.1,.2,.4),saturate(direction.y))*Environment.x;
    float2 p=EnvironmentUV(direction)*EnvironmentMap.xy-.5;
    int2 base=(int2)floor(p); float2 f=frac(p);
    return lerp(lerp(EnvironmentTexels[EnvironmentIndex(base)].radiance.rgb,EnvironmentTexels[EnvironmentIndex(base+int2(1,0))].radiance.rgb,f.x),
        lerp(EnvironmentTexels[EnvironmentIndex(base+int2(0,1))].radiance.rgb,EnvironmentTexels[EnvironmentIndex(base+1)].radiance.rgb,f.x),f.y)*Environment.x;
}
