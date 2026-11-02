cbuffer Frame : register(b0) {
    float4 Origin,Forward,Right,Up,Size,Control,Environment,Limits,SunDirection,SunRadiance;
    float4 PreviousOrigin,PreviousForward,PreviousRight,PreviousUp,Sampling;
    float4 Lighting,CausticBounds,CausticSettings,PathSettings,EnvironmentMap;
}
Texture2D<float4> Base : register(t0);
Texture2D<float4> Normal : register(t1);
Texture2D<float> Depth : register(t2);
Texture2D<float4> Emission : register(t3);
RWTexture2D<float4> SampleEmission : register(u3);
RWTexture2D<float4> SampleBase : register(u0);
RWTexture2D<float4> SampleNormal : register(u1);
RWTexture2D<float> SampleDepth : register(u2);
[numthreads(8,8,1)]
void SampleGuides(uint3 tid:SV_DispatchThreadID) {
    int2 p=tid.xy;if(any(p>=(int2)Sampling.xy))return;
    int2 q=p*(int)Sampling.z+(int)floor(Sampling.z*.5);
    if(any(q>=(int2)Size.xy)) {SampleBase[p]=0;SampleNormal[p]=float4(0,1,0,1);SampleDepth[p]=0;SampleEmission[p]=0;return;}
    SampleBase[p]=Base[q];SampleNormal[p]=Normal[q];SampleDepth[p]=Depth[q];SampleEmission[p]=Emission[q];
}
