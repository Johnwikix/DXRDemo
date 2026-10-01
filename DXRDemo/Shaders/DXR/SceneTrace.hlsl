// Explicit scene ABI. C# layouts live in SceneAsset.cs and DxrSceneBackend.cs.
static const float PI = 3.14159265359;
struct Vertex { float4 position, normal, tangent, uv, color; };
struct Binding { float4 info, transform, extra; };
struct Material { float4 baseColor, emissive, factors, flags; Binding baseMap, mrMap, normalMap, aoMap, emissiveMap;
    float4 transmission, attenuation; Binding transmissionMap, thicknessMap; };
struct Instance { uint4 mesh; row_major float4x4 world, normal; };
struct Light { float4 positionType, directionRange, colorIntensity, spot; };
struct Emitter { float4 p0, e1, e2, meta; };
#if DXR_SHADER_MODEL_69
// SM 6.9 payload access qualifiers keep any-hit state small while allowing
// HitObject::TraceRay/Invoke to move closest-hit work to a coherent context.
struct [raypayload] Payload {
    float t : read(caller) : write(caller,closesthit);
    uint instance : read(caller) : write(caller,closesthit,miss);
    uint primitive : read(caller) : write(caller,closesthit);
    float2 bary : read(caller) : write(caller,closesthit);
    uint random : read(caller,anyhit) : write(caller,anyhit);
    uint transparency : read(caller,anyhit) : write(caller,anyhit);
};
#else
struct Payload { float t; uint instance, primitive; float2 bary; uint random, transparency; };
#endif
struct Surface { float3 p, n, g, base, emission; float metallic, roughness, alpha, ao, transmission, ior, thickness; uint material, front, causticCaster; };
cbuffer Frame : register(b0) {
    float4 CameraOrigin, CameraForward, CameraRight, CameraUp, Size, Control, Environment, Limits, SunDirection, SunRadiance;
    float4 PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, PreviousSize;
    float4 Lighting, CausticBounds, CausticSettings, NeuralSettings, NeuralBounds;
}
RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<Vertex> Vertices : register(t1);
StructuredBuffer<uint> Indices : register(t2);
StructuredBuffer<Instance> Instances : register(t3);
StructuredBuffer<Material> Materials : register(t4);
StructuredBuffer<Light> Lights : register(t5);
StructuredBuffer<Emitter> Emitters : register(t6);
Texture2D<float4> Textures[1024] : register(t0, space1);
SamplerState Samplers[1024] : register(s0, space1);
#include "NeuralRadianceCache.hlsli"
#include "NRD/NRD.hlsli"
Texture2D<float4> OpaqueColor : register(t0, space2);
Texture2D<float4> OpaqueSurface : register(t1, space2);
Texture2D<float4> OpaqueNormalRoughness : register(t2, space2);
RWTexture2D<float4> Raw : register(u0);
RWTexture2D<float4> Normals : register(u1);
RWTexture2D<float4> Surfaces : register(u2);
RWTexture2D<float4> NormalRoughness : register(u3);
RWTexture2D<float4> Diffuse : register(u4);
RWTexture2D<float4> Specular : register(u5);
RWTexture2D<float4> Albedo : register(u6);
RWTexture2D<float4> Unfiltered : register(u7); // Emission, sky and deterministic primary direct lighting.
RWTexture2D<float4> SpecularGuide : register(u8); // RR: integrated reflectance RGB, deterministic mirror hit distance A.
RWTexture2D<float4> DiffuseSh : register(u13);
RWTexture2D<float4> SpecularSh : register(u14);
RWTexture2D<float4> GlassSurface : register(u15); // Closest glass position; w=-(ray distance+1), sky=-1.
RWTexture2D<float4> DiffuseFactor : register(u16);
RWTexture2D<float4> SpecularFactor : register(u17);
RWTexture2D<float4> GlassFallback : register(u18); // Contribution without a denoised opaque-cache lookup.
RWTexture2D<float4> GlassNormal : register(u19); // World normal + closest glass instance ID (one-based).

