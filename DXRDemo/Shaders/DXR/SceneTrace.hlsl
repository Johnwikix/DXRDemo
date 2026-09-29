// Explicit scene ABI. C# layouts live in SceneAsset.cs and DxrSceneBackend.cs.
static const float PI = 3.14159265359;
struct Vertex { float4 position, normal, tangent, uv, color; };
struct Binding { float4 info, transform, extra; };
struct Material { float4 baseColor, emissive, factors, flags; Binding baseMap, mrMap, normalMap, aoMap, emissiveMap; };
struct Instance { uint4 mesh; row_major float4x4 world, normal; };
struct Light { float4 positionType, directionRange, colorIntensity, spot; };
struct Emitter { float4 p0, e1, e2, meta; };
struct Payload { float t; uint instance, primitive; float2 bary; uint random, transparency; };
struct Surface { float3 p, n, g, base, emission; float metallic, roughness, alpha, ao; uint material; };
cbuffer Frame : register(b0) {
    float4 CameraOrigin, CameraForward, CameraRight, CameraUp, Size, Control, Environment, Limits, SunDirection, SunRadiance;
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
RWTexture2D<float4> Raw : register(u0);
RWTexture2D<float4> Normals : register(u1);
RWTexture2D<float4> Surfaces : register(u2);
RWTexture2D<float4> NormalRoughness : register(u3);
RWTexture2D<float4> Diffuse : register(u4);
RWTexture2D<float4> Specular : register(u5);
RWTexture2D<float4> Albedo : register(u6);
RWTexture2D<float4> Unfiltered : register(u7); // Emission, sky and deterministic primary direct lighting.

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
    if (dot(g,view)<0) { g=-g; n=-n; }
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
    return result;
}
[shader("anyhit")]
void AnyHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    Instance node; Vertex a,b,c; Triangle(InstanceID(),PrimitiveIndex(),node,a,b,c);
    Material material=Materials[node.mesh.z];
    float3 normal=mul(float4(cross(b.position.xyz-a.position.xyz,c.position.xyz-a.position.xyz),0),node.normal).xyz;
    if (material.flags.z==0 && dot(normal,WorldRayDirection())>=0) { IgnoreHit(); return; }
    if(material.flags.x==0) return;
    if(material.flags.x==2) payload.transparency=1;
    float3 w=float3(1-attr.barycentrics.x-attr.barycentrics.y,attr.barycentrics);
    float alpha=material.baseColor.a*Sample(material.baseMap,a.uv*w.x+b.uv*w.y+c.uv*w.z,0).a*(a.color.a*w.x+b.color.a*w.y+c.color.a*w.z);
    if((material.flags.x==1 && alpha<material.flags.y)||(material.flags.x==2 && Random(payload.random)>=alpha)) IgnoreHit();
}
[shader("closesthit")]
void ClosestHit(inout Payload payload, BuiltInTriangleIntersectionAttributes attr) {
    payload.t=RayTCurrent(); payload.instance=InstanceID(); payload.primitive=PrimitiveIndex(); payload.bary=attr.barycentrics;
}
[shader("miss")]
void Miss(inout Payload payload) { payload.instance=0xffffffff; }
Payload Trace(float3 origin,float3 direction,float maximum,bool shadow,inout uint rng) {
    RayDesc ray; ray.Origin=origin; ray.Direction=direction; ray.TMin=Limits.x; ray.TMax=max(maximum,Limits.x*2);
    Payload hit; hit.t=ray.TMax; hit.instance=0xffffffff; hit.primitive=0; hit.bary=0; hit.random=rng; hit.transparency=0;
    TraceRay(Scene,shadow?RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH:RAY_FLAG_NONE,255,0,0,0,ray,hit); rng=hit.random; return hit;
}
float3 Offset(Surface s,float3 direction) {
    float e=max(Limits.x,max(max(abs(s.p.x),abs(s.p.y)),abs(s.p.z))*1e-6);
    return s.p+s.g*(dot(s.g,direction)>=0?e:-e);
}
float3 Sky(float3 direction) { return lerp(float3(.45,.55,.7),float3(.1,.2,.4),saturate(direction.y))*Environment.x; }
float Luminance(float3 value) { return dot(value,float3(.2126,.7152,.0722)); }
float SpecProbability(Surface s) {
    if(s.metallic>.999) return 1;
    float reflection=Luminance(lerp(.04.xxx,s.base,s.metallic));
    return clamp(reflection/(reflection+Luminance(s.base)*(1-s.metallic)+1e-6),.1,.9);
}
float G1(float cosine,float a2) { return 2*cosine/max(cosine+sqrt(a2+(1-a2)*cosine*cosine),1e-6); }
void Bsdf(Surface s,float3 v,float3 l,out float3 diffuse,out float3 specular,out float pdf) {
    diffuse=specular=0; pdf=0; float nv=saturate(dot(s.n,v)),nl=saturate(dot(s.n,l));
    if(nv<=0||nl<=0||dot(s.g,l)<=0) return;
    float3 h=SafeNormalize(v+l,s.n); float nh=saturate(dot(s.n,h)),vh=saturate(dot(v,h));
    float a=s.roughness*s.roughness,a2=a*a,den=nh*nh*(a2-1)+1;
    float d=a2/(PI*den*den); float3 f0=lerp(.04.xxx,s.base,s.metallic),f=f0+(1-f0)*pow(1-vh,5);
    specular=d*G1(nv,a2)*G1(nl,a2)*f/max(4*nv*nl,1e-6);
    diffuse=(1-f)*s.base*(1-s.metallic)/PI;
    float probability=SpecProbability(s); pdf=(1-probability)*nl/PI+probability*d*nh/max(4*vh,1e-6);
}
float3 Local(float3 n,float3 v) {
    float3 t=normalize(cross(abs(n.y)<.99?float3(0,1,0):float3(1,0,0),n)); return t*v.x+cross(n,t)*v.y+n*v.z;
}
float Weight(float a,float b) { a*=a; b*=b; return a/max(a+b,1e-20); }
void Direct(Surface s,float3 v,inout uint rng,out float3 diffuse,out float3 specular,out float3 unfiltered) {
    diffuse=specular=unfiltered=0;
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
        if(nl>0 && attenuation>0) {
            Payload shadow=Trace(Offset(s,l),l,distance-Limits.x*4,true,rng);
            if(shadow.instance==0xffffffff) {
                float3 energy=light.colorIntensity.rgb*light.colorIntensity.w*attenuation*nl;
                if(shadow.transparency!=0 || Materials[s.material].flags.x==2) { diffuse+=d*energy; specular+=r*energy; }
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
            if(cosine>0 && dot(s.n,l)>0 && Trace(Offset(s,l),l,dist-max(Limits.x*8,dist*1e-5),true,rng).instance==0xffffffff) {
                float3 energy=lamp.emission*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/max(lightPdf,1e-10);
                if(lm.flags.x==1 && lamp.alpha<lm.flags.y) energy=0; if(lm.flags.x==2) energy*=lamp.alpha;
                diffuse+=d*energy; specular+=r*energy;
            }
        }
    }
    if(Environment.x>0) {
        float z=1-2*Random(rng),phi=2*PI*Random(rng),radius=sqrt(max(0,1-z*z)); float3 l=float3(radius*cos(phi),z,radius*sin(phi));
        float3 d,r; float pdf; Bsdf(s,v,l,d,r,pdf); float lightPdf=1/(4*PI);
        if(dot(s.n,l)>0 && Trace(Offset(s,l),l,CameraRight.w,true,rng).instance==0xffffffff) {
            // AO affects only environment illumination, never punctual lights.
            float3 energy=Sky(l)*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/lightPdf*s.ao; diffuse+=d*energy; specular+=r*energy;
        }
    }
}
[shader("raygeneration")]
void RayGen() {
    uint2 pixel=DispatchRaysIndex().xy;
    uint rng=pixel.x*73856093+pixel.y*19349663+(uint)Control.z*83492791+12345;
    float2 ndc=(float2(pixel)+.5+Size.zw)/Size.xy*2-1;
    float3 primary=normalize(CameraForward.xyz+CameraRight.xyz*ndc.x*(Size.x/Size.y)*CameraForward.w-CameraUp.xyz*ndc.y*CameraForward.w);
    float3 sumD=0,sumS=0,sumE=0,primaryAlbedo=0,primaryNormal=float3(0,0,1); float roughness=1,reactive=0;
    float4 primarySurface=float4(primary,-1); uint primaryMaterial=0;
    float hitD=0,hitS=0,countD=0,countS=0;
    for(uint sample=0;sample<(uint)Control.x;sample++) {
        float3 ro=CameraOrigin.xyz,rd=primary,throughput=1; float previousPdf=0; int firstLobe=-1;
        for(uint bounce=0;bounce<(uint)Control.y;bounce++) {
            Payload hit=Trace(ro,rd,CameraRight.w,false,rng);
            if(bounce==0) reactive=max(reactive,(float)hit.transparency);
            if(bounce==1) { float distance=hit.instance==0xffffffff?65504:min(hit.t,65504); if(firstLobe==0) { hitD+=distance; countD++; } else { hitS+=distance; countS++; } }
            if(hit.instance==0xffffffff) {
                float3 value=throughput*Sky(rd)*(bounce==0?1:Weight(previousPdf,1/(4*PI)));
                if(bounce==0) sumE+=value; else if(firstLobe==0) sumD+=value; else sumS+=value; break;
            }
            float cone=hit.t*CameraForward.w*2/Size.y;
            Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,cone,-rd); Material m=Materials[s.material];
            if(bounce==0 && sample==0) {
                primarySurface=float4(s.p,hit.t); primaryNormal=s.n; primaryAlbedo=s.base*(1-s.metallic);
                roughness=s.roughness; primaryMaterial=s.material;
            }
            float emissionWeight=bounce==0||Environment.z<=0?1:Weight(previousPdf,hit.t*hit.t/max(Environment.z*abs(dot(s.g,-rd)),1e-10));
            float3 radiance=s.emission*emissionWeight;
            if(m.flags.w!=0) radiance+=s.base;
            if(bounce==0) sumE+=radiance; else if(firstLobe==0) sumD+=throughput*radiance; else sumS+=throughput*radiance;
            if(m.flags.w!=0) break;
            float3 directD,directS,unfiltered; Direct(s,-rd,rng,directD,directS,unfiltered);
            if(bounce==0) { sumD+=directD; sumS+=directS; sumE+=unfiltered; }
            else if(firstLobe==0) sumD+=throughput*(directD+directS+unfiltered); else sumS+=throughput*(directD+directS+unfiltered);
            bool spec=Random(rng)<SpecProbability(s); float u=Random(rng),phi=2*PI*Random(rng); float3 next;
            if(spec) { float a=s.roughness*s.roughness,ct=sqrt((1-u)/(1+(a*a-1)*u)),st=sqrt(max(0,1-ct*ct)); next=reflect(rd,Local(s.n,float3(st*cos(phi),st*sin(phi),ct))); }
            else { float r=sqrt(u); next=Local(s.n,float3(r*cos(phi),r*sin(phi),sqrt(1-u))); }
            float3 d,r; float pdf; Bsdf(s,-rd,next,d,r,pdf);
            // Split the primary mixture into physically distinct lobe signals with their own proposal PDFs.
            if(bounce==0) {
                firstLobe=spec?1:0;
                float p=SpecProbability(s),nl=saturate(dot(s.n,next));
                float3 h=SafeNormalize(next-rd,s.n); float nh=saturate(dot(s.n,h)),vh=saturate(dot(-rd,h));
                float a=s.roughness*s.roughness,a2=a*a,den=nh*nh*(a2-1)+1;
                float lobePdf=spec?p*a2*nh/max(PI*den*den*4*vh,1e-10):(1-p)*nl/PI;
                throughput=(spec?r:d)*nl/max(lobePdf,1e-10);
            } else throughput*=(d+r)*saturate(dot(s.n,next))/max(pdf,1e-10);
            previousPdf=pdf; ro=Offset(s,next); rd=next;
            if(Control.y==1 && bounce==0) {
                Payload secondary=Trace(ro,rd,CameraRight.w,false,rng); float distance=secondary.instance==0xffffffff?65504:min(secondary.t,65504);
                if(firstLobe==0) { hitD+=distance; countD++; } else { hitS+=distance; countS++; }
            }
            if(pdf<=0||max(max(throughput.r,throughput.g),throughput.b)<=0) break;
            if(bounce>=2) { float survival=clamp(max(max(throughput.r,throughput.g),throughput.b),.05,.95); if(Random(rng)>survival) break; throughput/=survival; }
        }
    }
    float inv=1/Control.x; sumD*=inv; sumS*=inv; sumE*=inv;
    float3 total=sumD+sumS+sumE;
    Raw[pixel]=float4(all(isfinite(total))?max(total,0):0,1);
    Normals[pixel]=float4(primaryNormal*.5+.5,min(primaryMaterial,254)/255.0);
    Surfaces[pixel]=primarySurface;
    NormalRoughness[pixel]=float4(primaryNormal/max(max(abs(primaryNormal.x),abs(primaryNormal.y)),abs(primaryNormal.z)),roughness);
    Diffuse[pixel]=float4(sumD/max(primaryAlbedo,.001),countD>0?hitD/countD:0);
    Specular[pixel]=float4(sumS,countS>0?hitS/countS:0);
    Albedo[pixel]=float4(primaryAlbedo,reactive); Unfiltered[pixel]=float4(sumE,1);
}
