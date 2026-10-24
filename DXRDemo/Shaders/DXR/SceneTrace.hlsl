// Explicit scene ABI. C# layouts live in SceneAsset.cs and DxrSceneBackend.cs.
static const float PI = 3.14159265359;
struct Vertex { float4 position, normal, tangent, uv, color; };
struct Binding { float4 info, transform, extra; };
struct Material { float4 baseColor, emissive, factors, flags; Binding baseMap, mrMap, normalMap, aoMap, emissiveMap;
    float4 transmission, attenuation; Binding transmissionMap, thicknessMap; };
struct Instance { uint4 mesh; row_major float4x4 world, normal; float4 emissionSampling; };
struct Light { float4 positionType, directionRange, colorIntensity, spot; };
struct Emitter { float4 p0, e1, e2, meta; };
#if DXR_SHADER_MODEL_69
// SM 6.9 payload access qualifiers keep any-hit state small while allowing
// the SM 6.5 fallback and the optional SM 6.9 HitObject reordering variant.
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
struct Surface { float3 p, n, g, base, emission; float metallic, roughness, alpha, ao, transmission, ior, thickness; uint material, front, causticCaster, emitter; float emissionPdf, bsdfSampling, eta; };
cbuffer Frame : register(b0) {
    float4 CameraOrigin, CameraForward, CameraRight, CameraUp, Size, Control, Environment, Limits, SunDirection, SunRadiance;
    float4 PreviousOrigin, PreviousForward, PreviousRight, PreviousUp, PreviousSize;
    float4 Lighting, CausticBounds, CausticSettings, PathSettings, EnvironmentMap;
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

#include "PathSampling.hlsli"

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
    uint index = NonUniformResourceIndex((uint)b.info.x);
    float lod=0;
    if(cone>0) {
        float edge=b.extra.w;
        if(edge<=0) { uint w,h; Textures[index].GetDimensions(w,h); edge=max(w,h); }
        // Temporal SR reconstructs display-resolution texture detail from jittered
        // samples. Input-resolution LODs would discard it before denoising starts.
        lod=clamp(log2(max(cone*edge*max(abs(b.transform.z),abs(b.transform.w)),1))+EnvironmentMap.w,0,b.extra.z);
    }
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
    result.metallic=saturate(material.factors.x*mr.b); result.roughness=saturate(material.factors.y*mr.g);
    result.emission=material.emissive.rgb*Sample(material.emissiveMap,uv,footprint[(uint)material.emissiveMap.info.y]).rgb;
    result.ao=lerp(1,Sample(material.aoMap,uv,footprint[(uint)material.aoMap.info.y]).r,material.factors.w); result.material=node.mesh.z;
    result.transmission=saturate(material.transmission.x*Sample(material.transmissionMap,uv,footprint[(uint)material.transmissionMap.info.y]).r)*(1-result.metallic);
    result.ior=max(material.transmission.y,1); result.front=front;
    result.thickness=max(material.transmission.z*Sample(material.thicknessMap,uv,footprint[(uint)material.thicknessMap.info.y]).g,0);
    result.eta=front || result.thickness==0?1/result.ior:result.ior;
    result.causticCaster=node.mesh.w; result.emissionPdf=node.emissionSampling.x; result.bsdfSampling=1;
    result.emitter=node.emissionSampling.y>0?(uint)node.emissionSampling.y-1+primitive:0xffffffff;
    return result;
}
bool AcceptTriangle(uint instance,uint primitive,float2 bary,float distance,float3 direction,inout Payload payload) {
    Instance node=Instances[instance]; Material material=Materials[node.mesh.z];
    if((payload.transparency&4)!=0 && material.transmission.x>0) return false;
    if((payload.transparency&16)!=0 && material.transmission.x>0) {
        Surface s=Evaluate(instance,primitive,bary,distance*CameraForward.w*2/Size.y,-direction);
        if(s.transmission>0) return false;
    }
    if((payload.transparency&8)!=0 && material.transmission.x<=0) return false;
    if(material.flags.x==0) return true;
    if(material.flags.x==2) payload.transparency|=1;
    Vertex a,b,c; Triangle(instance,primitive,node,a,b,c);
    float3 w=float3(1-bary.x-bary.y,bary);
    float alpha=material.baseColor.a*Sample(material.baseMap,a.uv*w.x+b.uv*w.y+c.uv*w.z,0).a*
        (a.color.a*w.x+b.color.a*w.y+c.color.a*w.z);
    return material.flags.x==1?alpha>=material.flags.y:
        ((payload.transparency&2)!=0?alpha>0:Random(payload.random)<alpha);
}
[shader("anyhit")]
void AnyHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    if(!AcceptTriangle(InstanceID(),PrimitiveIndex(),attr.barycentrics,RayTCurrent(),WorldRayDirection(),payload)) IgnoreHit();
}
[shader("closesthit")]
void ClosestHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    payload.t=RayTCurrent(); payload.instance=InstanceID(); payload.primitive=PrimitiveIndex(); payload.bary=attr.barycentrics;
}
[shader("miss")]
void Miss(inout Payload payload) { payload.instance=0xffffffff; }
// Inline visibility avoids saving the entire path/shading state for a shader
// table call. TLAS mask 4 excludes transmission; those interfaces are evaluated
// in distance order by TraceGlass after the opaque test succeeds.
#if DXR_USE_SER
void TraceOcclusion(RayDesc ray,inout Payload hit) {
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH|RAY_FLAG_CULL_BACK_FACING_TRIANGLES|RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(Scene,RAY_FLAG_NONE,4,ray);
    while(query.Proceed()) {
        if(AcceptTriangle(query.CandidateInstanceID(),query.CandidatePrimitiveIndex(),
            query.CandidateTriangleBarycentrics(),query.CandidateTriangleRayT(),ray.Direction,hit))
            query.CommitNonOpaqueTriangleHit();
    }
    if(query.CommittedStatus()==COMMITTED_TRIANGLE_HIT) { hit.instance=0; hit.t=query.CommittedRayT(); }
}
// RTXPT traversal pattern: inline candidate filtering, followed by material
// coherence reordering before surface shading. No closest-hit invocation.
void TraceClosestInline(RayDesc ray,uint mask,inout Payload hit) {
    RayQuery<RAY_FLAG_CULL_BACK_FACING_TRIANGLES|RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(Scene,RAY_FLAG_NONE,mask,ray);
    while(query.Proceed()) {
        if(AcceptTriangle(query.CandidateInstanceID(),query.CandidatePrimitiveIndex(),
            query.CandidateTriangleBarycentrics(),query.CandidateTriangleRayT(),ray.Direction,hit))
            query.CommitNonOpaqueTriangleHit();
    }
#if DXR_REORDER_THREADS
    dx::HitObject object=dx::HitObject::FromRayQuery(query);
    uint materialHint=object.IsHit()?Instances[object.GetInstanceID()].mesh.z:0;
    dx::MaybeReorderThread(object,materialHint,8);
    if(object.IsHit()) {
        hit.instance=object.GetInstanceID(); hit.primitive=object.GetPrimitiveIndex();
        BuiltInTriangleIntersectionAttributes attributes; object.GetAttributes(attributes);
        hit.t=object.GetRayTCurrent(); hit.bary=attributes.barycentrics;
    }
#else
    if(query.CommittedStatus()==COMMITTED_TRIANGLE_HIT) {
        hit.instance=query.CommittedInstanceID(); hit.primitive=query.CommittedPrimitiveIndex();
        hit.t=query.CommittedRayT(); hit.bary=query.CommittedTriangleBarycentrics();
    }
#endif
}

