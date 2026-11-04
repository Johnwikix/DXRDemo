cbuffer Frame:register(b0) {
    float4 CameraOrigin,CameraForward,CameraRight,CameraUp,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,PreviousSize;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
RWStructuredBuffer<uint> Queue : register(u0);
RWStructuredBuffer<uint2> Flags : register(u1);
RWTexture2D<float4> Radiance : register(u2);
RWStructuredBuffer<uint2> Stamp : register(u3);
[numthreads(8,8,1)]
void ResetFeedback(uint3 id:SV_DispatchThreadID) {
    uint index=id.y*4096+id.x;
    if(index<517)Queue[index]=0;
    if(CausticSettings.z>0&&index<(uint)CausticBounds.x)Flags[index]=0;
}
[numthreads(8,8,1)]
void ClearLight(uint3 id:SV_DispatchThreadID) {
    uint w,h;Radiance.GetDimensions(w,h);if(any(id.xy>=uint2(w,h)))return;
    Radiance[id.xy]=0;Stamp[id.y*w+id.x]=0;
}
[numthreads(8,8,1)]
void PadLight(uint3 id:SV_DispatchThreadID) {
    if(any(id.xy>=24)||CausticSettings.y<0)return;
    uint slot=(uint)CausticSettings.y;uint2 start=uint2(slot%8,slot/8)*24;
    if(all(id.xy>=4)&&all(id.xy<20))return;
    uint2 p=start+id.xy,q=start+clamp(id.xy,4,19);uint width=192;
    Radiance[p]=Radiance[q];Stamp[p.y*width+p.x]=Stamp[q.y*width+q.x];
}
