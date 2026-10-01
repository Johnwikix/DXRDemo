#include "NeuralRadianceCache.hlsli"
cbuffer Training : register(b1) { uint SampleCount, TrainingStep, Unused0, Unused1; }

[numthreads(64,1,1)]
void ResetNetwork(uint index : SV_DispatchThreadID) {
    if(index<NRC_WEIGHTS) {
        bool bias=index%33==32;
        float random=(NrcHash(index+9187)&0xffffff)/16777216.0*2-1;
        NrcNetwork[index]=float4(bias?0:random*(index<NRC_OUTPUT_OFFSET?.306186:.01),0,0,0);
    }
    if(index<NRC_CELLS+4) NrcCoverage[index]=0;
    if(index<256) NrcSamples[index]=(NrcSample)0;
}
[numthreads(64,1,1)]
void ClearBatch(uint index : SV_DispatchThreadID) {
    if(index<NRC_MAX_SAMPLES) NrcSamples[index]=(NrcSample)0;
    if(index==0) {
        NrcCoverage[NRC_QUERIES]=0; NrcCoverage[NRC_VALID]=0; NrcCoverage[NRC_TOTAL]=0; NrcCoverage[NRC_ERROR]=0;
    }
}
[numthreads(32,1,1)]
void TrainSamples(uint sampleIndex : SV_DispatchThreadID) {
    if(sampleIndex>=SampleCount) return;
    NrcSample s=NrcSamples[sampleIndex]; uint offset=sampleIndex*NRC_WEIGHTS;
    if(s.target.w==0) {
        [loop] for(uint i=0;i<NRC_WEIGHTS;i++) NrcGradients[offset+i]=0;
        return;
    }
    float x[32],hidden[32]; NrcEncode(s,x);
    float3 prediction=NrcForward(x,hidden),error=prediction-s.target.xyz/NRC_RADIANCE_SCALE;
    // Linear radiance L2: zero targets participate, no log-domain/geometric-mean bias.
    float3 delta=2*error/3;
    [unroll] for(uint c=0;c<3;c++) {
        [loop] for(uint h=0;h<NRC_HIDDEN;h++) NrcGradients[offset+NRC_OUTPUT_OFFSET+c*33+h]=delta[c]*hidden[h];
        NrcGradients[offset+NRC_OUTPUT_OFFSET+c*33+32]=delta[c];
    }
    [loop] for(uint h=0;h<NRC_HIDDEN;h++) {
        float back=0;
        [unroll] for(uint c=0;c<3;c++) back+=NrcNetwork[NRC_OUTPUT_OFFSET+c*33+h].x*delta[c];
        back=hidden[h]>0?back:0;
        [loop] for(uint i=0;i<NRC_INPUTS;i++) NrcGradients[offset+h*33+i]=back*x[i];
        NrcGradients[offset+h*33+32]=back;
    }
    InterlockedAdd(NrcCoverage[NrcCell(s)],1);
    InterlockedAdd(NrcCoverage[NRC_VALID],1); InterlockedAdd(NrcCoverage[NRC_TOTAL],1);
    InterlockedAdd(NrcCoverage[NRC_ERROR],(uint)(min(dot(error,error),1000)*4096));
}
[numthreads(64,1,1)]
void OptimizeWeights(uint index : SV_DispatchThreadID) {
    if(index>=NRC_WEIGHTS || NrcCoverage[NRC_VALID]==0) return;
    float gradient=0;
    [loop] for(uint s=0;s<SampleCount;s++) gradient+=NrcGradients[s*NRC_WEIGHTS+index];
    gradient=clamp(gradient/NrcCoverage[NRC_VALID],-1,1);
    float4 state=NrcNetwork[index];
    state.y=.9*state.y+.1*gradient; state.z=.999*state.z+.001*gradient*gradient;
    float m=state.y/(1-pow(.9,(float)TrainingStep)),v=state.z/(1-pow(.999,(float)TrainingStep));
    state.x-=.002*m/(sqrt(v)+1e-8);
    NrcNetwork[index]=state;
}
