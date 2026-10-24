// Executed inside one bounded offline DispatchRays using the real production helpers.
void ShaderContracts() {
    uint2 pixel=DispatchRaysIndex().xy;
    uint test=pixel.x%9; float error=0;
    Surface s=(Surface)0; s.n=s.g=float3(0,0,1); s.base=1; s.roughness=.5;
    s.ior=1.5; s.eta=1/1.5; s.front=1; s.bsdfSampling=1; s.thickness=1;
    if(test==0) {
        s.metallic=.5; s.transmission=.5;
        error=length(DiffuseAlbedo(s))+abs(TransmissionProbability(s)-1);
        s.transmission=.25;
        error+=abs(DiffuseAlbedo(s).x-.25)+abs(TransmissionProbability(s)-.5);
    } else if(test==1 || test==2) {
        PathState p=BeginPath(0,float3(0,0,-1),0); s.material=0; s.transmission=1;
        error+=!UpdateMedium(p,s,0); error+=abs(InterfaceEta(p,s,1)-1);
        s.material=1; s.ior=1.33;
        error+=FalseInterface(p,s,1); error+=abs(InterfaceEta(p,s,1)-1.5/1.33);
        error+=!UpdateMedium(p,s,1);
        if(test==1) {
            s.material=0; s.ior=1.5; s.front=0;
            error+=!FalseInterface(p,s,0); error+=!UpdateMedium(p,s,0);
            error+=abs(MediumIor(p,ActiveMedium(p))-1.33);
            s.material=1; s.ior=1.33;
            error+=abs(InterfaceEta(p,s,1)-1.33); error+=!UpdateMedium(p,s,1); error+=p.mediumCount;
        } else {
            error+=!UpdateMedium(p,s,1); error+=abs((float)p.mediumCount-2);
            s.front=0; error+=abs(InterfaceEta(p,s,1)-1.33/1.5);
            error+=!UpdateMedium(p,s,1); error+=abs(MediumIor(p,ActiveMedium(p))-1.5);
        }
    } else if(test==3) {
        uint rng=pixel.y+71;
        for(uint i=0;i<256;i++) {
            float pdf; float3 d=SampleEnvironment(float4(Random(rng),Random(rng),Random(rng),Random(rng)),pdf);
            error=max(error,abs(pdf-1/(4*PI))+abs(pdf-EnvironmentPdf(d))+abs(length(d)-1));
        }
    } else if(test==4) {
        AreaProposal proposal=(AreaProposal)0; proposal.lights=uint4(0,1,1,1); proposal.count=4; proposal.mixture=.75;
        error=abs(EmitterAreaPdf(0,proposal)-.425)+abs(EmitterAreaPdf(1,proposal)-1.575);
        uint rng=69,first=0;
        for(uint i=0;i<4096;i++) first+=SampleEmitter(proposal,rng)==0;
        error+=max(0,abs(first/4096.0-.2125)-.03);
    } else if(test==5) {
        s.transmission=1; s.eta=1.5; s.roughness=.025; float3 view=normalize(float3(.9,0,.43589));
        // Total internal reflection must be included in both continuation and NEE PDFs.
        ScatterResult result=Scatter(s,view,float3(.001,.5,.99));
        float3 d,r; float pdf; Bsdf(s,view,result.direction,d,r,pdf);
        error=abs(pdf-result.pdf)+result.transmitted+!result.valid;
    } else if(test==6) {
        s.metallic=1; s.roughness=0; s.base=float3(.8,.6,.4);
        ScatterResult result=Scatter(s,float3(0,0,1),float3(.2,.3,.4));
        float3 d,r; float pdf; Bsdf(s,float3(0,0,1),float3(0,0,1),d,r,pdf);
        error=!result.valid+!result.delta+length(result.weight-s.base)+length(d+r)+pdf+result.pdf;
        // A smooth zero-thickness sheet transmits along the incoming direction,
        // including oblique views; it must not mirror the tangential component.
        s.metallic=0; s.transmission=1; s.thickness=0; s.base=1;
        float3 view=normalize(float3(.2+pixel.y*.15,.1,1));
        result=Scatter(s,view,float3(.2,.3,.99));
        error+=!result.valid+!result.transmitted+length(result.direction+view);
    } else if(test==7) {
        uint rng=42, expected=pixel.y==3?0:pixel.y+2;
        float x=pixel.y==0?3.25:pixel.y==1?5.75:pixel.y==2?9.25:.25;
        Payload front=Trace(float3(x,.25,.5),float3(0,0,-1),.75,false,rng);
        Payload back=Trace(float3(x,.25,-.5),float3(0,0,1),.75,false,rng);
        error=(front.instance!=expected)+(back.instance!=(pixel.y<2?0xffffffff:expected));
        Raw[pixel]=float4(error==0?1:0,error,front.instance,back.instance); return;
    } else {
        s.roughness=pixel.y==0?.001:pixel.y==1?.003:pixel.y==2?.01:.02;
        float alpha=s.roughness*s.roughness, expected=1/(PI*alpha*alpha);
        float actual=StableNdfGGX(alpha,1);
        float3 d,r; float pdf; Bsdf(s,s.n,s.n,d,r,pdf);
        error=abs(actual/expected-1)+!all(isfinite(d+r))+!isfinite(pdf)+abs(Weight(1e30,1e30)-.5);
    }
    Raw[pixel]=float4(error<1e-4?1:0,error,test,1);
}
