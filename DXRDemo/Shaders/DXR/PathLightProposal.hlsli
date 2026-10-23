struct LightSample { uint index; float2 bary; uint area; };
// Shared 64-byte cache ABI. Adaptive mode uses selected light IDs as proposals,
// never reuses the reservoir's radiance weight as a lighting estimate.
struct Reservoir {
    LightSample light;
    float weight, target, count; uint material;
    float3 position; float depth;
    float3 normal; float roughness;
};
RWStructuredBuffer<Reservoir> ReservoirsIn : register(u9);
RWStructuredBuffer<Reservoir> ReservoirsOut : register(u10);
struct AreaProposal { uint4 lights; uint count; float mixture; };
bool Reproject(float3 p,out int2 pixel) {
    float3 delta=p-PreviousOrigin.xyz; float z=dot(delta,PreviousForward.xyz);
    float2 ndc=float2(dot(delta,PreviousRight.xyz)/(PreviousForward.w*(PreviousSize.x/PreviousSize.y)),-dot(delta,PreviousUp.xyz)/PreviousForward.w)/max(z,1e-8);
    pixel=(int2)floor((ndc*.5+.5)*PreviousSize.xy-PreviousSize.zw);
    return z>0 && all(pixel>=0) && all(pixel<(int2)PreviousSize.xy);
}
AreaProposal LightProposal(Surface s) {
    AreaProposal result=(AreaProposal)0;
    int2 center;
    if(PathSettings.y==0 || Lighting.y==0 || Environment.y==0 || !Reproject(s.p,center)) return result;
    float footprint=max(length(s.p-CameraOrigin.xyz)*CameraForward.w*2/Size.y,Limits.x*8);
    const int2 offsets[4]={int2(0,0),int2(4,0),int2(0,4),int2(-4,0)};
    [unroll] for(uint i=0;i<4;i++) {
        int2 pixel=center+offsets[i]; if(any(pixel<0)||any(pixel>=(int2)Size.xy)) continue;
        Reservoir old=ReservoirsIn[pixel.y*(uint)Size.x+pixel.x];
        if(old.weight<=0 || old.light.area==0 || old.light.index>=(uint)Environment.y ||
            old.material!=s.material || dot(old.normal,s.n)<.9 || length(old.position-s.p)>footprint*12 ||
            abs(dot(old.position-s.p,s.g))>footprint) continue;
        if(Emitters[old.light.index].meta.x>0) result.lights[result.count++]=old.light.index;
    }
    result.mixture=result.count>0?.75:0;
    return result;
}
float EmitterAreaPdf(uint index,AreaProposal proposal) {
    if(index==0xffffffff || index>=(uint)Environment.y) return 0;
    Emitter emitter=Emitters[index]; if(emitter.meta.x<=0) return 0;
    uint matches=0;
    [unroll] for(uint i=0;i<4;i++) if(i<proposal.count && proposal.lights[i]==index) matches++;
    return (1-proposal.mixture)*emitter.e1.w+proposal.mixture*matches/max(proposal.count*emitter.meta.x,1e-20);
}
uint SampleEmitter(AreaProposal proposal,inout uint rng) {
    if(Random(rng)<proposal.mixture) return proposal.lights[min((uint)(Random(rng)*proposal.count),proposal.count-1)];
    uint count=(uint)Environment.y;
    uint bucket=min((uint)(Random(rng)*count),count-1);
    Emitter alias=Emitters[bucket];
    return Random(rng)<alias.p0.w?bucket:(uint)alias.e2.w;
}
void RecordLightFeedback(inout Reservoir feedback,uint emitter,float3 contribution,inout uint rng) {
    float weight=Luminance(contribution);
    if(weight<=0 || !isfinite(weight)) return;
    feedback.count++; feedback.weight+=weight;
    if(Random(rng)*feedback.weight<weight) { feedback.light.index=emitter; feedback.light.area=1; }
}