#endif
Payload Trace(float3 origin,float3 direction,float maximum,bool shadow,inout uint rng,bool guide=false,bool ignoreGlass=false) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=max(maximum,Limits.x*2);
    Payload hit; hit.t=ray.TMax; hit.instance=0xffffffff; hit.primitive=0; hit.bary=0; hit.random=rng; hit.transparency=(guide?2:0)|(shadow?4:0)|(ignoreGlass?16:0);
#if DXR_USE_SER
    if(shadow) { TraceOcclusion(ray,hit); rng=hit.random; return hit; }
#endif
    uint rayFlags=RAY_FLAG_CULL_BACK_FACING_TRIANGLES|(shadow?RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH:0);
#if DXR_USE_SER
    TraceClosestInline(ray,255,hit);
#else
    TraceRay(Scene,rayFlags,255,0,0,0,ray,hit);
#endif
    rng=hit.random; return hit;
}
float3 Offset(Surface s,float3 direction) {
    float e=max(Limits.x,max(max(abs(s.p.x),abs(s.p.y)),abs(s.p.z))*1e-6);
    return s.p+s.g*(dot(s.g,direction)>=0?e:-e);
}
#include "PathEnvironment.hlsli"
float Luminance(float3 value) { return dot(value,float3(.2126,.7152,.0722)); }
#include "PathBsdf.hlsli"
#include "PathLightProposal.hlsli"
#include "PathFirefly.hlsli"
#include "PathState.hlsli"
float Weight(float a,float b) {
    float scale=max(a,b); if(scale<=0) return 0;
    a/=scale; b/=scale; return a*a/(a*a+b*b);
}
// Opaque blockers use first-hit termination, without material/normal-map evaluation.
// Thick refractors lit by analytic lights are handled by the photon pass.
Payload TraceGlass(float3 origin,float3 direction,float maximum,inout uint rng) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=maximum;
    Payload hit=(Payload)0; hit.t=maximum; hit.instance=0xffffffff; hit.random=rng; hit.transparency=8;
