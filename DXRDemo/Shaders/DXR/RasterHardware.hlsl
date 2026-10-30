// 与路径追踪共用材质求值、MASK/BLEND、阴影透射、GGX 和实例 ABI。
#include "SceneRayCore.hlsli"
#include "PathEnvironment.hlsli"
float Luminance(float3 value) { return dot(value,float3(.2126,.7152,.0722)); }
#include "PathBsdf.hlsli"
#include "SceneVisibility.hlsli"

Texture2D<float4> RasterBase : register(t0, space3);
Texture2D<float4> RasterNormal : register(t1, space3);
Texture2D<float> RasterDepth : register(t2, space3);
RWTexture2D<float4> IndirectDiffuse : register(u0, space3);
RWTexture2D<float4> IndirectSpecular : register(u1, space3);

float Mis(float a,float b) {
    float scale=max(a,b);if(scale<=0)return 0;
    a/=scale;b/=scale;return a*a/(a*a+b*b);
}
float RasterZ(float depth) {
    return CameraOrigin.w*CameraRight.w/(CameraOrigin.w+depth*(CameraRight.w-CameraOrigin.w));
}
float3 RasterPosition(int2 pixel) {
    float2 ndc=(pixel+.5+Size.zw)/Size.xy*2-1;
    float3 ray=CameraForward.xyz+CameraRight.xyz*ndc.x*(Size.x/Size.y)*CameraForward.w-CameraUp.xyz*ndc.y*CameraForward.w;
    return CameraOrigin.xyz+ray*RasterZ(RasterDepth[pixel]);
}
Surface PrimarySurface(int2 pixel) {
    Surface s=(Surface)0;
    float4 a=RasterBase[pixel],n=RasterNormal[pixel];
    s.p=RasterPosition(pixel);s.n=SafeNormalize(n.xyz,float3(0,1,0));s.g=s.n;
    // 从 opaque depth 选择同表面的一侧导数；几何法线负责起点偏移，不能沿凹凸法线穿墙。
    int2 lo=max(pixel-1,0),hi=min(pixel+1,int2(Size.xy)-1);
    float3 left=RasterPosition(int2(lo.x,pixel.y)),right=RasterPosition(int2(hi.x,pixel.y));
    float3 up=RasterPosition(int2(pixel.x,lo.y)),down=RasterPosition(int2(pixel.x,hi.y));
    float3 dx=length(s.p-left)<length(right-s.p)?s.p-left:right-s.p;
    float3 dy=length(s.p-up)<length(down-s.p)?s.p-up:down-s.p;
    s.g=SafeNormalize(cross(dx,dy),s.n);if(dot(s.g,CameraOrigin.xyz-s.p)<0)s.g=-s.g;
    s.base=a.rgb;s.metallic=a.w;s.roughness=max(n.w,.02);s.ior=1.5;s.eta=1/1.5;s.bsdfSampling=1;
    return s;
}

// 对共享发光面 CDF 的一个面积样本。返回出射反射光；primaryDiffuse 时保存 E/π。
float3 AreaLight(Surface s,float3 view,inout uint rng,bool primaryDiffuse) {
    if(Environment.y<=0)return 0;
    float choice=Random(rng);uint lo=0,hi=(uint)Environment.y-1;
    while(lo<hi) {uint mid=(lo+hi)/2;if(Emitters[mid].meta.y<=choice)lo=mid+1;else hi=mid;}
    Emitter emitter=Emitters[lo];if(emitter.e1.w<=0)return 0;
    float u=sqrt(Random(rng));float2 bary=float2(u*(1-Random(rng)),0);bary.y=u-bary.x;
    float3 position=emitter.p0.xyz+emitter.e1.xyz*bary.x+emitter.e2.xyz*bary.y;
    float3 delta=position-s.p;float distance=length(delta);float3 l=delta/max(distance,1e-8);
    if(dot(s.n,l)<=0||dot(s.g,l)<=0)return 0;
    Surface lamp=Evaluate((uint)emitter.meta.z,(uint)emitter.meta.w,bary,0,-l);
    Material m=Materials[lamp.material];if(m.flags.z==0&&lamp.front==0)return 0;
    if(m.flags.x==1&&lamp.alpha<m.flags.y)return 0;
    float pdf=emitter.e1.w*distance*distance/max(abs(dot(lamp.g,-l)),1e-8);
    bool stochastic;float3 visibility=Visibility(Offset(s,l),l,distance-max(Limits.x*8,distance*1e-5),rng,stochastic);
    float3 emission=lamp.emission*(m.flags.x==2?lamp.alpha:1);
    float cosine=saturate(dot(s.n,l));
    if(primaryDiffuse)return emission*visibility*cosine/(PI*max(pdf,1e-8))*Mis(pdf,cosine/PI);
    float3 d,r;float unused;Bsdf(s,view,l,d,r,unused);
    return (d+r)*emission*visibility*cosine/max(pdf,1e-8);
}
float3 EnvironmentLight(Surface s,float3 view,inout uint rng,bool primaryDiffuse) {
    if(Environment.x<=0)return 0;
    float pdf;float3 l=SampleEnvironment(float4(Random(rng),Random(rng),Random(rng),Random(rng)),pdf);
    float cosine=saturate(dot(s.n,l));if(cosine<=0||dot(s.g,l)<=0||pdf<=0)return 0;
    bool stochastic;float3 visibility=Visibility(Offset(s,l),l,CameraRight.w,rng,stochastic);
    if(primaryDiffuse)return Sky(l)*visibility*cosine/(PI*pdf)*Mis(pdf,cosine/PI);
    float3 d,r;float unused;Bsdf(s,view,l,d,r,unused);
    return (d+r)*Sky(l)*visibility*cosine/pdf;
}
float3 HitLighting(Surface s,float3 view,inout uint rng) {
    float3 result=AreaLight(s,view,rng,false)+EnvironmentLight(s,view,rng,false);
    uint count=(uint)Control.w+(any(SunRadiance.rgb>0)?1:0);
    for(uint i=0;i<count;i++) {
        Light light=(Light)0;
        if(i<(uint)Control.w)light=Lights[i];
        else {light.directionRange=float4(-SunDirection.xyz,0);light.colorIntensity=float4(SunRadiance.rgb,1);}
        float3 l=-light.directionRange.xyz;float distance=CameraRight.w,attenuation=1;
        if(light.positionType.w!=0) {
            l=light.positionType.xyz-s.p;distance=length(l);l/=max(distance,1e-8);attenuation=1/max(distance*distance,1e-8);
            if(light.directionRange.w>0)attenuation*=saturate(1-pow(distance/light.directionRange.w,4));
            if(light.positionType.w==2)attenuation*=pow(saturate((dot(-l,light.directionRange.xyz)-light.spot.y)/max(light.spot.x-light.spot.y,1e-6)),2);
        }
        float3 d,r;float unused;Bsdf(s,view,l,d,r,unused);
        if(!any(d+r>0))continue;
        bool stochastic;float3 visibility=Visibility(Offset(s,l),l,distance-Limits.x*4,rng,stochastic,true);
        result+=(d+r)*light.colorIntensity.rgb*light.colorIntensity.w*attenuation*saturate(dot(s.n,l))*visibility;
    }
    return result;
}

