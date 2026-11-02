cbuffer Frame : register(b0) {
    float4 CameraOrigin,CameraForward,CameraRight,CameraUp,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,PreviousSize;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
#include "RasterWorldCache.hlsli"
StructuredBuffer<WorldProbe> Input : register(t0);
StructuredBuffer<ProbeRay> Rays : register(t1);
RWStructuredBuffer<WorldProbe> Output : register(u0);
RWStructuredBuffer<WorldProbe> ClearOther : register(u1);
[numthreads(8,8,1)]
void Initialize(uint3 tid:SV_DispatchThreadID) {
    if(tid.y!=0||tid.x>=(uint)PreviousForward.w)return;
    Output[tid.x]=(WorldProbe)0;ClearOther[tid.x]=(WorldProbe)0;
}
[numthreads(8,8,1)]
void Blend(uint3 tid:SV_DispatchThreadID) {
    if(tid.y!=0||tid.x>=(uint)PreviousRight.y)return;
    uint slot=CacheUpdateSlot(tid.x,(uint)PreviousRight.w,(uint)PreviousRight.x,(uint)PreviousRight.y);int3 cell=CacheCell(slot);
    WorldProbe old=Input[slot],probe=(WorldProbe)0;
    bool same=old.position.w>0&&all((int3)old.cell.xyz==cell);
    float spacing=CacheGrid(slot/CacheCount()).w;
    float3 center=(cell+.5)*spacing;
    float3 position=same?old.position.xyz:center;
    uint back=0;float closest=1e30;float3 escape=0;
    [loop]for(uint ray=0;ray<(uint)PreviousRight.z;ray++) {
        float4 d=Rays[tid.x*((uint)PreviousRight.z+1)+ray].directionDistance;
        if(d.w<0) {back++;if(-d.w<closest) {closest=-d.w;escape=d.xyz*(-d.w+spacing*.1);}}
    }
    probe.cell=float4(cell,back>PreviousRight.z*.25?-1:1);
    if(probe.cell.w<0) {
        float3 relocation=position+escape-center;float maximum=spacing*.45;
        relocation*=min(1,maximum/max(length(relocation),1e-6));position=center+relocation;
    }
    probe.position=float4(position,PreviousRight.w+1);
    [unroll]for(uint axis=0;axis<6;axis++) {
        float3 n=CacheAxis(axis);float3 radiance=0;
        [loop]for(uint ray=0;ray<(uint)PreviousRight.z;ray++) {
            ProbeRay sample=Rays[tid.x*((uint)PreviousRight.z+1)+ray];
            float cosine=saturate(dot(n,sample.directionDistance.xyz));
            radiance+=sample.radiance.rgb*cosine*(4/PreviousRight.z);
        }
        ProbeRay nee=Rays[tid.x*((uint)PreviousRight.z+1)+(uint)PreviousRight.z];
        radiance+=nee.radiance.rgb*saturate(dot(n,nee.directionDistance.xyz));
        float alpha=same&&old.cell.w>0?(old.irradiance[axis].w<4?.5:.2):1;
        probe.irradiance[axis]=float4(lerp(old.irradiance[axis].rgb,min(radiance,65000),alpha),same&&old.cell.w>0?min(old.irradiance[axis].w+1,32):1);
    }
    [loop]for(uint bin=0;bin<64;bin++) {
        float3 n=CacheDirection(bin);float2 moments=0;float weight=0;
        [loop]for(uint ray=0;ray<(uint)PreviousRight.z;ray++) {
            float4 sample=Rays[tid.x*((uint)PreviousRight.z+1)+ray].directionDistance;
            float w=pow(saturate(dot(n,sample.xyz)),64);float distance=abs(sample.w);
            moments+=float2(distance,distance*distance)*w;weight+=w;
        }
        moments/=max(weight,1e-20);
        if((bin&1)==0)probe.moments[bin/2].xy=moments;else probe.moments[bin/2].zw=moments;
    }
    Output[slot]=probe;
}
