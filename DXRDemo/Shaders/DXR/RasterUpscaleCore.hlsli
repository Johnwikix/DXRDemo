cbuffer Frame : register(b0) {
    float4 Origin,Forward,Right,Up,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,Sampling;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
Texture2D<float4> Base : register(t0);
Texture2D<float4> Normal : register(t1);
Texture2D<float> Depth : register(t2);
Texture2D<float4> Direct : register(t3);
Texture2D<float4> SampleBase : register(t4);
Texture2D<float4> SampleNormal : register(t5);
Texture2D<float> SampleDepth : register(t6);
Texture2D<float4> Diffuse : register(t7);
Texture2D<float4> Specular : register(t8);
Texture2D<float4> Emission : register(t9);
Texture2D<float4> RawSpecular : register(t10);
RWTexture2D<float4> Color : register(u0);
float Z(float d) {return Origin.w*Right.w/(Origin.w+d*(Right.w-Origin.w));}
float3 Position(int2 p,float depth) {
    float2 ndc=(p+.5+Size.zw)/Size.xy*2-1;
    return Origin.xyz+(Forward.xyz+Right.xyz*ndc.x*(Size.x/Size.y)*Forward.w-Up.xyz*ndc.y*Forward.w)*Z(depth);
}
[numthreads(8,8,1)]
void Composite(uint3 tid:SV_DispatchThreadID) {
    int2 p=tid.xy;if(any(p>=(int2)Size.xy))return;
    if(Depth[p]==0||Emission[p].w<0) {Color[p]=Direct[p];return;}
    float4 a=Base[p],n=Normal[p];float3 world=Position(p,Depth[p]);
    float3 d=1,s=1;
#if RASTER_NRD
    NRD_MaterialFactors(normalize(n.xyz),normalize(Origin.xyz-world),a.rgb*(1-a.w),lerp(.04,a.rgb,a.w),n.w,d,s);
#endif
    float3 diffuse=0,specular=0,rawSpecular=0;float sum=0,specSum=0;
    float2 grid=(p-floor(Sampling.z*.5))/Sampling.z;int2 center=(int2)floor(grid);
    [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++) {
        int2 q=clamp(center+int2(x,y),0,(int2)Sampling.xy-1);
        float depth=SampleDepth[q];if(depth==0)continue;
        float4 m=SampleNormal[q];int2 source=min(q*(int)Sampling.z+(int)floor(Sampling.z*.5),(int2)Size.xy-1);
        float3 delta=Position(source,depth)-world;
        // 同一斜面允许切向距离；法线距离/朝向拒绝跨墙与轮廓泄漏。
        float footprint=max(Z(Depth[p])*Forward.w*2/Size.y,1e-5);
        float plane=max(abs(dot(delta,n.xyz)),abs(dot(delta,m.xyz)));
        float w=exp(-plane/max(footprint*Sampling.z*.4,1e-4))*pow(saturate(dot(n.xyz,m.xyz)),24)
            *exp(-dot(q-grid,q-grid)*.75);
#if RASTER_NRD
        if(Sampling.w>0) {
            diffuse+=(Lighting.x>0?Diffuse[q].rgb:RELAX_BackEnd_UnpackRadiance(Diffuse[q]).rgb)*w;
            float sw=w*pow(saturate(1-abs(n.w-m.w)),32);
            if(Lighting.x>0&&m.w>.8&&SampleBase[q].w<.2)rawSpecular+=RawSpecular[q].rgb*sw;
            else specular+=RELAX_BackEnd_UnpackRadiance(Specular[q]).rgb*sw;
            specSum+=sw;
        } else
#endif
        {
            diffuse+=Diffuse[q].rgb*w;
            float sw=w*pow(saturate(1-abs(n.w-m.w)),32);
            rawSpecular+=Specular[q].rgb*sw;specSum+=sw;
        }
        sum+=w;
    }
    float3 diffuseFactor=a.rgb*(1-a.w)*.96;
#if RASTER_NRD
    if(Sampling.w>0&&Lighting.x==0)diffuseFactor=d;
#endif
    float3 indirect=diffuse/max(sum,1e-8)*diffuseFactor+(specular*s+rawSpecular)/max(specSum,1e-8);
    Color[p]=float4(min(max(Direct[p].rgb+indirect,0),65000),1);
}