float Random(inout uint state) {
    uint old = state; state = old * 747796405u + 2891336453u;
    uint word = ((old >> ((old >> 28) + 4)) ^ old) * 277803737u;
    return min(((word >> 22) ^ word) * (1.0 / 4294967296.0), 0.99999994);
}
float3 SafeNormalize(float3 v, float3 fallback) { return dot(v,v) > 1e-20 ? normalize(v) : fallback; }
float2 UV(Binding b, float4 uv) {
    float2 value = (b.info.y == 0 ? uv.xy : uv.zw) * b.transform.zw;
    float s, c; sincos(b.extra.x, s, c);
    return float2(c * value.x - s * value.y, s * value.x + c * value.y) + b.transform.xy;
}
float4 Sample(Binding b, float4 uv, float cone) {
    if (b.info.x < 0) return 1;
    uint index = NonUniformResourceIndex((uint)b.info.x), w, h;
    Textures[index].GetDimensions(w, h);
    float lod = clamp(log2(max(cone * max(w,h) * max(abs(b.transform.z),abs(b.transform.w)), 1)), 0, b.extra.z);
    return Textures[index].SampleLevel(Samplers[index], UV(b,uv), lod);
}
float Footprint(float2 a,float2 b,float2 c,float3 e1,float3 e2,float cone) {
    float2 u=b-a,v=c-a;
    return cone*sqrt(abs(u.x*v.y-u.y*v.x)/max(length(cross(e1,e2)),1e-20));
}
void Triangle(uint instance, uint primitive, out Instance node, out Vertex a, out Vertex b, out Vertex c) {
    node = Instances[instance]; uint offset = node.mesh.y + primitive * 3;
    a = Vertices[node.mesh.x + Indices[offset]]; b = Vertices[node.mesh.x + Indices[offset+1]]; c = Vertices[node.mesh.x + Indices[offset+2]];
}
Surface Evaluate(uint instance, uint primitive, float2 bary, float cone, float3 view) {
    Instance node; Vertex a,b,c; Triangle(instance,primitive,node,a,b,c);
    Material material = Materials[node.mesh.z]; float3 weights = float3(1-bary.x-bary.y,bary);
    float3 p0 = mul(a.position,node.world).xyz, e1 = mul(b.position,node.world).xyz-p0, e2 = mul(c.position,node.world).xyz-p0;
    float3 localG = SafeNormalize(cross(b.position.xyz-a.position.xyz,c.position.xyz-a.position.xyz),float3(0,1,0));
    float3 g = SafeNormalize(mul(float4(localG,0),node.normal).xyz,float3(0,1,0));
    float3 n = SafeNormalize(mul(float4(a.normal.xyz*weights.x+b.normal.xyz*weights.y+c.normal.xyz*weights.z,0),node.normal).xyz,g);
    bool front=dot(g,view)>=0;
    if (!front) { g=-g; n=-n; }
    float4 uv=a.uv*weights.x+b.uv*weights.y+c.uv*weights.z;
    // Convert the world-space ray cone to UV space; model scale must not change texture sharpness.
    cone/=max(abs(dot(g,view)),.1);
    float2 footprint=float2(Footprint(a.uv.xy,b.uv.xy,c.uv.xy,e1,e2,cone),Footprint(a.uv.zw,b.uv.zw,c.uv.zw,e1,e2,cone));
    if (material.normalMap.info.x>=0) {
        float4 tangent=a.tangent*weights.x+b.tangent*weights.y+c.tangent*weights.z;
        float3 t, bitangent;
        if (dot(tangent.xyz,tangent.xyz)>1e-10) {
            t=mul(float4(tangent.xyz,0),node.world).xyz; t=SafeNormalize(t-n*dot(n,t),float3(1,0,0));
            float sign=determinant((float3x3)node.world)<0?-1:1;
            bitangent=cross(n,t)*(tangent.w<0?-1:1)*sign;
        } else {
            float2 d1=UV(material.normalMap,b.uv)-UV(material.normalMap,a.uv),d2=UV(material.normalMap,c.uv)-UV(material.normalMap,a.uv);
            float det=d1.x*d2.y-d1.y*d2.x;
            t=abs(det)>1e-10?(e1*d2.y-e2*d1.y)/det:cross(abs(n.y)<.99?float3(0,1,0):float3(1,0,0),n);
            t=SafeNormalize(t-n*dot(n,t),float3(1,0,0)); bitangent=cross(n,t)*(det<0?-1:1);
        }
        float3 mapped=Sample(material.normalMap,uv,footprint[(uint)material.normalMap.info.y]).xyz*2-1; mapped.xy*=material.factors.z;
        n=SafeNormalize(t*mapped.x+bitangent*mapped.y+n*mapped.z,n);
        if(dot(n,g)<=0) n=g;
    }
    float4 base=material.baseColor*Sample(material.baseMap,uv,footprint[(uint)material.baseMap.info.y])*(a.color*weights.x+b.color*weights.y+c.color*weights.z);
    float4 mr=Sample(material.mrMap,uv,footprint[(uint)material.mrMap.info.y]);
    Surface result;
    result.p=p0+e1*bary.x+e2*bary.y; result.n=n; result.g=g; result.base=max(base.rgb,0); result.alpha=saturate(base.a);
    result.metallic=saturate(material.factors.x*mr.b); result.roughness=clamp(material.factors.y*mr.g,.025,1);
    result.emission=material.emissive.rgb*Sample(material.emissiveMap,uv,footprint[(uint)material.emissiveMap.info.y]).rgb;
    result.ao=lerp(1,Sample(material.aoMap,uv,footprint[(uint)material.aoMap.info.y]).r,material.factors.w); result.material=node.mesh.z;
    result.transmission=saturate(material.transmission.x*Sample(material.transmissionMap,uv,footprint[(uint)material.transmissionMap.info.y]).r)*(1-result.metallic);
    result.ior=max(material.transmission.y,1); result.front=front;
    result.thickness=max(material.transmission.z*Sample(material.thicknessMap,uv,footprint[(uint)material.thicknessMap.info.y]).g,0);
    result.causticCaster=node.mesh.w;
    return result;
}
[shader("anyhit")]
void AnyHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    Instance node; Vertex a,b,c; Triangle(InstanceID(),PrimitiveIndex(),node,a,b,c);
    Material material=Materials[node.mesh.z];
    // The first shadow query ignores transmitting surfaces and terminates on any opaque blocker.
    if((payload.transparency&4)!=0 && material.transmission.x>0) { IgnoreHit(); return; }
    // NRD's primary query must keep opaque texels and metals in a transmitting material.
    if((payload.transparency&16)!=0 && material.transmission.x>0) {
        Surface s=Evaluate(InstanceID(),PrimitiveIndex(),attr.barycentrics,
            RayTCurrent()*CameraForward.w*2/Size.y,-WorldRayDirection());
        if(s.transmission>0) { IgnoreHit(); return; }
    }
    if((payload.transparency&8)!=0 && material.transmission.x<=0) { IgnoreHit(); return; }
    float3 normal=mul(float4(cross(b.position.xyz-a.position.xyz,c.position.xyz-a.position.xyz),0),node.normal).xyz;
    // Volume exit faces must remain intersectable even on single-sided glTF materials.
    if (material.flags.z==0 && material.transmission.x==0 && dot(normal,WorldRayDirection())>=0) { IgnoreHit(); return; }
    if(material.flags.x==0) return;
    if(material.flags.x==2) payload.transparency|=1;
    float3 w=float3(1-attr.barycentrics.x-attr.barycentrics.y,attr.barycentrics);
    float alpha=material.baseColor.a*Sample(material.baseMap,a.uv*w.x+b.uv*w.y+c.uv*w.z,0).a*(a.color.a*w.x+b.color.a*w.y+c.color.a*w.z);
    if((material.flags.x==1 && alpha<material.flags.y)||(material.flags.x==2 &&
        ((payload.transparency&2)!=0 ? alpha<=0 : Random(payload.random)>=alpha))) IgnoreHit();
}
[shader("closesthit")]
void ClosestHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    payload.t=RayTCurrent(); payload.instance=InstanceID(); payload.primitive=PrimitiveIndex(); payload.bary=attr.barycentrics;
}
[shader("miss")]
void Miss(inout Payload payload) { payload.instance=0xffffffff; }
Payload Trace(float3 origin,float3 direction,float maximum,bool shadow,inout uint rng,bool guide=false,bool ignoreGlass=false) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=max(maximum,Limits.x*2);
    Payload hit; hit.t=ray.TMax; hit.instance=0xffffffff; hit.primitive=0; hit.bary=0; hit.random=rng; hit.transparency=(guide?2:0)|(shadow?4:0)|(ignoreGlass?16:0);
