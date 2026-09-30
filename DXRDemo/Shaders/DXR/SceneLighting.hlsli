// ReSTIR DI: unoccluded-target RIS, fused temporal/spatial reuse from the previous frame.
// A bounded, biased reuse estimator (no visibility bias correction); final visibility is always retraced.
// Sample domain: discrete punctual lights plus emissive triangle area. The area-light MIS partition
// remains the same as the BSDF continuation in SceneTrace, so emitters are not counted twice.
struct LightSample { uint index; float2 bary; uint area; };
// 64-byte ABI. weight is finalized W = sum(w) / (M * target(selected)).
struct Reservoir {
    LightSample light;
    float weight, target, count; uint material;
    float3 position; float depth;
    float3 normal; float roughness;
};
RWStructuredBuffer<Reservoir> ReservoirsIn : register(u9);
RWStructuredBuffer<Reservoir> ReservoirsOut : register(u10);
// 64-byte ABI. Each launched photon owns one node; atomic heads publish nodes between dispatches.
struct Photon { float3 position; uint next; float3 flux; float pad0; float3 incoming; float pad1; float3 normal; float pad2; };
RWStructuredBuffer<Photon> Photons : register(u11);
RWStructuredBuffer<uint> PhotonHeads : register(u12);

uint AnalyticLightCount() { return (uint)Control.w+(any(SunRadiance.rgb>0)?1u:0u); }
Light AnalyticLight(uint index) {
    if(index<(uint)Control.w) return Lights[index];
    Light light=(Light)0; light.directionRange=float4(-SunDirection.xyz,0);
    light.colorIntensity=float4(SunRadiance.rgb,1); return light;
}
float RangeFalloff(Light light,float distance) {
    return light.directionRange.w>0?saturate(1-pow(distance/light.directionRange.w,4)):1;
}
float SpotFalloff(Light light,float3 direction) {
    if(light.positionType.w!=2) return 1;
    float cone=saturate((dot(direction,light.directionRange.xyz)-light.spot.y)/max(light.spot.x-light.spot.y,1e-6));
    return cone*cone;
}
LightSample ProposeLight(inout uint rng,out float proposal) {
    uint count=AnalyticLightCount(); bool hasArea=Environment.y>0;
    float areaProbability=hasArea?(count>0?.5:1):0;
    LightSample result=(LightSample)0;
    if(Random(rng)<areaProbability) {
        result.area=1;
        float choice=Random(rng); uint lo=0,hi=(uint)Environment.y-1;
        while(lo<hi) { uint mid=(lo+hi)/2; if(Emitters[mid].meta.y<choice) lo=mid+1; else hi=mid; }
        result.index=lo;
        float u=sqrt(Random(rng)); result.bary.x=u*(1-Random(rng)); result.bary.y=u-result.bary.x;
        proposal=areaProbability/max(Environment.z,1e-20);
    } else {
        result.index=min((uint)(Random(rng)*count),max(count,1u)-1);
        proposal=(1-areaProbability)/max(count,1u);
    }
    return result;
}
void EvaluateLight(Surface s,float3 view,LightSample sample,out float3 d,out float3 r,out float3 direction,out float distance) {
    float3 energy=0; direction=0; distance=CameraRight.w; d=r=0;
    if(sample.area==0) {
        if(sample.index>=AnalyticLightCount()) return;
        Light light=AnalyticLight(sample.index); direction=-light.directionRange.xyz;
        float attenuation=1;
        if(light.positionType.w!=0) {
            direction=light.positionType.xyz-s.p; distance=length(direction); direction/=max(distance,1e-8);
            attenuation=RangeFalloff(light,distance)*SpotFalloff(light,-direction)/max(distance*distance,1e-8);
        }
        energy=light.colorIntensity.rgb*light.colorIntensity.w*attenuation;
    } else {
        if(sample.index>=(uint)Environment.y) return;
        Emitter emitter=Emitters[sample.index];
        float3 position=emitter.p0.xyz+emitter.e1.xyz*sample.bary.x+emitter.e2.xyz*sample.bary.y;
        direction=position-s.p; distance=length(direction); direction/=max(distance,1e-8);
        Surface lamp=Evaluate((uint)emitter.meta.z,(uint)emitter.meta.w,sample.bary,0,-direction);
        Material material=Materials[lamp.material];
        if(material.flags.z==0 && lamp.front==0) return;
        float cosine=abs(dot(lamp.g,-direction));
        float lightPdf=distance*distance/max(Environment.z*cosine,1e-20);
        float pdf; Bsdf(s,view,direction,d,r,pdf);
        energy=lamp.emission*(cosine/max(distance*distance,1e-12))*Weight(lightPdf,pdf);
        if(material.flags.x==1 && lamp.alpha<material.flags.y) energy=0;
        if(material.flags.x==2) energy*=lamp.alpha;
    }
    float pdf; Bsdf(s,view,direction,d,r,pdf);
    energy*=saturate(dot(s.n,direction)); d*=energy; r*=energy;
}
float Target(Surface s,float3 view,LightSample sample) {
    float3 d,r,l; float distance; EvaluateLight(s,view,sample,d,r,l,distance);
    float target=Luminance(d+r); return isfinite(target)?max(target,0):0;
}
void Stream(inout Reservoir reservoir,LightSample sample,float target,float weight,float count,inout uint rng) {
    reservoir.count+=count;
    if(weight<=0 || !isfinite(weight)) return;
    reservoir.weight+=weight;
    if(Random(rng)*reservoir.weight<weight) { reservoir.light=sample; reservoir.target=target; }
}
bool Reproject(float3 p,out int2 pixel) {
    float3 delta=p-PreviousOrigin.xyz; float z=dot(delta,PreviousForward.xyz);
    float2 ndc=float2(dot(delta,PreviousRight.xyz)/(PreviousForward.w*(Size.x/Size.y)),-dot(delta,PreviousUp.xyz)/PreviousForward.w)/max(z,1e-8);
    pixel=(int2)floor((ndc*.5+.5)*PreviousSize.xy-PreviousSize.zw);
    return z>0 && all(pixel>=0) && all(pixel<(int2)PreviousSize.xy);
}
Reservoir BuildReservoir(Surface s,float3 view,bool reuse,inout uint rng) {
    Reservoir result=(Reservoir)0;
    // Current candidates always contribute, including zero-target samples, to avoid stale-history lock-in.
    uint candidates=reuse?(uint)Lighting.z:4u;
    for(uint i=0;i<candidates;i++) {
        float proposal; LightSample light=ProposeLight(rng,proposal); float target=Target(s,view,light);
        Stream(result,light,target,target/max(proposal,1e-20),1,rng);
    }
    int2 previousPixel;
    if(reuse && Lighting.y!=0 && Reproject(s.p,previousPixel)) {
        float footprint=max(length(s.p-CameraOrigin.xyz)*CameraForward.w*2/Size.y,Limits.x*8);
        for(uint neighbor=0;neighbor<5;neighbor++) {
            int2 location=previousPixel;
            if(neighbor>0) {
                float angle=2*PI*(Random(rng)+neighbor*.25),radius=2+6*Random(rng);
                location+=(int2)round(float2(cos(angle),sin(angle))*radius);
            }
            if(any(location<0)||any(location>=(int2)Size.xy)) continue;
            Reservoir old=ReservoirsIn[location.y*(uint)Size.x+location.x];
            float3 delta=old.position-s.p;
            // Position/plane, normal, roughness and material gates reject disocclusion and silhouette reuse.
            if(old.count<=0 || old.material!=s.material || dot(old.normal,s.n)<.95 || abs(old.roughness-s.roughness)>.1 ||
                abs(dot(delta,s.g))>footprint*.75 || length(delta)>footprint*(neighbor==0?2:12)) continue;
            float count=min(old.count,Lighting.w),target=Target(s,view,old.light);
            Stream(result,old.light,target,target*old.weight*count,count,rng);
        }
    }
    result.weight=result.target>0?result.weight/max(result.count*result.target,1e-20):0;
    result.count=min(result.count,Lighting.w);
    result.material=s.material; result.position=s.p; result.normal=s.n;
    result.depth=length(s.p-CameraOrigin.xyz); result.roughness=s.roughness;
    return result;
}
void Direct(Surface s,float3 view,inout uint rng,bool reuse,uint2 pixel,out float3 diffuse,out float3 specular,out float3 unfiltered,out float3 momentD,out float3 momentS) {
    // Few delta lights are cheaper and sharper with deterministic evaluation; emissive scenes use ReSTIR.
    if(Lighting.x==0 || (AnalyticLightCount()<=2 && Environment.y==0)) {
        DirectClassic(s,view,rng,diffuse,specular,unfiltered,momentD,momentS); return;
    }
    diffuse=specular=unfiltered=momentD=momentS=0;
    float3 factorD,factorS; NRD_MaterialFactors(s.n,view,s.base*(1-s.metallic)*(1-s.transmission),F0(s),s.roughness,factorD,factorS);
    Reservoir reservoir=BuildReservoir(s,view,reuse,rng);
    if(reuse) ReservoirsOut[pixel.y*(uint)Size.x+pixel.x]=reservoir;
    if(reservoir.weight>0) {
        float3 d,r,l; float distance; EvaluateLight(s,view,reservoir.light,d,r,l,distance);
        bool stochastic;
        float maximum=distance-(reservoir.light.area!=0?max(Limits.x*8,distance*1e-5):Limits.x*4);
        float3 visibility=Visibility(Offset(s,l),l,maximum,rng,stochastic,reservoir.light.area==0);
        // Reservoir lighting is stochastic even for punctual lights and must enter the NRD/RR lobe signals.
        diffuse=d*visibility*reservoir.weight; specular=r*visibility*reservoir.weight;
        momentD=l*_NRD_Luminance(diffuse/factorD); momentS=l*_NRD_Luminance(specular/factorS);
    }
    if(Environment.x>0) {
        float z=1-2*Random(rng),phi=2*PI*Random(rng),radius=sqrt(max(0,1-z*z));
        float3 l=float3(radius*cos(phi),z,radius*sin(phi)),d,r; float pdf; Bsdf(s,view,l,d,r,pdf);
        if(any(d+r>0)) {
            bool stochastic; float lightPdf=1/(4*PI);
            float3 energy=Sky(l)*saturate(dot(s.n,l))*Weight(lightPdf,pdf)/lightPdf*s.ao*
                Visibility(Offset(s,l),l,CameraRight.w,rng,stochastic);
            diffuse+=d*energy; specular+=r*energy;
            momentD+=l*_NRD_Luminance(d*energy/factorD); momentS+=l*_NRD_Luminance(r*energy/factorS);
        }
    }
}

