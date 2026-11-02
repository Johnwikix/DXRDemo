RWStructuredBuffer<ProbeRay> WorldRays : register(u0,space4);
struct SurfaceSample {uint4 key;float4 radiance,position;};
StructuredBuffer<SurfaceSample> SurfaceInput : register(t10);
RWStructuredBuffer<SurfaceSample> SurfaceOutput : register(u1,space4);
RWStructuredBuffer<uint> SurfaceStatistics : register(u2,space4);
// 世界缓存探针需要识别实体内部的背面；普通像素/反射的背面剔除约定保持不变。
Payload ProbeTrace(float3 origin,float3 direction,float maximum,inout uint rng) {
    RayDesc ray;ray.Origin=origin;ray.Direction=direction;ray.TMin=Limits.x;ray.TMax=maximum;
    Payload hit=(Payload)0;hit.t=maximum;hit.instance=0xffffffff;hit.random=rng;hit.transparency=16;
#if DXR_USE_SER
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(Scene,RAY_FLAG_NONE,255,ray);
    while(query.Proceed())
        if(AcceptTriangle(query.CandidateInstanceID(),query.CandidatePrimitiveIndex(),query.CandidateTriangleBarycentrics(),query.CandidateTriangleRayT(),direction,hit))query.CommitNonOpaqueTriangleHit();
    if(query.CommittedStatus()==COMMITTED_TRIANGLE_HIT) {
        hit.instance=query.CommittedInstanceID();hit.primitive=query.CommittedPrimitiveIndex();hit.t=query.CommittedRayT();hit.bary=query.CommittedTriangleBarycentrics();
    }
#else
    TraceRay(Scene,RAY_FLAG_NONE,255,0,0,0,ray,hit);
#endif
    rng=hit.random;return hit;
}
float3 ProbeDiffuseLighting(Surface s,inout uint rng) {
    float3 irradiance=ResampledAreaLight(s,rng);
    if(Environment.x>0) {
        float pdf;float3 l=SampleEnvironment(float4(Random(rng),Random(rng),Random(rng),Random(rng)),pdf);
        if(dot(s.g,l)>0&&dot(s.n,l)>0&&pdf>0) {
            bool stochastic;irradiance+=Sky(l)*Visibility(Offset(s,l),l,PreviousUp.z,rng,stochastic)*saturate(dot(s.n,l))/(PI*pdf);
        }
    }
    uint count=(uint)Control.w+(any(SunRadiance.rgb>0)?1:0);
    [loop]for(uint i=0;i<count;i++) {
        Light light=(Light)0;
        if(i<(uint)Control.w)light=Lights[i];else {light.directionRange=float4(-SunDirection.xyz,0);light.colorIntensity=float4(SunRadiance.rgb,1);}
        float3 l=-light.directionRange.xyz;float distance=PreviousUp.z,attenuation=1;
        if(light.positionType.w!=0) {
            l=light.positionType.xyz-s.p;distance=length(l);l/=max(distance,1e-8);attenuation=1/max(distance*distance,1e-8);
            if(light.directionRange.w>0)attenuation*=saturate(1-pow(distance/light.directionRange.w,4));
            if(light.positionType.w==2)attenuation*=pow(saturate((dot(-l,light.directionRange.xyz)-light.spot.y)/max(light.spot.x-light.spot.y,1e-6)),2);
        }
        if(dot(s.g,l)<=0||dot(s.n,l)<=0)continue;
        bool stochastic;irradiance+=light.colorIntensity.rgb*light.colorIntensity.w*attenuation*saturate(dot(s.n,l))/PI*
            Visibility(Offset(s,l),l,distance-Limits.x*4,rng,stochastic,true);
    }
    return s.base*(1-s.metallic)*(1-s.transmission)*.96*irradiance;
}
// 反弹反馈需要真实几何可见性：距离矩只作提议，按其权重选一个探针，追一条有界可见性射线。
// 仅世界探针命中着色调用；主表面漫反射仍不追逐像素 GI 射线。
float3 ValidatedCacheIrradiance(Surface surface,inout uint rng) {
    CacheCandidate selected=(CacheCandidate)0;float total=0,remaining=1;
    [loop]for(uint level=0;level<3;level++) {
        float coverage=CacheCoverage(surface.p,level),mass=remaining*coverage;
        remaining*=1-coverage;
        if(mass>0) {
            float spacing=CacheGrid(level).w;float3 world=CacheQueryPosition(surface.p,surface.n,spacing);
            float3 grid=world/spacing-.5;int3 base=(int3)floor(grid);float3 f=frac(grid);
            CacheCandidate chosen=(CacheCandidate)0;float sum=0;
            [unroll]for(uint corner=0;corner<8;corner++) {
                int3 offset=int3(corner&1,(corner>>1)&1,(corner>>2)&1);float3 tri=lerp(1-f,f,(float3)offset);
                CacheCandidate probe=CacheNeighbor(world,surface.n,base+offset,tri.x*tri.y*tri.z,level);
                sum+=probe.weight;
                if(probe.weight>0&&Random(rng)*sum<probe.weight)chosen=probe;
            }
            if(sum>.005) {
                total+=mass;
                if(Random(rng)*total<mass)selected=chosen;
            }
        }
        if(remaining<=0)break;
    }
    if(total<=0)return 0;
    float3 delta=selected.position-surface.p;float distance=length(delta);
    if(distance<=Limits.x*8)return selected.irradiance*total;
    float3 direction=delta/distance;bool stochastic;
    return selected.irradiance*total*Visibility(Offset(surface,direction),direction,distance-Limits.x*4,rng,stochastic);
}
// 三角形/重心位置缓存：严格验证几何身份和灯光 epoch，不会在近邻薄墙间共享结果。
// 这是一种有界 Surface Lighting 表，不是 Cards 捕获 atlas；材质仍由真实命中求值。
float3 CachedSurfaceLighting(Surface s,Payload hit,inout uint rng,bool eligible) {
    if(Materials[s.material].flags.w!=0)return s.base;
    float3 factor=s.base*(1-s.metallic)*(1-s.transmission)*.96;
    if(!any(factor>0))return 0;
    uint3 key=uint3(hit.instance,hit.primitive,min((uint)(hit.bary.x*16),15u)|(min((uint)(hit.bary.y*16),15u)<<8)|(s.front<<16));
    uint hash=key.x*73856093u^key.y*19349663u^key.z*83492791u;hash^=hash>>16;
    uint slot=hash&((uint)Lighting.z-1),epoch=(uint)Lighting.y,frame=(uint)PreviousRight.w+1;
    SurfaceSample old=SurfaceInput[slot];
    float radius=max(Limits.x*8,PreviousOrigin.w*.01);
    bool match=old.key.w==epoch&&all(old.key.xyz==key)&&distance(s.p,old.position.xyz)<=radius;
    uint ignored;
    if(match) {
        InterlockedAdd(SurfaceStatistics[0],1,ignored);
        if(frame-old.radiance.w<16)return old.radiance.rgb;
    }
    float3 indirect=factor*ValidatedCacheIrradiance(s,rng);
    if(!eligible) {
        InterlockedAdd(SurfaceStatistics[3],1,ignored);
        return match?old.radiance.rgb:indirect;
    }
    // 每个 8 方向分层恰好选择一个方向，最多 256*8 次直接光着色；miss 乘 8 补偿选择概率。
    // 缓存写入未加权的光照，仅下一版本可读取，避免本轮读写反馈与竞争顺序影响当前估计。
    uint reservation;InterlockedAdd(SurfaceStatistics[1],1,reservation);
    float3 direct=ProbeDiffuseLighting(s,rng);
    InterlockedAdd(SurfaceStatistics[2],1,ignored);
    uint observed;InterlockedCompareExchange(SurfaceOutput[slot].key.w,old.key.w,0xffffffff,observed);
    if(observed==old.key.w) {
        SurfaceOutput[slot].key.xyz=key;
        SurfaceOutput[slot].radiance=float4(min(direct+indirect,65000),frame);
        SurfaceOutput[slot].position=float4(s.p,1);
        DeviceMemoryBarrier();InterlockedExchange(SurfaceOutput[slot].key.w,epoch,ignored);
    }
    return match?old.radiance.rgb:indirect+direct*8;
}

