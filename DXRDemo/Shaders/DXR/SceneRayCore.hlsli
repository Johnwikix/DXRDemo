// 共享 TraceRay/材质核心；不依赖 NRD 头文件或厂商二进制。
#include "SceneTypes.hlsli"
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
RWTexture2D<float4> Unfiltered : register(u7); // Emission/sky/direct terms, including bounded deterministic optical paths.
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
