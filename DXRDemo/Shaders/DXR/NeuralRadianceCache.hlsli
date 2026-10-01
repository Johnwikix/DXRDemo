// Experimental online NRC, shared by DXR inference and compute backpropagation.
// ABI constants also live in NeuralRadianceCache.cs. This is not the NVIDIA NRC SDK.
#ifndef DXR_NEURAL_RADIANCE_CACHE
#define DXR_NEURAL_RADIANCE_CACHE
static const uint NRC_INPUTS=32, NRC_HIDDEN=32, NRC_OUTPUT_OFFSET=1056, NRC_WEIGHTS=1155, NRC_MAX_SAMPLES=256;
static const uint NRC_CELLS=3072; // 8^3 positions x 6 dominant normal directions.
static const uint NRC_QUERIES=3072, NRC_VALID=3073, NRC_TOTAL=3074, NRC_ERROR=3075;
static const float NRC_RADIANCE_SCALE=16;
// 80 bytes. Positions are normalized by the scene's largest bounds extent.
struct NrcSample { float4 position, normal, view, albedo, target; };
// x = trainable weight; y/z = Adam first/second moment. Read-only during DispatchRays.
RWStructuredBuffer<float4> NrcNetwork : register(u20);
RWStructuredBuffer<NrcSample> NrcSamples : register(u21);
RWStructuredBuffer<float> NrcGradients : register(u22);
RWStructuredBuffer<uint> NrcCoverage : register(u23);

uint NrcHash(uint value) {
    value^=value>>16; value*=0x7feb352du; value^=value>>15;
    value*=0x846ca68bu; return value^(value>>16);
}
uint NrcCell(NrcSample s) {
    uint3 p=(uint3)(saturate(s.position.xyz)*7.999);
    float3 n=abs(s.normal.xyz);
    uint axis=n.x>n.y && n.x>n.z?0:(n.y>n.z?1:2);
    uint face=axis*2+(s.normal[axis]<0?1:0);
    return ((p.z*8+p.y)*8+p.x)*6+face;
}
void NrcEncode(NrcSample s,out float x[32]) {
    [unroll] for(uint axis=0;axis<3;axis++) {
        x[axis]=s.position[axis]*2-1;
        [unroll] for(uint band=0;band<3;band++) {
            float phase=s.position[axis]*6.28318530718*(1u<<band);
            x[3+axis*6+band*2]=sin(phase); x[4+axis*6+band*2]=cos(phase);
        }
        x[21+axis]=s.normal[axis]; x[24+axis]=s.view[axis];
        x[27+axis]=s.albedo[axis];
    }
    x[30]=s.albedo.w; x[31]=s.normal.w;
}
float3 NrcForward(float x[32],out float hidden[32]) {
    [loop] for(uint h=0;h<NRC_HIDDEN;h++) {
        float value=NrcNetwork[h*33+32].x;
        [loop] for(uint i=0;i<NRC_INPUTS;i++) value+=NrcNetwork[h*33+i].x*x[i];
        hidden[h]=max(value,0);
    }
    float3 result=0;
    [unroll] for(uint c=0;c<3;c++) {
        result[c]=NrcNetwork[NRC_OUTPUT_OFFSET+c*33+32].x;
        [loop] for(uint h=0;h<NRC_HIDDEN;h++) result[c]+=NrcNetwork[NRC_OUTPUT_OFFSET+c*33+h].x*hidden[h];
    }
    return result;
}
float3 NrcPredict(NrcSample s) {
    float x[32],hidden[32]; NrcEncode(s,x);
    return max(NrcForward(x,hidden)*NRC_RADIANCE_SCALE,0);
}
#endif