[shader("raygeneration")]
void WorldProbeRayGen() {
    uint2 id=DispatchRaysIndex().xy;
    uint slot=CacheUpdateSlot(id.y,(uint)PreviousRight.w,(uint)PreviousRight.x,(uint)PreviousRight.y);int3 cell=CacheCell(slot);WorldProbe old=WorldProbes[slot];
    bool same=old.position.w>0&&all((int3)old.cell.xyz==cell);
    float spacing=CacheGrid(slot/CacheCount()).w;
    float3 origin=same?old.position.xyz:(cell+.5)*spacing;
    uint rng=PathSeed(uint2(slot,id.x),(uint)PreviousRight.w,31);
    ProbeRay output=(ProbeRay)0;
    if(id.x==(uint)PreviousRight.z) {
        if(Environment.y>0) {
            float2 bary;Emitter e=SampleRasterEmitter(rng,bary);
            float3 delta=e.p0.xyz+e.e1.xyz*bary.x+e.e2.xyz*bary.y-origin;float distance=length(delta);float3 l=delta/max(distance,1e-8);
            Surface lamp=Evaluate((uint)e.meta.z,(uint)e.meta.w,bary,0,-l);Material material=Materials[lamp.material];
            if(e.meta.x>0&&(material.flags.z!=0||lamp.front!=0)&&(material.flags.x!=1||lamp.alpha>=material.flags.y)) {
                float pdf=RasterEmitterPdf(e)*distance*distance/max(abs(dot(lamp.g,-l)),1e-8);
                bool stochastic;output.radiance=float4(lamp.emission*(material.flags.x==2?lamp.alpha:1)*Visibility(origin,l,distance-max(Limits.x*8,distance*1e-5),rng,stochastic)*Mis(pdf,1/(4*PI))/(PI*max(pdf,1e-8)),0);
                output.directionDistance=float4(l,distance);
            }
        }
    } else {
        // 全球分层方向，不依赖屏幕像素或相机方向。
        uint rotation=PathSeed(uint2(slot,0),((uint)PreviousRight.w+slot%32)/32,19);
        float z=1-2*(id.x+.5)/PreviousRight.z,phi=2*PI*frac(id.x*.61803398875+Random(rotation));float r=sqrt(max(0,1-z*z));
        float3 l=float3(r*cos(phi),z,r*sin(phi));
        Payload hit=ProbeTrace(origin,l,min(PreviousUp.z,spacing*64),rng);float3 radiance=Sky(l);float distance=hit.t;
        if(hit.instance!=0xffffffff) {
            Surface s=Evaluate(hit.instance,hit.primitive,hit.bary,0,-l);Material material=Materials[s.material];
            if(s.front==0&&material.flags.z==0) {distance=-max(distance,1e-6);radiance=0;}
            else {
                float pdf=0;
                if(s.emitter!=0xffffffff)pdf=RasterEmitterPdf(Emitters[s.emitter])*hit.t*hit.t/max(abs(dot(s.g,-l)),1e-8);
                uint choice=PathSeed(uint2(slot,id.x/8),(uint)PreviousRight.w,37)&7;
                radiance=s.emission*Mis(1/(4*PI),pdf)+CachedSurfaceLighting(s,hit,rng,(id.x&7)==choice);
            }
        }
        bool stochastic;float3 transmission=TransmissionVisibility(origin,l,hit.t-Limits.x*4,rng,stochastic);
        output.radiance=float4(min(max(radiance*transmission,0),65000),0);output.directionDistance=float4(l,distance);
    }
    WorldRays[id.y*((uint)PreviousRight.z+1)+id.x]=output;
}