#if DXR_USE_SER
    TraceClosestInline(ray,2,hit);
#else
    TraceRay(Scene,RAY_FLAG_NONE,2,0,0,0,ray,hit);
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
        // A thin sheet is surrounded by air on both sides. Evaluate already uses
        // 1/IOR for either facing; using IOR on its back face invents total
        // internal reflection and makes indoor sun shadow rays see an opaque pane.
        float f=Fresnel(saturate(dot(s.g,-direction)),s.eta);
        visibility*=s.transmission*(1-f);
        if(s.thickness==0) visibility*=s.base;
        else if(s.front!=0) visibility*=exp(-Absorption(Materials[s.material])*s.thickness/max(abs(dot(s.g,direction)),.05));
        if(max(max(visibility.x,visibility.y),visibility.z)<1e-5) return 0;
        maximum-=hit.t; if(maximum<=Limits.x*4) return visibility;
        origin=Offset(s,direction);
    }
    return 0;
}
void DirectClassic(Surface s,float3 v,inout uint rng,AreaProposal proposal,inout Reservoir feedback,float3 throughput,
    out float3 diffuse,out float3 specular,out float3 unfiltered,out float3 momentD,out float3 momentS) {
    diffuse=specular=unfiltered=momentD=momentS=0;
    if(s.roughness<.001 && !any(DiffuseAlbedo(s)>0)) return;
    float3 factorD,factorS; NRD_MaterialFactors(s.n,v,DiffuseAlbedo(s),F0(s),s.roughness,factorD,factorS);
    // Delta lights retain deterministic direct lighting. Finite solar disks and
    // stochastic coverage use the denoised lobes, including their visibility noise.
    uint lightCount=(uint)Control.w+(SunRadiance.x>0?1:0);
    for(uint lightIndex=0;lightIndex<lightCount;lightIndex++) {
        Light light;
        if(lightIndex<(uint)Control.w) light=Lights[lightIndex];
        else { light.positionType=0; light.directionRange=float4(-SunDirection.xyz,0); light.colorIntensity=float4(SunRadiance.rgb,1); light.spot=0; }
        float3 l=-light.directionRange.xyz; float distance=CameraRight.w,attenuation=1;
        bool solarDisk=lightIndex==(uint)Control.w && SunDirection.w>0;
        if(solarDisk) { l=SampleSun(float2(Random(rng),Random(rng))); attenuation=SunSampleWeight(); }
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
                if(solarDisk || stochastic || Materials[s.material].flags.x==2) {
                    diffuse+=d*energy; specular+=r*energy;
                    momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
                }
                else unfiltered+=(d+r)*energy;
            }
        }
    }
    if(Environment.y>0) {
        uint lo=SampleEmitter(proposal,rng);
        Emitter emitter=Emitters[lo]; float u=sqrt(Random(rng)); float2 bary=float2(u*(1-Random(rng)),0); bary.y=u-bary.x;
        float3 position=emitter.p0.xyz+emitter.e1.xyz*bary.x+emitter.e2.xyz*bary.y;
        float3 delta=position-s.p; float dist=length(delta); float3 l=delta/max(dist,1e-6);
        float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf);
        if(any(d+r>0)) {
            Surface lamp=Evaluate((uint)emitter.meta.z,(uint)emitter.meta.w,bary,0,-l);
            float cosine=abs(dot(lamp.g,-l)); Material lm=Materials[lamp.material];
            if(lm.flags.z!=0||lamp.front!=0) {
                float lightPdf=EmitterAreaPdf(lo,proposal)*dist*dist/max(cosine,1e-10);
                bool stochastic; float3 visibility=0;
                if(cosine>0 && any(d+r>0)) visibility=Visibility(Offset(s,l),l,dist-max(Limits.x*8,dist*1e-5),rng,stochastic);
                if(cosine>0 && dot(s.n,l)>0 && any(visibility>0)) {
                    float3 energy=lamp.emission*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/max(lightPdf,1e-10)*visibility;
                    if(lm.flags.x==1 && lamp.alpha<lm.flags.y) energy=0; if(lm.flags.x==2) energy*=lamp.alpha;
                    diffuse+=d*energy; specular+=r*energy;
                    RecordLightFeedback(feedback,lo,throughput*(d+r)*energy,rng);
                    momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
                }
            }
        }
    }
    if(Environment.x>0) {
        float lightPdf; float3 l=SampleEnvironment(float4(Random(rng),Random(rng),Random(rng),Random(rng)),lightPdf);
        float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf);
        bool stochastic; float3 visibility=0;
        if(any(d+r>0)) visibility=Visibility(Offset(s,l),l,CameraRight.w,rng,stochastic);
        if(dot(s.n,l)>0 && any(visibility>0)) {
            // Geometry visibility already accounts for occlusion; multiplying AO here would
            // disagree with the complementary BSDF-sampled environment contribution.
            float3 energy=Sky(l)*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/lightPdf*visibility; diffuse+=d*energy; specular+=r*energy;
            momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
        }
    }
}
#include "SceneLighting.hlsli"
#include "PathStablePlanes.hlsli"

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