[shader("raygeneration")]
void RasterLightingRayGen() {
    uint2 pixel=DispatchRaysIndex().xy;
    if(RasterDepth[pixel]==0) {IndirectDiffuse[pixel]=0;IndirectSpecular[pixel]=0;return;}
    Surface s=PrimarySurface(pixel);float3 view=normalize(CameraOrigin.xyz-s.p);
    uint rng=PathSeed(pixel,(uint)Control.z,7);
    float2 u=float2(Random(rng),Random(rng));float radius=sqrt(u.x),phi=2*PI*u.y;
    float3 l=Local(s.n,float3(radius*cos(phi),radius*sin(phi),sqrt(1-u.x)));
    float diffuseHit=0;
    float3 incoming=AreaLight(s,view,rng,true)+EnvironmentLight(s,view,rng,true);
    if(dot(s.g,l)>0) {
        Payload hit=Trace(Offset(s,l),l,CameraRight.w,false,rng,false,true);
        diffuseHit=hit.instance==0xffffffff?65504:hit.t;
        bool stochastic;
        float3 transmission=TransmissionVisibility(Offset(s,l),l,hit.t-Limits.x*4,rng,stochastic);
        float pdf=saturate(dot(s.n,l))/PI;
        if(hit.instance==0xffffffff)incoming+=transmission*Sky(l)*Mis(pdf,EnvironmentPdf(l));
        else {
            Surface secondary=Evaluate(hit.instance,hit.primitive,hit.bary,hit.t*CameraForward.w*2/Size.y,-l);
            float lightPdf=secondary.emissionPdf*hit.t*hit.t/max(abs(dot(secondary.g,-l)),1e-8);
            incoming+=transmission*(secondary.emission*Mis(pdf,lightPdf)+HitLighting(secondary,-l,rng));
        }
    }
    IndirectDiffuse[pixel]=float4(min(max(incoming,0),65000),min(diffuseHit,65504));

    // 独立 GGX VNDF 镜面样本，命中重新着色；不读取当前最终颜色或漫反射缓存。
    float3 micro=Local(s.n,sampleGGX_VNDF(s.roughness*s.roughness,ToLocal(s.n,view),float2(Random(rng),Random(rng))));
    l=reflect(-view,micro);float3 reflected=0;float specularHit=0;
    float specPdf=ReflectionPdf(s,view,l);
    if(specPdf>0) {
        Payload hit=Trace(Offset(s,l),l,CameraRight.w,false,rng,false,true);
        specularHit=hit.instance==0xffffffff?65504:hit.t;
        float3 radiance=Sky(l);
        if(hit.instance!=0xffffffff) {
            Surface secondary=Evaluate(hit.instance,hit.primitive,hit.bary,hit.t*CameraForward.w*2/Size.y,-l);
            radiance=secondary.emission+HitLighting(secondary,-l,rng);
        }
        float3 d,r;float unused;Bsdf(s,view,l,d,r,unused);
        bool stochastic;
        float3 transmission=TransmissionVisibility(Offset(s,l),l,hit.t-Limits.x*4,rng,stochastic);
        reflected=r*radiance*transmission*saturate(dot(s.n,l))/specPdf;
    }
    IndirectSpecular[pixel]=float4(min(max(reflected,0),65000),min(specularHit,65504));
}