#if DXR_USE_SER
    dx::HitObject object=dx::HitObject::TraceRay(Scene,shadow?RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH:RAY_FLAG_NONE,255,0,0,0,ray,hit);
    dx::MaybeReorderThread(object);
    if(shadow) {
        // Visibility only needs hit/miss and the any-hit transparency bit.
        // Skipping miss/closest-hit avoids a shader-table round trip for every shadow ray.
        if(object.IsHit()) { hit.instance=0; hit.t=object.GetRayTCurrent(); }
    } else if(object.IsHit()) {
        dx::HitObject::Invoke(object,hit);
    }
#else
    TraceRay(Scene,shadow?RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH:RAY_FLAG_NONE,255,0,0,0,ray,hit);
#endif
    rng=hit.random; return hit;
}
float3 Offset(Surface s,float3 direction) {
    float e=max(Limits.x,max(max(abs(s.p.x),abs(s.p.y)),abs(s.p.z))*1e-6);
    return s.p+s.g*(dot(s.g,direction)>=0?e:-e);
}
float3 Sky(float3 direction) { return lerp(float3(.45,.55,.7),float3(.1,.2,.4),saturate(direction.y))*Environment.x; }
float Luminance(float3 value) { return dot(value,float3(.2126,.7152,.0722)); }
float3 F0(Surface s) { float dielectric=(s.ior-1)/(s.ior+1); return lerp((dielectric*dielectric).xxx,s.base,s.metallic); }
float Fresnel(float cosine,float eta) {
    float sin2=eta*eta*max(0,1-cosine*cosine); if(sin2>=1) return 1;
    float ct=sqrt(1-sin2),rs=(eta*cosine-ct)/max(eta*cosine+ct,1e-7),rp=(cosine-eta*ct)/max(cosine+eta*ct,1e-7);
    return saturate(.5*(rs*rs+rp*rp));
}
float3 Absorption(Material m) { return m.attenuation.w>0 ? -log(max(m.attenuation.rgb,1e-6))/m.attenuation.w : 0; }
float SpecProbability(Surface s) {
    if(s.metallic>.999) return 1;
    float reflection=Luminance(F0(s));
    return clamp(reflection/(reflection+Luminance(s.base)*(1-s.metallic)+1e-6),.25,.75);
}
float G1(float cosine,float a2) { return 2*cosine/max(cosine+sqrt(a2+(1-a2)*cosine*cosine),1e-6); }
void Bsdf(Surface s,float3 v,float3 l,out float3 diffuse,out float3 specular,out float pdf) {
    diffuse=specular=0; pdf=0; float nv=saturate(dot(s.n,v)),nl=saturate(dot(s.n,l));
    if(nv<=0||nl<=0||dot(s.g,l)<=0) return;
    float3 h=SafeNormalize(v+l,s.n); float nh=saturate(dot(s.n,h)),vh=saturate(dot(v,h));
    float a=s.roughness*s.roughness,a2=a*a,den=nh*nh*(a2-1)+1;
    float d=a2/(PI*den*den); float3 f0=F0(s),f=f0+(1-f0)*pow(1-vh,5);
    specular=d*G1(nv,a2)*G1(nl,a2)*f/max(4*nv*nl,1e-6);
    diffuse=(1-f)*s.base*(1-s.metallic)*(1-s.transmission)/PI;
    float probability=SpecProbability(s); pdf=(1-probability)*nl/PI+probability*d*nh/max(4*vh,1e-6);
}
float3 Local(float3 n,float3 v) {
    float3 t=normalize(cross(abs(n.y)<.99?float3(0,1,0):float3(1,0,0),n)); return t*v.x+cross(n,t)*v.y+n*v.z;
}
float Weight(float a,float b) { a*=a; b*=b; return a/max(a+b,1e-20); }
// Opaque blockers use first-hit termination, without material/normal-map evaluation.
// Thick refractors lit by analytic lights are handled by the photon pass.
Payload TraceGlass(float3 origin,float3 direction,float maximum,inout uint rng) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=maximum;
    Payload hit=(Payload)0; hit.t=maximum; hit.instance=0xffffffff; hit.random=rng; hit.transparency=8;