void AccumulatePath(float3 value,int lobe,float3 direction,float3 factorD,float3 factorS,
    inout float3 sumD,inout float3 sumS,inout float3 momentD,inout float3 momentS,
    uint plane,inout float3 reflection,inout float3 transmission) {
    if(plane==1) { reflection+=value; return; }
    if(plane==2) { transmission+=value; return; }
    if(lobe==0) { sumD+=value; momentD+=direction*_NRD_Luminance(value/factorD); }
    else { sumS+=value; momentS+=direction*_NRD_Luminance(value/factorS); }
}

// Dedicated unlit OptiX features. Smooth glass/mirrors follow the dominant ideal
// path to the visible surface; stochastic beauty paths and NRD/RR features are unchanged.
void OptixGuide(Surface s,float3 direction,uint instance,out float3 albedo,out float3 normal) {
    PathState path=BeginPath(CameraOrigin.xyz,direction,CameraForward.w*2/Size.y);
    float3 tint=1; float3x3 rotation=float3x3(1,0,0,0,1,0,0,0,1); uint rng=0x4c1539u;
    for(uint bounce=0;bounce<8;bounce++) {
        if(!SplitSurface(s)) break;
        float eta=s.thickness>0?InterfaceEta(path,s,instance):1/s.ior;
        bool transmitted=s.transmission>0 && Fresnel(saturate(dot(-path.direction,s.n)),eta)<.5;
        float3 outgoing=transmitted?(s.thickness>0?refract(path.direction,s.n,eta):path.direction):reflect(path.direction,s.n);
        if(dot(outgoing,outgoing)<1e-8) { transmitted=false; outgoing=reflect(path.direction,s.n); }
        rotation=mul(rotation,InterfaceTransform(s,path.direction,outgoing,transmitted));
        tint*=transmitted?s.transmission*(s.thickness>0?float3(1,1,1):s.base):F0(s);
        if(transmitted && s.thickness>0 && !UpdateMedium(path,s,instance)) break;
        path.origin=Offset(s,outgoing); path.direction=outgoing;
        Payload hit=Trace(path.origin,path.direction,CameraRight.w,false,rng,true);
        if(hit.instance==0xffffffff) { albedo=0; normal=0; return; }
        s=Evaluate(hit.instance,hit.primitive,hit.bary,hit.t*path.coneSpread,-outgoing); instance=hit.instance;
    }
    albedo=saturate(tint*(DiffuseAlbedo(s)+F0(s)+s.transmission));
    normal=SafeNormalize(mul(rotation,s.n),s.n);
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
    Reservoir feedback=(Reservoir)0;
    float3 optixAlbedo=0,optixNormal=0;
    float3 stableReflection=0,stableTransmission=0;
    float4 reflectionSurface=float4(0,0,0,-1),transmissionSurface=reflectionSurface;
    float4 reflectionNormal=float4(0,0,1,1),transmissionNormal=reflectionNormal;
    bool stableSplit=false;
    if(guide.instance!=0xffffffff) {
        Surface g=Evaluate(guide.instance,guide.primitive,guide.bary,guide.t*CameraForward.w*2/Size.y,-primary);
        feedback.position=g.p; feedback.normal=g.n; feedback.material=g.material; feedback.roughness=g.roughness;
        glass=g.transmission>0;
        stableSplit=PathSettings.w!=0 && !coverage && SplitSurface(g);
        if(stableSplit) {
            StableGuide(g,primary,guide.instance,false,reflectionSurface,reflectionNormal);
            if(glass) StableGuide(g,primary,guide.instance,true,transmissionSurface,transmissionNormal);
        }
        if(!coverage) GatherCaustics(g,-primary,primaryCausticD,primaryCausticS,causticMomentD,causticMomentS);
        primarySurface=float4(g.p,guide.t); primaryNormal=g.n; primaryAlbedo=DiffuseAlbedo(g);
        roughness=g.roughness; primaryMaterial=g.material; reactive=coverage||g.transmission>0?1:0;
        if(Environment.w!=0) OptixGuide(g,primary,guide.instance,optixAlbedo,optixNormal);
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
    uint pathCount=(uint)Control.x*(transparentPass || (stableSplit && glass)?2u:1u);
    for(uint sample=0;sample<pathCount;sample++) {
        uint sampleIndex=(uint)Control.z*pathCount+sample;
        float3 rayDirection=primary;
        if(PathSettings.x!=0) {
            float2 sampleNdc=(float2(pixel)+.5+CameraSample(pixel,sampleIndex))/Size.xy*2-1;
            rayDirection=normalize(CameraForward.xyz+CameraRight.xyz*sampleNdc.x*(Size.x/Size.y)*CameraForward.w-CameraUp.xyz*sampleNdc.y*CameraForward.w);
        }
        PathState path=BeginPath(CameraOrigin.xyz,rayDirection,CameraForward.w*2/Size.y);
        float secondaryDistance=0; int firstLobe=-1; float3 firstDirection=0;
        bool hitDistanceRecorded=false;
        uint plane=0;
        uint bounce=0;
        for(uint step=0;step<(uint)Control.y+16 && bounce<(uint)Control.y;step++) {
            // NEE, alpha traversal, BSDF and roulette have independent random streams.
            rng=Hash32Combine(PathSeed(pixel,bounce,2),sampleIndex);
            Payload hit;
            if(step==0 && !coverage && PathSettings.x==0) hit=guide;
            else hit=Trace(path.origin,path.direction,CameraRight.w,false,rng,false,bounce==0 && opaqueForNrd);
            if(bounce==0) reactive=max(reactive,(float)(hit.transparency&1));
            if(bounce>0 && !hitDistanceRecorded) secondaryDistance+=hit.instance==0xffffffff?65504:min(hit.t,65504);
            if(hit.instance==0xffffffff) {
                if(bounce>0 && !hitDistanceRecorded) {
                    if(firstLobe==0) { hitD+=min(secondaryDistance,65504); countD++; }
                    else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,min(secondaryDistance,65504));
                }
                float3 value=path.throughput*Sky(path.direction)*(bounce==0||path.transmitted||path.delta?1:Weight(path.previousPdf,EnvironmentPdf(path.direction)));
                if(bounce>0) value*=FireflyWeight(value,path.fireflyK);
                if(bounce==0) sumE+=value;
                else AccumulatePath(value,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission);
                break;
            }
            path.coneWidth+=path.coneSpread*hit.t;
            path.distanceSinceScatter+=hit.t;
            Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,path.coneWidth,-path.direction);
            if(s.thickness>0) s.eta=InterfaceEta(path,s,hit.instance);
            Material m=Materials[s.material];
            if(path.mediumCount>0) path.throughput*=exp(-MediumAbsorption(path,ActiveMedium(path))*hit.t);
            else if(s.front==0 && s.transmission>0 && s.thickness>0) path.throughput*=exp(-Absorption(m)*hit.t);
            if(FalseInterface(path,s,hit.instance)) {
                if(!UpdateMedium(path,s,hit.instance)) break;
                path.origin=Offset(s,path.direction);
                continue; // Rejected overlap boundaries do not consume a scattering bounce.
            }
            if(bounce>0 && !hitDistanceRecorded && !(path.transmitted && s.transmission>0)) {
                if(firstLobe==0) { hitD+=min(secondaryDistance,65504); countD++; }
                else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,min(secondaryDistance,65504));
                hitDistanceRecorded=true;
            }
            float3 cached;
            if(transparentPass && bounce>0 && s.transmission==0 && SampleOpaque(s,-path.direction,cached)) {
                float3 value=path.throughput*cached; value*=FireflyWeight(value,path.fireflyK); cachedSum+=value;
                AccumulatePath(value,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission); break;
            }
            float areaPdf=PathSettings.y!=0?EmitterAreaPdf(s.emitter,path.previousLightProposal):s.emissionPdf;
            float lightPdf=areaPdf*path.distanceSinceScatter*path.distanceSinceScatter/max(abs(dot(s.g,-path.direction)),1e-10);
            float emissionWeight=bounce==0||path.transmitted||path.delta||s.emissionPdf==0?1:Weight(path.previousPdf,lightPdf);
            float3 radiance=s.emission*emissionWeight;
            if(m.flags.w!=0) radiance+=s.base;
            if(bounce>0) radiance*=FireflyWeight(path.throughput*radiance,path.fireflyK);
            if(bounce==0) sumE+=path.throughput*radiance;
            else AccumulatePath(path.throughput*radiance,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission);
            if(m.flags.w!=0) break;
            float3 directD,directS,unfiltered,directMomentD,directMomentS;
            // At the depth limit there is no competing continuation sample for MIS.
            s.bsdfSampling=bounce+1<(uint)Control.y?1:0;
            AreaProposal proposal=LightProposal(s);
            Direct(s,-path.direction,rng,!transparentPass && bounce==0 && sample==0 && !coverage && s.transmission==0,
                pixel,proposal,feedback,path.throughput,directD,directS,unfiltered,directMomentD,directMomentS);
            float3 causticD=primaryCausticD,causticS=primaryCausticS,cmD=causticMomentD,cmS=causticMomentS;
            if(bounce>0 || coverage || PathSettings.x!=0) GatherCaustics(s,-path.direction,causticD,causticS,cmD,cmS);
            directD+=causticD; directS+=causticS;
            float filter=FireflyWeight(path.throughput*(directD+directS+unfiltered),path.fireflyK);
            directD*=filter; directS*=filter; unfiltered*=filter;
            directMomentD*=filter; directMomentS*=filter; cmD*=filter; cmS*=filter;
            if(bounce==0) {
                sumD+=path.throughput*directD; sumS+=path.throughput*directS; sumE+=path.throughput*unfiltered;
                momentD+=directMomentD+cmD; momentS+=directMomentS+cmS;
            } else {
                float3 localRadiance=directD+directS+unfiltered;
                AccumulatePath(path.throughput*localRadiance,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission);
            }
            float3 u=ScatterSamples(pixel,sampleIndex,bounce);
            // Deterministic first-event splitting keeps both glass lobes present in NRD.
            float splitWeight=1;
            if((transparentPass || (stableSplit && glass)) && bounce==0) {
                if(s.transmission==1) {
                    float p=SpecProbability(s,-path.direction);
                    bool reflection=(sample&1u)==0;
                    float pTrans=(1-p)*TransmissionProbability(s);
                    u.z=reflection?p*.5:p+pTrans*.5;
                    splitWeight=2*(reflection?p:pTrans);
                } else u.z=(u.z+(sample&1u))*.5;
            }
            ScatterResult scatter=Scatter(s,-path.direction,u);
            path.previousLightProposal=proposal;
            scatter.weight*=splitWeight;
            if(bounce==0) {
                firstLobe=(int)scatter.lobe; firstDirection=scatter.direction;
                if(stableSplit && hit.instance==guide.instance && scatter.lobe==1) plane=scatter.transmitted?2:1;
            }
            if(!AdvancePath(path,s,hit.instance,scatter)) break;
            if(Control.y==1 && bounce==0) {
                Payload secondary=Trace(path.origin,path.direction,CameraRight.w,false,rng);
                float distance=secondary.instance==0xffffffff?65504:min(secondary.t,65504);
                if(firstLobe==0) { hitD+=distance; countD++; } else NRD_FrontEnd_SpecHitDistAveraging_Add(hitS,distance);
            }
            // Apply roulette to all lobes. Remove radiance-mode eta scaling from its survival estimate.
            if(bounce>=2) {
                float survival=clamp(max(max(path.throughput.r,path.throughput.g),path.throughput.b)*path.rouletteEtaScale,.05,.95);
                if(RouletteSample(pixel,sampleIndex,bounce)>survival) break;
                path.throughput/=survival;
            }
            bounce++;
        }
    }
    float inv=1.0/pathCount; sumD*=inv; sumS*=inv; sumE*=inv; momentD*=inv; momentS*=inv;
    stableReflection*=inv; stableTransmission*=inv;
    float3 total=sumD+sumS+sumE+stableReflection+stableTransmission;
    if(!transparentPass && PathSettings.y!=0) ReservoirsOut[pixel.y*(uint)Size.x+pixel.x]=feedback;
    // Legacy BLEND may reveal emissive/direct-lit backgrounds stochastically: none of that may bypass denoising.
    if(coverage) { sumS=total; sumD=sumE=0; }
    Raw[pixel]=float4(all(isfinite(total))?max(total,0):0,1);
    if(transparentPass) {
        GlassSurface[pixel]=float4(primarySurface.xyz,-(primarySurface.w+1));
        GlassNormal[pixel]=float4(primaryNormal,guide.instance+1);
        float3 fallback=max(total-cachedSum*inv,0);
        GlassFallback[pixel]=float4(all(isfinite(fallback))?fallback:0,1);
        return;
    }
    StableReflection[pixel]=float4(stableReflection,max(reflectionSurface.w,0));
    StableTransmission[pixel]=float4(stableTransmission,max(transmissionSurface.w,0));
    Diffuse[pixel]=float4(sumD/factorD,countD>0?hitD/countD:0);
    NRD_FrontEnd_SpecHitDistAveraging_End(hitS);
    Specular[pixel]=float4(sumS/factorS,hitS);
    DiffuseSh[pixel]=float4(coverage?0:momentD,0); SpecularSh[pixel]=float4(coverage?0:momentS,0);
    StableReflectionSurface[pixel]=reflectionSurface; StableTransmissionSurface[pixel]=transmissionSurface;
    StableReflectionNormal[pixel]=reflectionNormal; StableTransmissionNormal[pixel]=transmissionNormal;
    Normals[pixel]=float4(primaryNormal*.5+.5,min(primaryMaterial,254)/255.0);
    Surfaces[pixel]=primarySurface;
    NormalRoughness[pixel]=Environment.w!=0?float4(optixNormal,roughness):float4(primaryNormal/max(max(abs(primaryNormal.x),abs(primaryNormal.y)),abs(primaryNormal.z)),roughness);
    DiffuseFactor[pixel]=float4(factorD,1); SpecularFactor[pixel]=float4(factorS,1);
    GlassSurface[pixel]=primarySurface;
    GlassFallback[pixel]=0; GlassNormal[pixel]=0;
    SpecularGuide[pixel]=float4(specularAlbedo,mirrorDistance);
    Albedo[pixel]=float4(Environment.w!=0?optixAlbedo:primaryAlbedo,reactive); Unfiltered[pixel]=float4(sumE,1);
}
#ifdef DXR_VALIDATE_CONTRACTS
#include "PathContracts.hlsli"
[shader("raygeneration")]
void RayGen() { ShaderContracts(); }
#else
[shader("raygeneration")]
void RayGen() { RenderPixel(false); }
#endif
[shader("raygeneration")]
void TransparentRayGen() { RenderPixel(true); }
