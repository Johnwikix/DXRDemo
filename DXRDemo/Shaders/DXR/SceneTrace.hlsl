#include "SceneRayCore.hlsli"
#include "NRD/NRD.hlsli"
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
#include "SceneVisibility.hlsli"
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

// Unlit OptiX features and conventional SR optical geometry. Follow the dominant
// ideal glass/mirror path; NRD/RR keep their separate feature conventions.
void OptixGuide(Surface s,float3 direction,uint instance,out float3 albedo,out float3 normal,out float4 opticalSurface,out uint interfaces) {
    PathState path=BeginPath(CameraOrigin.xyz,direction,CameraForward.w*2/Size.y);
    interfaces=0;
    float3 tint=1,translation=0; float3x3 rotation=float3x3(1,0,0,0,1,0,0,0,1),image=rotation; uint rng=0x4c1539u;
    for(uint bounce=0;bounce<8;bounce++) {
        if(!SplitSurface(s)) break;
        float eta=s.thickness>0?InterfaceEta(path,s,instance):1/s.ior;
        float f=Fresnel(saturate(dot(-path.direction,s.n)),eta);
        float reflection=Luminance(ReflectionFresnel(s,saturate(dot(-path.direction,s.n)),f));
        float transmission=(1-f)*s.transmission*(s.thickness>0?1:Luminance(s.base));
        float diffuse=(1-f)*Luminance(DiffuseAlbedo(s));
        if(diffuse>max(reflection,transmission)) break;
        interfaces++;
        bool transmitted=transmission>reflection;
        float3 outgoing=transmitted?(s.thickness>0?refract(path.direction,s.n,eta):path.direction):reflect(path.direction,s.n);
        if(dot(outgoing,outgoing)<1e-8) { transmitted=false; outgoing=reflect(path.direction,s.n); }
        float3x3 local=InterfaceTransform(s,path.direction,outgoing,transmitted);
        float3x3 positionTransform=local;
        if(transmitted && s.thickness>0) {
            // Preserve tangential coordinates and map the refracted ray back to
            // its incident ray. At normal incidence a slab's apparent thickness
            // is thickness/IOR; pure rotation incorrectly leaves its depth intact.
            float scale=clamp(eta*abs(dot(path.direction,s.n))/max(abs(dot(outgoing,s.n)),1e-6),.001,1000);
            float3 n=s.n;
            positionTransform=float3x3(1,0,0,0,1,0,0,0,1)+(scale-1)*float3x3(n.x*n,n.y*n,n.z*n);
        }
        translation+=mul(image,s.p-mul(positionTransform,s.p)); image=mul(image,positionTransform);
        rotation=mul(rotation,local);
        tint*=transmitted?s.transmission*(s.thickness>0?float3(1,1,1):s.base):F0(s);
        if(transmitted && s.thickness>0 && !UpdateMedium(path,s,instance)) break;
        path.origin=Offset(s,outgoing); path.direction=outgoing;
        Payload hit=Trace(path.origin,path.direction,CameraRight.w,false,rng,true);
        if(hit.instance==0xffffffff) { albedo=0; normal=0; opticalSurface=float4(SafeNormalize(mul(image,path.direction),path.direction),-1); return; }
        s=Evaluate(hit.instance,hit.primitive,hit.bary,hit.t*path.coneSpread,-outgoing); instance=hit.instance;
    }
    albedo=saturate(tint*(DiffuseAlbedo(s)+F0(s)+s.transmission));
    normal=SafeNormalize(mul(rotation,s.n),s.n);
    float3 position=mul(image,s.p)+translation;
    opticalSurface=float4(position,length(position-CameraOrigin.xyz));
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
    bool stableSplit=false,splitGlass=false,srGlass=false;
    uint glassBranches=2,glassStrata=1;
    float4 closestGlassSurface=primarySurface,closestGlassNormal=0;
    if(guide.instance!=0xffffffff) {
        Surface g=Evaluate(guide.instance,guide.primitive,guide.bary,guide.t*CameraForward.w*2/Size.y,-primary);
        feedback.position=g.p; feedback.normal=g.n; feedback.material=g.material; feedback.roughness=g.roughness;
        glass=g.transmission>0;
        stableSplit=PathSettings.w!=0 && !coverage && SplitSurface(g);
        srGlass=((uint)Environment.w&2)!=0 && !coverage && glass && g.roughness<=.05;
        splitGlass=glass && (stableSplit || srGlass);
        if(srGlass && g.transmission<1) glassBranches=3;
        if(stableSplit) {
            StableGuide(g,primary,guide.instance,false,reflectionSurface,reflectionNormal);
            if(glass) StableGuide(g,primary,guide.instance,true,transmissionSurface,transmissionNormal);
        }
        if(!coverage) GatherCaustics(g,-primary,primaryCausticD,primaryCausticS,causticMomentD,causticMomentS);
        primarySurface=float4(g.p,guide.t); primaryNormal=g.n; primaryAlbedo=DiffuseAlbedo(g);
        roughness=g.roughness; primaryMaterial=g.material; reactive=coverage||g.transmission>0?1:0;
        if(((uint)Environment.w&1)!=0 || srGlass) {
            float4 opticalSurface;
            uint opticalInterfaces;
            OptixGuide(g,primary,guide.instance,optixAlbedo,optixNormal,opticalSurface,opticalInterfaces);
            if(srGlass) {
                // Bound splitting to two interfaces, including a slab's exit or
                // a second window pane. Single sheets retain only two paths.
                glassStrata=g.thickness>0 || opticalInterfaces>1?2:1;
                closestGlassSurface=primarySurface; closestGlassNormal=float4(g.n,guide.instance+1);
                primarySurface=opticalSurface; primaryNormal=optixNormal; primaryAlbedo=optixAlbedo;
                float f=Fresnel(saturate(dot(-primary,g.n)),g.eta);
                float t=(1-f)*g.transmission*(g.thickness>0?1:Luminance(g.base));
                float d=(1-f)*Luminance(DiffuseAlbedo(g));
                reactive=clamp(1-max(f,max(t,d))/max(f+t+d,1e-6),.08,.45);
            }
        }
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
    uint pathCount=(uint)Control.x*(transparentPass || splitGlass?glassBranches*glassStrata:1u);
    for(uint sample=0;sample<pathCount;sample++) {
        uint sampleIndex=(uint)Control.z*pathCount+sample;
        float3 rayDirection=primary;
        if(PathSettings.x!=0) {
            float2 sampleNdc=(float2(pixel)+.5+CameraSample(pixel,sampleIndex))/Size.xy*2-1;
            rayDirection=normalize(CameraForward.xyz+CameraRight.xyz*sampleNdc.x*(Size.x/Size.y)*CameraForward.w-CameraUp.xyz*sampleNdc.y*CameraForward.w);
        }
        PathState path=BeginPath(CameraOrigin.xyz,rayDirection,CameraForward.w*2/Size.y);
        float secondaryDistance=0; int firstLobe=-1; float3 firstDirection=0;
        bool hitDistanceRecorded=false,deterministicPath=false;
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
                bool sharpSky=deterministicPath && (hit.transparency&1)==0;
                if(bounce>0 && !sharpSky) value*=FireflyWeight(value,path.fireflyK);
                if(bounce==0 || sharpSky) sumE+=value;
                else AccumulatePath(value,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission);
                break;
            }
            path.coneWidth+=path.coneSpread*hit.t;
            path.distanceSinceScatter+=hit.t;
            Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,path.coneWidth,-path.direction);
            if(s.thickness>0) s.eta=InterfaceEta(path,s,hit.instance);
            Material m=Materials[s.material];
            bool sharpDetail=deterministicPath && (hit.transparency&1)==0;
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
            if(bounce>0 && !sharpDetail) radiance*=FireflyWeight(path.throughput*radiance,path.fireflyK);
            if(bounce==0 || sharpDetail) sumE+=path.throughput*radiance;
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
            float3 deterministicDirect=unfiltered;
            float filter=FireflyWeight(path.throughput*(directD+directS+(sharpDetail?0:unfiltered)),path.fireflyK);
            directD*=filter; directS*=filter; unfiltered*=filter;
            directMomentD*=filter; directMomentS*=filter; cmD*=filter; cmS*=filter;
            if(bounce==0) {
                sumD+=path.throughput*directD; sumS+=path.throughput*directS; sumE+=path.throughput*unfiltered;
                momentD+=directMomentD+cmD; momentS+=directMomentS+cmS;
            } else {
                if(sharpDetail) sumE+=path.throughput*deterministicDirect;
                float3 localRadiance=directD+directS+(sharpDetail?0:unfiltered);
                AccumulatePath(path.throughput*localRadiance,firstLobe,firstDirection,factorD,factorS,sumD,sumS,momentD,momentS,plane,stableReflection,stableTransmission);
            }
            float3 u=ScatterSamples(pixel,sampleIndex,bounce);
            // Stratify first-event glass closures for NRD and conventional SR.
            float splitWeight=1; bool splitSecondary=false;
            if((transparentPass || splitGlass) && bounce==0) {
                if(srGlass && glassBranches==3) {
                    float p=SpecProbability(s,-path.direction),t=TransmissionProbability(s);
                    float pt=(1-p)*t,pd=(1-p)*(1-t);
                    uint branch=sample%3;
                    u.z=branch==0?p*.5:branch==1?p+pt*.5:p+pt+pd*.5;
                    splitWeight=3*(branch==0?p:branch==1?pt:pd);
                } else if(s.transmission==1) {
                    float p=SpecProbability(s,-path.direction);
                    bool reflection=(sample&1u)==0;
                    float pTrans=(1-p)*TransmissionProbability(s);
                    u.z=reflection?p*.5:p+pTrans*.5;
                    splitWeight=2*(reflection?p:pTrans);
                } else u.z=(u.z+(sample&1u))*.5;
            }
            if(srGlass && glassStrata==2 && bounce==1 && s.transmission==1 && s.roughness<.001) {
                float p=SpecProbability(s,-path.direction),pt=(1-p)*TransmissionProbability(s);
                bool reflection=((sample/glassBranches)&1u)==0;
                u.z=reflection?p*.5:p+pt*.5; splitWeight=2*(reflection?p:pt); splitSecondary=true;
            }
            ScatterResult scatter=Scatter(s,-path.direction,u);
            path.previousLightProposal=proposal;
            scatter.weight*=splitWeight;
            if(bounce==0) {
                firstLobe=(int)scatter.lobe; firstDirection=scatter.direction;
                deterministicPath=srGlass && scatter.delta && hit.instance==guide.instance;
                if(stableSplit && hit.instance==guide.instance && scatter.lobe==1) plane=scatter.transmitted?2:1;
            } else deterministicPath=deterministicPath && splitSecondary && scatter.delta && (hit.transparency&1)==0;
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
    NormalRoughness[pixel]=((uint)Environment.w&1)!=0?float4(optixNormal,roughness):float4(primaryNormal/max(max(abs(primaryNormal.x),abs(primaryNormal.y)),abs(primaryNormal.z)),roughness);
    DiffuseFactor[pixel]=float4(factorD,1); SpecularFactor[pixel]=float4(factorS,1);
    GlassSurface[pixel]=srGlass?closestGlassSurface:primarySurface;
    GlassFallback[pixel]=0; GlassNormal[pixel]=srGlass?closestGlassNormal:0;
    SpecularGuide[pixel]=float4(specularAlbedo,mirrorDistance);
    Albedo[pixel]=float4(((uint)Environment.w&1)!=0?optixAlbedo:primaryAlbedo,reactive); Unfiltered[pixel]=float4(sumE,1);
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