#if DXR_USE_SER
    dx::HitObject object=dx::HitObject::TraceRay(Scene,RAY_FLAG_NONE,255,0,0,0,ray,hit);
    dx::MaybeReorderThread(object);
    if(object.IsHit()) dx::HitObject::Invoke(object,hit);
#else
    TraceRay(Scene,RAY_FLAG_NONE,255,0,0,0,ray,hit);
#endif
    rng=hit.random; return hit;
}
float3 Visibility(float3 origin,float3 direction,float maximum,inout uint rng,out bool stochastic,bool analytic=false) {
    float3 visibility=1; stochastic=false;
    if(maximum<=Limits.x*2) return 0;
    Payload blocker=Trace(origin,direction,maximum,true,rng);
    stochastic=(blocker.transparency&1)!=0;
    if(blocker.instance!=0xffffffff) return 0;
    if(Limits.w==0) return 1;
    for(uint layer=0;layer<24;layer++) {
        // Alpha coverage was already sampled by the opaque query. Skip it on the ordered glass query.
        Payload hit=TraceGlass(origin,direction,maximum,rng);
        stochastic=stochastic||(hit.transparency&1)!=0;
        if(hit.instance==0xffffffff) return visibility;
        Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,0,-direction);
        if(s.transmission<=0) return 0;
        // Only focusing geometry delegates transmission to the photon map. Parallel panes retain
        // continuous analytic lighting instead of replacing it with isolated photon footprints.
        if(analytic && CausticSettings.x>0 && s.causticCaster!=0 && s.thickness>0 && s.ior>1.001) return 0;
        float f=Fresnel(saturate(dot(s.g,-direction)),s.front!=0?1/s.ior:s.ior);
        visibility*=s.transmission*(1-f);
        if(s.thickness==0) visibility*=s.base;
        else if(s.front!=0) visibility*=exp(-Absorption(Materials[s.material])*s.thickness/max(abs(dot(s.g,direction)),.05));
        if(max(max(visibility.x,visibility.y),visibility.z)<1e-5) return 0;
        maximum-=hit.t; if(maximum<=Limits.x*4) return visibility;
        origin=Offset(s,direction);
    }
    return 0;
}
void DirectClassic(Surface s,float3 v,inout uint rng,out float3 diffuse,out float3 specular,out float3 unfiltered,out float3 momentD,out float3 momentS) {
    diffuse=specular=unfiltered=momentD=momentS=0;
    float3 factorD,factorS; NRD_MaterialFactors(s.n,v,s.base*(1-s.metallic)*(1-s.transmission),F0(s),s.roughness,factorD,factorS);
    // Evaluate analytic lights deterministically. Their sharp shadows must not be blurred with
    // the indirect lobe hit distance. Stochastic transparent shadows still require denoising.
    uint lightCount=(uint)Control.w+(SunRadiance.x>0?1:0);
    for(uint lightIndex=0;lightIndex<lightCount;lightIndex++) {
        Light light;
        if(lightIndex<(uint)Control.w) light=Lights[lightIndex];
        else { light.positionType=0; light.directionRange=float4(-SunDirection.xyz,0); light.colorIntensity=float4(SunRadiance.rgb,1); light.spot=0; }
        float3 l=-light.directionRange.xyz; float distance=CameraRight.w,attenuation=1;
        if(light.positionType.w!=0) {
            l=light.positionType.xyz-s.p; distance=length(l); l/=max(distance,1e-6); attenuation=1/max(distance*distance,1e-8);
            if(light.directionRange.w>0) attenuation*=saturate(1-pow(distance/light.directionRange.w,4));
            if(light.positionType.w==2) { float cone=saturate((dot(-l,light.directionRange.xyz)-light.spot.y)/max(light.spot.x-light.spot.y,1e-6)); attenuation*=cone*cone; }
        }
        float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf); float nl=saturate(dot(s.n,l));
        if(nl>0 && attenuation>0 && any(d+r>0)) {
            bool stochastic; float3 visibility=Visibility(Offset(s,l),l,distance-Limits.x*4,rng,stochastic,true);
            if(any(visibility>0)) {
                float3 energy=light.colorIntensity.rgb*light.colorIntensity.w*attenuation*nl*visibility;
                if(stochastic || Materials[s.material].flags.x==2) {
                    diffuse+=d*energy; specular+=r*energy;
                    momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
                }
                else unfiltered+=(d+r)*energy;
            }
        }
    }
    if(Environment.y>0) {
        float choice=Random(rng); uint lo=0,hi=(uint)Environment.y-1;
        while(lo<hi) { uint mid=(lo+hi)/2; if(Emitters[mid].meta.y<choice) lo=mid+1; else hi=mid; }
        Emitter emitter=Emitters[lo]; float u=sqrt(Random(rng)); float2 bary=float2(u*(1-Random(rng)),0); bary.y=u-bary.x;
        float3 position=emitter.p0.xyz+emitter.e1.xyz*bary.x+emitter.e2.xyz*bary.y;
        float3 delta=position-s.p; float dist=length(delta); float3 l=delta/max(dist,1e-6);
        Surface lamp=Evaluate((uint)emitter.meta.z,(uint)emitter.meta.w,bary,0,-l);
        float cosine=abs(dot(lamp.g,-l)); Material lm=Materials[lamp.material];
        // Evaluate orients normals; use original geometric normal for one-sided emission.
        Instance node=Instances[(uint)emitter.meta.z]; Vertex va,vb,vc; Triangle((uint)emitter.meta.z,(uint)emitter.meta.w,node,va,vb,vc);
        float3 front=normalize(mul(float4(cross(vb.position.xyz-va.position.xyz,vc.position.xyz-va.position.xyz),0),node.normal).xyz);
        if(lm.flags.z!=0||dot(front,-l)>0) {
            float lightPdf=dist*dist/max(Environment.z*cosine,1e-10); float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf);
            bool stochastic; float3 visibility=0;
            if(cosine>0 && any(d+r>0)) visibility=Visibility(Offset(s,l),l,dist-max(Limits.x*8,dist*1e-5),rng,stochastic);
            if(cosine>0 && dot(s.n,l)>0 && any(visibility>0)) {
                float3 energy=lamp.emission*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/max(lightPdf,1e-10)*visibility;
                if(lm.flags.x==1 && lamp.alpha<lm.flags.y) energy=0; if(lm.flags.x==2) energy*=lamp.alpha;
                diffuse+=d*energy; specular+=r*energy;
                momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
            }
        }
    }
    if(Environment.x>0) {
        float z=1-2*Random(rng),phi=2*PI*Random(rng),radius=sqrt(max(0,1-z*z)); float3 l=float3(radius*cos(phi),z,radius*sin(phi));
        float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf); float lightPdf=1/(4*PI);
        bool stochastic; float3 visibility=0;
        if(any(d+r>0)) visibility=Visibility(Offset(s,l),l,CameraRight.w,rng,stochastic);
        if(dot(s.n,l)>0 && any(visibility>0)) {
            // AO affects only environment illumination, never punctual lights.
            float3 energy=Sky(l)*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/lightPdf*s.ao*visibility; diffuse+=d*energy; specular+=r*energy;
            momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
        }
    }
}
#include "SceneLighting.hlsli"

