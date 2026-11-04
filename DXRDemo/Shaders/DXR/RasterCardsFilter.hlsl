cbuffer Frame:register(b0) {
    float4 CameraOrigin,CameraForward,CameraRight,CameraUp,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,PreviousSize;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
static const uint PITCH=24,SIZE=16,PAD=4,COLUMNS=8,QUEUE=256;
struct CardData {uint4 key;float4 center,u,v,n;};
Texture2D<float4> Source : register(t0);
StructuredBuffer<uint2> Stamps : register(t1);
StructuredBuffer<CardData> Metadata : register(t2);
RWTexture2D<float4> A:register(u0),B:register(u1),C:register(u2),D:register(u3),E:register(u4);
[numthreads(8,8,1)]
void PadMaterials(uint3 id:SV_DispatchThreadID) {
    uint job=id.x/PITCH;if(job>=(uint)PathSettings.z||id.y>=PITCH)return;
    uint slot=(uint)(job==0?PathSettings.x:PathSettings.y);
    uint2 local=uint2(id.x%PITCH,id.y),start=uint2(slot%COLUMNS,slot/COLUMNS)*PITCH;
    if(all(local>=PAD)&&all(local<PAD+SIZE))return;
    uint2 p=start+local,q=start+clamp(local,PAD,PAD+SIZE-1);
    A[p]=A[q];B[p]=B[q];C[p]=C[q];D[p]=D[q];E[p]=E[q];
}
[numthreads(8,8,1)]
void Mip(uint3 id:SV_DispatchThreadID) {
    uint w,h;A.GetDimensions(w,h);if(any(id.xy>=uint2(w,h)))return;
    uint sourceWidth,sourceHeight;Source.GetDimensions(sourceWidth,sourceHeight);
    uint sourcePitch=sourceWidth/COLUMNS,destPitch=w/COLUMNS;
    uint2 tile=id.xy/destPitch;uint slot=tile.y*COLUMNS+tile.x;
    uint generation=Metadata[slot].key.w;float4 sum=0;
    [unroll]for(uint tap=0;tap<4;tap++) {
        uint2 p=id.xy*2+uint2(tap&1,tap>>1);float4 value=Source[p];
        if(sourcePitch==PITCH) {
            uint2 stamp=Stamps[p.y*sourceWidth+p.x];
            if(stamp.x!=generation||(uint)CausticBounds.w-stamp.y>128)value=0;
        }
        if(value.w<=0) {A[id.xy]=0;return;}
        sum+=value;
    }
    A[id.xy]=sum*.25;
}