uint PhotonHash(int3 cell) {
    uint3 bits=asuint(cell); return (bits.x*73856093u^bits.y*19349663u^bits.z*83492791u)&((uint)CausticSettings.z-1);
}
[shader("raygeneration")]
void ClearPhotonGrid() { PhotonHeads[DispatchRaysIndex().x]=0xffffffff; }
[shader("raygeneration")]
void PhotonGen() {
    uint index=DispatchRaysIndex().x,rng=index*747796405u+9187u;
    uint lightCount=AnalyticLightCount();
    if(lightCount==0) return;
    uint lightIndex=index%lightCount,stratum=index/lightCount;
    uint strata=((uint)CausticSettings.x+lightCount-1-lightIndex)/lightCount;
    Light light=AnalyticLight(lightIndex);
    float3 origin,direction,flux=light.colorIntensity.rgb*light.colorIntensity.w/strata;
    // Stratified 2-D emission, with exact per-light normalization even when N is not divisible by L.
    float uEmission=(stratum+.5)/strata;
    float radius=CausticBounds.w,phi=2*PI*(reversebits(stratum)*2.3283064365386963e-10);
    if(light.positionType.w==0) {
        direction=SafeNormalize(light.directionRange.xyz,float3(0,-1,0));
        float diskRadius=radius*sqrt(uEmission);
        origin=CausticBounds.xyz-direction*(radius+Limits.x*8)+Local(direction,float3(diskRadius*cos(phi),diskRadius*sin(phi),0));
        flux*=PI*radius*radius;
    } else {
        origin=light.positionType.xyz;
        float3 delta=CausticBounds.xyz-origin; float distance=length(delta);
        float cosMax=distance>radius?sqrt(max(0,1-radius*radius/(distance*distance))):-1;
        float z=lerp(1,cosMax,uEmission),r=sqrt(max(0,1-z*z));
        direction=Local(SafeNormalize(delta,float3(0,1,0)),float3(r*cos(phi),r*sin(phi),z));
        flux*=2*PI*(1-cosMax)*SpotFalloff(light,direction);
    }
    if(!any(flux>0)) return;
    float4 media[8]; media[0]=float4(1,0,0,0); uint medium=0;
    bool refracted=false; float traveled=0;
    for(uint bounce=0;bounce<(uint)CausticSettings.w;bounce++) {
        Payload hit=Trace(origin,direction,CameraRight.w,false,rng);
        if(hit.instance==0xffffffff) return;
        traveled+=hit.t;
        Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,0,-direction);
        Material material=Materials[s.material];
        if(medium>0) flux*=exp(-media[medium].yzw*hit.t);
        else if(s.front==0 && s.transmission>0 && s.thickness>0) flux*=exp(-Absorption(material)*hit.t);
        if(s.transmission<=0) {
            // Only paths with a refractive chain before this receiver: no ordinary direct light in this map.
            if(!refracted || material.flags.w!=0) return;
            Photon photon=(Photon)0; photon.position=s.p; photon.normal=s.g; photon.incoming=-direction;
            photon.flux=flux*(light.positionType.w!=0?RangeFalloff(light,traveled):1);
            if(!all(isfinite(photon.flux))) return;
            int3 cell=(int3)floor(s.p/CausticSettings.y);
            InterlockedExchange(PhotonHeads[PhotonHash(cell)],index,photon.next);
            Photons[index]=photon; return;
        }
        float a=s.roughness*s.roughness,u=Random(rng),angle=2*PI*Random(rng);
        float ct=sqrt((1-u)/(1+(a*a-1)*u)),st=sqrt(max(0,1-ct*ct));
        float3 micro=Local(s.n,float3(st*cos(angle),st*sin(angle),ct));
        float vm=dot(-direction,micro); if(vm<=0) return;
        bool volume=s.thickness>0;
        float etaI=medium>0?media[medium].x:(s.front==0 && volume?s.ior:1);
        float etaT=s.front!=0?s.ior:(medium>1?media[medium-1].x:1),eta=etaI/max(etaT,1);
        float fresnel=Fresnel(vm,volume?eta:1/s.ior),pick=Random(rng); bool transmitted=false;
        float3 next;
        if(pick<fresnel) next=reflect(direction,micro);
        else if(pick<fresnel+(1-fresnel)*s.transmission) {
            next=volume?refract(direction,micro,eta):-reflect(direction,micro); transmitted=true;
            if(dot(next,next)<1e-10) { next=reflect(direction,micro); transmitted=false; }
        } else return;
        if((dot(s.g,next)<0)!=transmitted) return;
        flux*=G1(abs(dot(s.n,direction)),a*a)*G1(abs(dot(s.n,next)),a*a)*vm/max(abs(dot(s.n,direction))*abs(dot(s.n,micro)),1e-5);
        if(transmitted) {
            if(volume) {
                refracted=refracted||(s.causticCaster!=0 && s.ior>1.001);
                // Photon power uses importance transport: no radiance-mode eta^2 factor here.
                if(s.front!=0) { if(medium==7) return; media[++medium]=float4(s.ior,Absorption(material)); }
                else if(medium>0) medium--;
            } else flux*=s.base;
        }
        if(!all(isfinite(flux)) || !any(flux>0)) return;
        origin=Offset(s,next); direction=SafeNormalize(next,direction);
    }
}
void GatherCaustics(Surface s,float3 view,out float3 diffuse,out float3 specular,out float3 momentD,out float3 momentS) {
    diffuse=specular=momentD=momentS=0;
    float3 factorD,factorS; NRD_MaterialFactors(s.n,view,s.base*(1-s.metallic)*(1-s.transmission),F0(s),s.roughness,factorD,factorS);
    if(CausticSettings.x==0 || s.transmission>0) return;
    float radius=CausticSettings.y,radius2=radius*radius;
    int3 cell=(int3)floor(s.p/radius);
    [loop] for(int z=-1;z<=1;z++) [loop] for(int y=-1;y<=1;y++) [loop] for(int x=-1;x<=1;x++) {
        int3 neighbor=cell+int3(x,y,z); uint index=PhotonHeads[PhotonHash(neighbor)];
        while(index!=0xffffffff) {
            Photon photon=Photons[index]; index=photon.next;
            // Verify the cell, so hash collisions cannot duplicate flux across the 27 buckets.
            if(any((int3)floor(photon.position/radius)!=neighbor)) continue;
            float3 delta=photon.position-s.p; float distance2=dot(delta,delta);
            if(distance2>=radius2 || dot(photon.normal,s.g)<.9 || abs(dot(delta,s.g))>radius*.1) continue;
            float3 d,r; float pdf; Bsdf(s,view,photon.incoming,d,r,pdf);
            // A spatial photon kernel cannot resolve a near-delta GGX lobe. Filter only the
            // photon specular estimate; direct light, camera BSDFs and NRD roughness stay exact.
            if(s.roughness<.35) {
                Surface filtered=s; filtered.roughness=.35;
                float3 unused; Bsdf(filtered,view,photon.incoming,unused,r,pdf);
            }
            // Normalized Epanechnikov disk kernel: integral over the receiving surface is one.
            float3 energy=photon.flux*(2*(1-distance2/radius2)/(PI*radius2));
            diffuse+=energy*d; specular+=energy*r;
            momentD+=photon.incoming*_NRD_Luminance(energy*d/factorD); momentS+=photon.incoming*_NRD_Luminance(energy*r/factorS);
        }
    }
}