// Current-frame reprojection is a cache lookup, never a temporal filter for glass.
// Reject taps on another surface; an off-screen or occluded hit continues path tracing.
bool SampleOpaque(Surface s,float3 view,out float3 radiance) {
    float3 delta=s.p-CameraOrigin.xyz; float z=dot(delta,CameraForward.xyz);
    float2 ndc=float2(dot(delta,CameraRight.xyz)/(CameraForward.w*(Size.x/Size.y)),-dot(delta,CameraUp.xyz)/CameraForward.w)/max(z,1e-8);
    float2 position=(ndc*.5+.5)*Size.xy-Size.zw-.5;
    int2 base=(int2)floor(position); float2 fraction=frac(position); float weights=0; radiance=0;
    float footprint=max(z*CameraForward.w*2/Size.y,Limits.x*8);
    [unroll] for(int y=0;y<2;y++) [unroll] for(int x=0;x<2;x++) {
        int2 q=base+int2(x,y); if(z<=0 || any(q<0) || any(q>=(int2)Size.xy)) continue;
        float4 surface=OpaqueSurface[q],normalRoughness=OpaqueNormalRoughness[q];
        float3 normal=normalize(normalRoughness.xyz),offset=surface.xyz-s.p;
        if(surface.w<0 || dot(normal,s.n)<.9 || abs(dot(offset,s.g))>footprint*.5 || length(offset)>footprint*3) continue;
        // A glossy cache value is valid only near the original outgoing direction.
        float3 originalView=normalize(CameraOrigin.xyz-surface.xyz);
        if(dot(originalView,view)<lerp(.999,.6,s.roughness)) continue;
        float weight=(x==0?1-fraction.x:fraction.x)*(y==0?1-fraction.y:fraction.y);
        radiance+=OpaqueColor[q].rgb*weight; weights+=weight;
    }
    radiance/=max(weights,1e-8); return weights>.05;
}

NrcSample NrcMakeSample(Surface s,float3 view,float3 target) {
    NrcSample sample=(NrcSample)0;
    float3 normalized=saturate((s.p-NeuralBounds.xyz)/max(NeuralSettings.y,1e-3));
    sample.position=float4(normalized,1);
    sample.normal=float4(s.n,s.roughness);
    sample.view=float4(view,0);
    sample.albedo=float4(s.base*(1-s.metallic)*(1-s.transmission),s.ao);
    sample.target=float4(max(target,0),1);
    return sample;
}
bool NrcCanUse(Surface s) {
    return NeuralSettings.x!=0 && s.transmission<=0 && s.metallic<=.95 && s.roughness>.03;
}
bool NrcLookup(Surface s,float3 view,out float3 radiance) {
    radiance=0;
    if(!NrcCanUse(s)) return false;
    NrcSample sample=NrcMakeSample(s,view,0);
    uint cell=NrcCell(sample);
    if(NrcCoverage[cell]<4) return false;
    radiance=NrcPredict(sample);
    return all(isfinite(radiance)) && any(radiance>1e-5);
}
void NrcStore(Surface s,float3 view,float3 target) {
    if(!NrcCanUse(s) || !any(target>1e-5)) return;
    uint2 pixel=DispatchRaysIndex().xy;
    uint width=max((uint)Size.x,1), height=max((uint)Size.y,1);
    uint total=width*height, stride=max(total/NRC_MAX_SAMPLES,1);
    uint linearIndex=pixel.y*width+pixel.x;
    if(linearIndex%stride!=0) return;
    uint pixelIndex=linearIndex/stride;
    if(pixelIndex>=NRC_MAX_SAMPLES) return;
    NrcSamples[pixelIndex]=NrcMakeSample(s,view,target);
}

void AccumulatePath(float3 value,int lobe,float3 direction,float3 factorD,float3 factorS,
    inout float3 sumD,inout float3 sumS,inout float3 momentD,inout float3 momentS) {
    if(lobe==0) { sumD+=value; momentD+=direction*_NRD_Luminance(value/factorD); }
    else { sumS+=value; momentS+=direction*_NRD_Luminance(value/factorS); }
}

void RenderPixel(bool transparentPass) {
    uint2 pixel=DispatchRaysIndex().xy;
    if(!transparentPass) ReservoirsOut[pixel.y*(uint)Size.x+pixel.x]=(Reservoir)0;
    uint rng=pixel.x*73856093+pixel.y*19349663+(uint)Control.z*83492791+12345;
    float2 ndc=(float2(pixel)+.5+Size.zw)/Size.xy*2-1;
    float3 primary=normalize(CameraForward.xyz+CameraRight.xyz*ndc.x*(Size.x/Size.y)*CameraForward.w-CameraUp.xyz*ndc.y*CameraForward.w);
    float3 sumD=0,sumS=0,sumE=0,primaryAlbedo=0,primaryNormal=float3(0,0,1),specularAlbedo=0,primaryCausticD=0,primaryCausticS=0; float roughness=1,reactive=0,mirrorDistance=0;
    float3 factorD=1,factorS=1,momentD=0,momentS=0,causticMomentD=0,causticMomentS=0;
    float4 primarySurface=float4(primary,-1); uint primaryMaterial=0;
    // A separate deterministic guide ray prevents alpha coverage from changing the G-buffer every frame.
    uint guideRng=pixel.x*73856093+pixel.y*19349663+12345;
    bool opaqueForNrd=!transparentPass && CameraUp.w!=0;
    Payload guide=Trace(CameraOrigin.xyz,primary,CameraRight.w,false,guideRng,true,opaqueForNrd);
    bool coverage=(guide.transparency&1)!=0;
    bool glass=false;
    if(guide.instance!=0xffffffff) {
        Surface g=Evaluate(guide.instance,guide.primitive,guide.bary,guide.t*CameraForward.w*2/Size.y,-primary);
        glass=g.transmission>0;
        if(!coverage) GatherCaustics(g,-primary,primaryCausticD,primaryCausticS,causticMomentD,causticMomentS);
        primarySurface=float4(g.p,guide.t); primaryNormal=g.n; primaryAlbedo=g.base*(1-g.metallic)*(1-g.transmission);
        roughness=g.roughness; primaryMaterial=g.material; reactive=coverage||g.transmission>0?1:0;
        NRD_MaterialFactors(g.n,-primary,primaryAlbedo,F0(g),roughness,factorD,factorS);
        // Split-sum environment BRDF approximation (linear material reflectance, never lit radiance).
        float4 c0=float4(-1,-.0275,-.572,.022),c1=float4(1,.0425,1.04,-.04);
        float4 r=g.roughness*c0+c1; float a004=min(r.x*r.x,exp2(-9.28*saturate(dot(g.n,-primary))))*r.x+r.y;
        float2 ab=float2(-1.04,1.04)*a004+r.zw; specularAlbedo=max(F0(g)*ab.x+ab.y,0);
        if(Limits.z!=0) {
            float3 mirror=reflect(primary,g.n); Payload reflected=Trace(Offset(g,mirror),mirror,CameraRight.w,false,guideRng);
            mirrorDistance=reflected.instance==0xffffffff?CameraRight.w:reflected.t;
        }
    }
    if(transparentPass && !glass) {
        Raw[pixel]=OpaqueColor[pixel]; GlassSurface[pixel]=OpaqueSurface[pixel];
        GlassFallback[pixel]=0; GlassNormal[pixel]=0; return;
    }
    float3 cachedSum=0;
    float hitD=0,hitS=NRD_FrontEnd_SpecHitDistAveraging_Begin(),countD=0;
    uint pathCount=(uint)Control.x*(transparentPass?2u:1u);
    for(uint sample=0;sample<pathCount;sample++) {
        float3 ro=CameraOrigin.xyz,rd=primary,throughput=1; float previousPdf=0,secondaryDistance=0; int firstLobe=-1;
        float3 firstDirection=0;
        bool previousTransmission=false,hitDistanceRecorded=false;
        // Bounded nesting: medium IOR and Beer-Lambert absorption coefficient, air in slot zero.
        float4 media[8]; media[0]=float4(1,0,0,0); uint medium=0;
        for(uint bounce=0;bounce<(uint)Control.y;bounce++) {
            Payload hit;
            if(bounce==0 && !coverage) hit=guide; else hit=Trace(ro,rd,CameraRight.w,false,rng,false,bounce==0 && opaqueForNrd);
            if(bounce==0) reactive=max(reactive,(float)(hit.transparency&1));
            if(bounce>0 && !hitDistanceRecorded) secondaryDistance+=hit.instance==0xffffffff?65504:min(hit.t,65504);
            if(hit.instance==0xffffffff) {
                if(bounce>0 && !hitDistanceRecorded) { if(firstLobe==0) { hitD+=min(secondaryDistance,65504); countD++; } else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,min(secondaryDistance,65504)); }
                float3 value=throughput*Sky(rd)*(bounce==0||previousTransmission?1:Weight(previousPdf,1/(4*PI)));
                if(bounce==0) sumE+=value; else AccumulatePath(value,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS); break;
            }
            float cone=hit.t*CameraForward.w*2/Size.y;
            Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,cone,-rd); Material m=Materials[s.material];
            // A pane's back face is not the reflected/transmitted object used for the virtual hit distance.
            if(bounce>0 && !hitDistanceRecorded && !(previousTransmission && s.transmission>0)) {
                if(firstLobe==0) { hitD+=min(secondaryDistance,65504); countD++; } else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,min(secondaryDistance,65504));
                hitDistanceRecorded=true;
            }
            if(medium>0) throughput*=exp(-media[medium].yzw*hit.t);
            else if(s.front==0 && s.transmission>0 && s.thickness>0) throughput*=exp(-Absorption(m)*hit.t); // Camera starts inside glass.
            float3 cached;
            if(transparentPass && bounce>0 && s.transmission==0 && SampleOpaque(s,-rd,cached)) {
                float3 value=throughput*cached; cachedSum+=value;
                AccumulatePath(value,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS); break;
            }
            float emissionWeight=bounce==0||previousTransmission||Environment.z<=0?1:Weight(previousPdf,hit.t*hit.t/max(Environment.z*abs(dot(s.g,-rd)),1e-10));
            float3 radiance=s.emission*emissionWeight;
            if(m.flags.w!=0) radiance+=s.base;
            if(bounce==0) sumE+=radiance; else AccumulatePath(throughput*radiance,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS);
            if(m.flags.w!=0) break;
            float3 directD,directS,unfiltered,directMomentD,directMomentS;
            Direct(s,-rd,rng,!transparentPass && bounce==0 && sample==0 && !coverage && s.transmission==0,pixel,directD,directS,unfiltered,directMomentD,directMomentS);
            float3 causticD=primaryCausticD,causticS=primaryCausticS;
            float3 cmD=causticMomentD,cmS=causticMomentS;
            if(bounce>0 || coverage) GatherCaustics(s,-rd,causticD,causticS,cmD,cmS);
            directD+=causticD; directS+=causticS;
            if(bounce==0) {
                sumD+=directD; sumS+=directS; sumE+=unfiltered; momentD+=directMomentD+cmD; momentS+=directMomentS+cmS;
            }
            else {
                float3 localRadiance=directD+directS+unfiltered;
                float3 predicted;
                if(NrcLookup(s,-rd,predicted)) {
                    AccumulatePath(throughput*predicted,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS);
                    break;
                }
                AccumulatePath(throughput*localRadiance,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS);
                // One training target per pixel is enough: the update pass averages
                // the batch and keeps the local path contribution as the teacher.
                if(sample==0) NrcStore(s,-rd,localRadiance);
            }
            if(s.transmission>0) {
                float u=Random(rng),phi=2*PI*Random(rng),a=s.roughness*s.roughness;
                float ct=sqrt((1-u)/(1+(a*a-1)*u)),st=sqrt(max(0,1-ct*ct));
                float3 micro=Local(s.n,float3(st*cos(phi),st*sin(phi),ct));
                float nv=max(dot(s.n,-rd),1e-5),vm=max(dot(-rd,micro),0),nm=max(dot(s.n,micro),1e-5);
                if(vm<=0) break;
                bool volume=s.thickness>0;
                float etaI=medium>0?media[medium].x:(s.front==0&&volume?s.ior:1);
                float etaT=s.front!=0?s.ior:(medium>1?media[medium-1].x:1);
                float eta=etaI/max(etaT,1);
                float fresnel=Fresnel(vm,volume?eta:1/s.ior);
                float transProbability=(1-fresnel)*s.transmission;
                float pick=Random(rng); float3 next; bool transmitted=false;
                // Trace both first-event lobes, as in NRD-Sample, instead of flickering between them.
                if(transparentPass && bounce==0 && s.transmission>=.999) {
                    bool reflection=(sample&1u)==0;
                    pick=reflection?0:1;
                    throughput*=2*(reflection?fresnel:transProbability);
                    if(!reflection) pick=fresnel+transProbability*.5;
                }
                if(pick<fresnel) next=reflect(rd,micro);
                else if(pick<fresnel+transProbability) {
                    next=volume?refract(rd,micro,eta):-reflect(rd,micro); transmitted=true;
                    if(dot(next,next)<1e-10) { next=reflect(rd,micro); transmitted=false; }
                } else {
                    float rr=sqrt(Random(rng)),angle=2*PI*Random(rng);
                    next=Local(s.n,float3(rr*cos(angle),rr*sin(angle),sqrt(1-rr*rr)));
                    throughput*=s.base;
                    if(bounce==0) { firstLobe=0; firstDirection=next; }
                    previousPdf=max(dot(s.n,next),0)/PI; previousTransmission=false;
                    ro=Offset(s,next); rd=next; continue;
                }
                if((dot(s.g,next)<0)!=transmitted) break;
                float nl=abs(dot(s.n,next));
                throughput*=G1(nv,a*a)*G1(nl,a*a)*vm/max(nv*nm,1e-5);
                if(transmitted) {
                    if(volume) {
                        throughput*=eta*eta;
                        if(s.front!=0) { if(medium==7) break; media[++medium]=float4(s.ior,Absorption(m)); }
                        else if(medium>0) medium--;
                    } else throughput*=s.base;
                }
                if(bounce==0) { firstLobe=1; firstDirection=next; }
                // Direct light sampling does not sample BTDF directions; transmitted emitter hits need full weight.
                float den=nm*nm*(a*a-1)+1;
                previousTransmission=transmitted; previousPdf=fresnel*a*a*nm/max(4*PI*den*den*vm,1e-10);
                ro=Offset(s,next); rd=SafeNormalize(next,rd);
                continue;
            }
            previousTransmission=false;
            float selection=Random(rng);
            if(bounce==0 && opaqueForNrd) {
                static const uint bayer[16]={0,8,2,10,12,4,14,6,3,11,1,9,15,7,13,5};
                selection=frac((bayer[(pixel.y&3)*4+(pixel.x&3)]+.5)/16.0+frac((Control.z+sample)*.61803398875));
            }
            bool spec=selection<SpecProbability(s); float u=Random(rng),phi=2*PI*Random(rng); float3 next;
            if(spec) { float a=s.roughness*s.roughness,ct=sqrt((1-u)/(1+(a*a-1)*u)),st=sqrt(max(0,1-ct*ct)); next=reflect(rd,Local(s.n,float3(st*cos(phi),st*sin(phi),ct))); }
            else { float r=sqrt(u); next=Local(s.n,float3(r*cos(phi),r*sin(phi),sqrt(1-u))); }
            float3 d,r; float pdf; Bsdf(s,-rd,next,d,r,pdf);
            // Split the primary mixture into physically distinct lobe signals with their own proposal PDFs.
            if(bounce==0) {
                firstLobe=spec?1:0;
                firstDirection=next;
                float p=SpecProbability(s),nl=saturate(dot(s.n,next));
                float3 h=SafeNormalize(next-rd,s.n); float nh=saturate(dot(s.n,h)),vh=saturate(dot(-rd,h));
                float a=s.roughness*s.roughness,a2=a*a,den=nh*nh*(a2-1)+1;
                float lobePdf=spec?p*a2*nh/max(PI*den*den*4*vh,1e-10):(1-p)*nl/PI;
                throughput=(spec?r:d)*nl/max(lobePdf,1e-10);
            } else throughput*=(d+r)*saturate(dot(s.n,next))/max(pdf,1e-10);
            previousPdf=pdf; ro=Offset(s,next); rd=next;
            if(Control.y==1 && bounce==0) {
                Payload secondary=Trace(ro,rd,CameraRight.w,false,rng); float distance=secondary.instance==0xffffffff?65504:min(secondary.t,65504);
                if(firstLobe==0) { hitD+=distance; countD++; } else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,distance);
            }
            if(pdf<=0||max(max(throughput.r,throughput.g),throughput.b)<=0) break;
            if(bounce>=2) { float survival=clamp(max(max(throughput.r,throughput.g),throughput.b),.05,.95); if(Random(rng)>survival) break; throughput/=survival; }
        }
    }
    float inv=1.0/pathCount; sumD*=inv; sumS*=inv; sumE*=inv; momentD*=inv; momentS*=inv;
    float3 total=sumD+sumS+sumE;
    // Legacy BLEND may reveal emissive/direct-lit backgrounds stochastically: none of that may bypass denoising.
    if(coverage) { sumS=total; sumD=sumE=0; }
    Raw[pixel]=float4(all(isfinite(total))?max(total,0):0,1);
    if(transparentPass) {
        GlassSurface[pixel]=float4(primarySurface.xyz,-(primarySurface.w+1));
        float3 fallback=max(total-cachedSum*inv,0);
        GlassFallback[pixel]=float4(all(isfinite(fallback))?fallback:0,1);
        GlassNormal[pixel]=float4(primaryNormal,guide.instance+1);
        return;
    }
    Normals[pixel]=float4(primaryNormal*.5+.5,min(primaryMaterial,254)/255.0);
    Surfaces[pixel]=primarySurface;
    NormalRoughness[pixel]=float4(primaryNormal/max(max(abs(primaryNormal.x),abs(primaryNormal.y)),abs(primaryNormal.z)),roughness);
    Diffuse[pixel]=float4(sumD/factorD,countD>0?hitD/countD:0);
    NRD_FrontEnd_SpecHitDistAveraging_End(hitS);
    Specular[pixel]=float4(sumS/factorS,hitS);
    DiffuseSh[pixel]=float4(coverage?0:momentD,0); SpecularSh[pixel]=float4(coverage?0:momentS,0);
    DiffuseFactor[pixel]=float4(factorD,1); SpecularFactor[pixel]=float4(factorS,1);
    GlassSurface[pixel]=primarySurface;
    GlassFallback[pixel]=0; GlassNormal[pixel]=0;
    Albedo[pixel]=float4(primaryAlbedo,reactive); Unfiltered[pixel]=float4(sumE,1);
    SpecularGuide[pixel]=float4(specularAlbedo,mirrorDistance);
}
[shader("raygeneration")]
void RayGen() { RenderPixel(false); }
[shader("raygeneration")]
void TransparentRayGen() { RenderPixel(true); }
