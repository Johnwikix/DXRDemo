#include "SceneTypes.hlsli"
cbuffer RasterFrame : register(b0) {
    row_major float4x4 ViewProjection, PreviousViewProjection;
    float4 CameraOrigin, CameraForward, CameraRight, CameraUp, Size, Temporal;
    float4 SunDirection, SunRadiance, Limits, CascadeSplits;
    row_major float4x4 ShadowViews[16];
    float4 ShadowIds, PreviousOrigin, Environment, EnvironmentMap;
}
cbuffer Draw : register(b1) { uint NodeIndex, ShadowIndex; }
StructuredBuffer<Vertex> Vertices : register(t0);
StructuredBuffer<Instance> Instances : register(t1);
StructuredBuffer<Material> Materials : register(t2);
StructuredBuffer<Light> Lights : register(t3);
RWStructuredBuffer<uint> Clusters : register(u0);
Texture2D<float4> Textures[1024] : register(t0, space1);
SamplerState Samplers[1024] : register(s0, space1);
Texture2D<float4> GBase : register(t0, space2);
Texture2D<float4> GNormal : register(t1, space2);
Texture2D<float4> GEmission : register(t2, space2);
Texture2D<float> GDepth : register(t3, space2);
Texture2DArray<float> SunShadow : register(t4, space2);
Texture2DArray<float> LocalShadow : register(t5, space2);
Texture2D<float4> OpaqueColor : register(t6, space2);
RWTexture2D<float4> SceneColor : register(u1);
RWTexture2D<float2> Motion : register(u2);
RWTexture2D<float> Reactive : register(u3);
#include "SceneEnvironment.hlsli"

struct Varyings {
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float4 uv : TEXCOORD3;
    float4 color : COLOR0;
};
Varyings VertexMain(uint id : SV_VertexID)
{
    Instance node = Instances[NodeIndex];
    // SV_VertexID 不包含 draw 的 BaseVertexLocation；手动 pulling 必须显式加 mesh base。
    Vertex vertex = Vertices[node.mesh.x + id]; Varyings result;
    result.world = mul(vertex.position, node.world).xyz;
    result.normal = mul(float4(vertex.normal.xyz, 0), node.normal).xyz;
    result.tangent = float4(mul(float4(vertex.tangent.xyz, 0), node.world).xyz,
        vertex.tangent.w * (determinant((float3x3)node.world) < 0 ? -1 : 1));
    result.uv = vertex.uv; result.color = vertex.color;
    result.position = mul(float4(result.world, 1), ViewProjection);
    result.position.xy += float2(-2 * Size.z / Size.x, 2 * Size.w / Size.y) * result.position.w;
    return result;
}
Varyings ShadowVertex(uint id : SV_VertexID)
{
    Varyings result = VertexMain(id);
    result.position = mul(float4(result.world, 1), ShadowViews[ShadowIndex]); return result;
}
float4 Texture(Binding binding, float4 uv)
{
    if (binding.info.x < 0) return 1;
    uint index = NonUniformResourceIndex((uint)binding.info.x);
    return Textures[index].SampleBias(Samplers[index], UV(binding, uv), EnvironmentMap.w);
}
bool ObjectFront(bool rasterFront) { return rasterFront != (determinant((float3x3)Instances[NodeIndex].world) < 0); }
void AlphaTest(Varyings input, bool front)
{
    Material m = Materials[Instances[NodeIndex].mesh.z];
    if (m.flags.z == 0 && !ObjectFront(front)) discard;
    if (m.flags.x == 1 && m.baseColor.a * input.color.a * Texture(m.baseMap, input.uv).a < m.flags.y) discard;
}
void DepthPixel(Varyings input, bool front : SV_IsFrontFace) { AlphaTest(input, front); }
struct Surface { float3 p, n, base, emission; float roughness, metallic, alpha, ao, transmission, thickness, ior; };
Surface Evaluate(Varyings input, bool front)
{
    Material m = Materials[Instances[NodeIndex].mesh.z]; Surface s;
    float3 n = normalize(input.normal); if (!ObjectFront(front)) n = -n;
    if (m.normalMap.info.x >= 0) {
        float3 t = input.tangent.xyz; float sign = input.tangent.w < 0 ? -1 : 1;
        if (dot(t,t) < 1e-10) {
            float2 uv = UV(m.normalMap, input.uv), a = ddx(uv), b = ddy(uv);
            float det = a.x*b.y-a.y*b.x;
            t = abs(det) > 1e-10 ? (ddx(input.world)*b.y-ddy(input.world)*a.y)/det : cross(abs(n.y)<.99?float3(0,1,0):float3(1,0,0),n);
            sign = det < 0 ? -1 : 1;
        }
        t = normalize(t - n * dot(n,t));
        float3 mapped = Texture(m.normalMap,input.uv).xyz*2-1; mapped.xy *= m.factors.z;
        n = normalize(t*mapped.x + cross(n,t)*sign*mapped.y + n*mapped.z);
    }
    float4 base = m.baseColor * input.color * Texture(m.baseMap,input.uv);
    float4 mr = Texture(m.mrMap,input.uv);
    s.p=input.world; s.n=n; s.base=max(base.rgb,0); s.alpha=saturate(base.a);
    s.roughness=clamp(m.factors.y*mr.g,.02,1); s.metallic=saturate(m.factors.x*mr.b);
    s.emission=max(m.emissive.rgb*Texture(m.emissiveMap,input.uv).rgb,0);
    s.ao=lerp(1,Texture(m.aoMap,input.uv).r,m.factors.w);
    s.transmission=saturate(m.transmission.x*Texture(m.transmissionMap,input.uv).r);
    s.thickness=max(m.transmission.z*Texture(m.thicknessMap,input.uv).g,0); s.ior=max(m.transmission.y,1.001);
    return s;
}
struct GBuffer { float4 base : SV_Target0; float4 normal : SV_Target1; float4 emission : SV_Target2; };
GBuffer GBufferPixel(Varyings input, bool front : SV_IsFrontFace)
{
    AlphaTest(input, front); Surface s=Evaluate(input,front); GBuffer result;
    result.base=float4(s.base,s.metallic); result.normal=float4(s.n,s.roughness); result.emission=float4(s.emission,s.ao); return result;
}
uint Slice(float z) { return min((uint)(log(max(z,Limits.x)/Limits.x)/log(Limits.y/Limits.x)*24),23u); }
uint ClusterOffset(uint2 pixel, float z) { uint2 tiles=(uint2(Size.xy)+15)/16; return ((Slice(z)*tiles.y+pixel.y/16)*tiles.x+pixel.x/16)*65; }
[numthreads(8,8,1)]
void BuildClusters(uint3 tid : SV_DispatchThreadID)
{
    uint2 tiles=(uint2(Size.xy)+15)/16; if(any(tid.xy>=tiles)||tid.z>=24)return;
    float zn=Limits.x*pow(Limits.y/Limits.x,tid.z/24.0), zf=Limits.x*pow(Limits.y/Limits.x,(tid.z+1)/24.0);
    float2 p0=float2(tid.xy*16)/Size.xy*2-1, p1=float2(min((tid.xy+1)*16,(uint2)Size.xy))/Size.xy*2-1;
    p0.y=-p0.y; p1.y=-p1.y;
    float2 a=float2(p0.x,p1.y)*float2(CameraRight.w,1)*CameraForward.w, b=float2(p1.x,p0.y)*float2(CameraRight.w,1)*CameraForward.w;
    float3 lo=float3(min(a*zn,a*zf),zn), hi=float3(max(b*zn,b*zf),zf);
    uint offset=((tid.z*tiles.y+tid.y)*tiles.x+tid.x)*65,count=0;
    for(uint i=0;i<(uint)Limits.z;i++) {
        Light light=Lights[i]; bool hit=true;
        if(light.positionType.w!=0 && light.directionRange.w>0) {
            float3 d=light.positionType.xyz-CameraOrigin.xyz;
            float3 view=float3(dot(d,CameraRight.xyz),dot(d,CameraUp.xyz),dot(d,CameraForward.xyz));
            float3 delta=view-clamp(view,lo,hi); hit=dot(delta,delta)<=light.directionRange.w*light.directionRange.w;
        }
        if(hit) { if(count<64) Clusters[offset+1+count]=i; count++; }
    }
    Clusters[offset]=count; // 超过预算时光照阶段遍历全部灯，不能静默丢灯。
}
float Shadow(float3 p,float3 n,uint view,bool sun)
{
    float4 clip=mul(float4(p+n*max(Limits.x*.02,1e-5),1),ShadowViews[view]);
    float3 q=clip.xyz/clip.w; float2 uv=q.xy*float2(.5,-.5)+.5;
    if(clip.w<=0||any(uv<=0)||any(uv>=1)||q.z<0||q.z>1)return 1;
    uint width,height,layers; if(sun)SunShadow.GetDimensions(width,height,layers);else LocalShadow.GetDimensions(width,height,layers);
    int2 pixel=(int2)(uv*float2(width,height)); float value=0;
    [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++) {
        int2 tap=clamp(pixel+int2(x,y),0,int2(width,height)-1);
        float d=sun?SunShadow.Load(int4(tap,view,0)):LocalShadow.Load(int4(tap,view-4,0));
        value+=(q.z+2e-5>=d)?1:0;
    }
    return value/9;
}
float SunVisibility(Surface s,float z)
{
    uint cascade=z<CascadeSplits.x?0:z<CascadeSplits.y?1:z<CascadeSplits.z?2:3;
    if(z>CascadeSplits.w)return 1;
    float result=Shadow(s.p,s.n,cascade,true);
    float start=cascade==0?Limits.x:CascadeSplits[cascade-1],end=CascadeSplits[cascade];
    float blend=saturate((z-lerp(start,end,.9))/max((end-start)*.1,1e-6));
    if(cascade<3&&blend>0)result=lerp(result,Shadow(s.p,s.n,cascade+1,true),blend);
    return result;
}
uint CubeFace(float3 direction) {
    float3 a=abs(direction); return a.x>=a.y&&a.x>=a.z?(direction.x>0?0:1):a.y>=a.z?(direction.y>0?2:3):(direction.z>0?4:5);
}
float3 Brdf(Surface s,float3 v,float3 l)
{
    float3 h=normalize(v+l); float nl=saturate(dot(s.n,l)),nv=max(saturate(dot(s.n,v)),1e-4),nh=saturate(dot(s.n,h));
    float a=s.roughness*s.roughness,a2=a*a,d=a2/(PI*pow(max(nh*nh*(a2-1)+1,1e-5),2));
    float k=(s.roughness+1)*(s.roughness+1)/8;
    float g=nv/(nv*(1-k)+k)*nl/(nl*(1-k)+k);
    float3 f0=lerp(.04,s.base,s.metallic),f=f0+(1-f0)*pow(1-saturate(dot(v,h)),5);
    return ((1-f)*s.base*(1-s.metallic)*(1-s.transmission)/PI+f*d*g/max(4*nv*nl,1e-5))*nl;
}
float3 Lighting(Surface s,uint2 pixel,float z)
{
    float3 v=normalize(CameraOrigin.xyz-s.p);
    float3 result=s.emission+(Sky(s.n)*s.base*(1-s.metallic)*.2+Sky(reflect(-v,s.n))*lerp(.04,s.base,s.metallic)*.2)*s.ao;
    if(any(SunRadiance.rgb>0)) result+=Brdf(s,v,SunDirection.xyz)*SunRadiance.rgb*SunVisibility(s,z);
    uint offset=ClusterOffset(pixel,z),count=Clusters[offset]; bool overflow=count>64;
    for(uint j=0;j<(overflow?(uint)Limits.z:count);j++) {
        uint i=overflow?j:Clusters[offset+1+j]; Light light=Lights[i]; float3 l=-light.directionRange.xyz; float attenuation=1;
        if(light.positionType.w!=0) {
            l=light.positionType.xyz-s.p; float distance=length(l); l/=max(distance,1e-8); attenuation=1/max(distance*distance,1e-8);
            if(light.directionRange.w>0)attenuation*=saturate(1-pow(distance/light.directionRange.w,4));
            if(light.positionType.w==2)attenuation*=pow(saturate((dot(-l,light.directionRange.xyz)-light.spot.y)/max(light.spot.x-light.spot.y,1e-6)),2);
        }
        float visibility=1;
        if(ShadowIds.z>=0 && i==(uint)ShadowIds.z)visibility=SunVisibility(s,z);
        [unroll]for(uint slot=0;slot<2;slot++)if(ShadowIds[slot]>=0 && i==(uint)ShadowIds[slot]) {
            uint face=light.positionType.w==2?0:CubeFace(s.p-light.positionType.xyz); visibility=Shadow(s.p,s.n,4+slot*6+face,false);
        }
        result+=Brdf(s,v,l)*light.colorIntensity.rgb*light.colorIntensity.w*attenuation*visibility;
    }
    return max(result,0);
}
float2 MotionVector(float3 p,bool sky)
{
    float4 current=mul(float4(p,1),ViewProjection);
    float4 previous=mul(float4(sky?PreviousOrigin.xyz+(p-CameraOrigin.xyz):p,1),PreviousViewProjection);
    if(Temporal.z!=0||previous.w<=0)return 0;
    return (previous.xy/previous.w-current.xy/current.w)*float2(Size.x*.5,-Size.y*.5);
}
[numthreads(8,8,1)]
void DeferredLighting(uint3 tid:SV_DispatchThreadID)
{
    uint2 p=tid.xy; if(any(p>=(uint2)Size.xy))return;
    float2 ndc=(p+.5+Size.zw)/Size.xy*2-1;
    float3 ray=CameraForward.xyz+CameraRight.xyz*ndc.x*CameraRight.w*CameraForward.w-CameraUp.xyz*ndc.y*CameraForward.w;
    float depth=GDepth[p]; bool sky=depth==0;
    float z=Limits.x*Limits.y/(Limits.x+depth*(Limits.y-Limits.x));
    float3 world=CameraOrigin.xyz+ray*(sky?1:z);
    float3 color;
    if(sky)color=Sky(normalize(ray));
    else {
        Surface s=(Surface)0;float4 a=GBase[p],n=GNormal[p],e=GEmission[p];
        s.p=world;s.n=normalize(n.xyz);s.base=a.rgb;s.metallic=a.w;s.roughness=n.w;s.emission=e.rgb;s.ao=e.w;
        color=Lighting(s,p,z);
    }
    SceneColor[p]=float4(min(color,65000),1); Motion[p]=clamp(MotionVector(world,sky),-32700,32700); Reactive[p]=0;
}
struct TransparentOutput { float4 color:SV_Target0; float2 motion:SV_Target1; float reactive:SV_Target2; };
TransparentOutput TransparentPixel(Varyings input,bool front:SV_IsFrontFace)
{
    AlphaTest(input,front);Surface s=Evaluate(input,front);Material m=Materials[Instances[NodeIndex].mesh.z];
    float3 color=Lighting(s,(uint2)input.position.xy,dot(s.p-CameraOrigin.xyz,CameraForward.xyz));
    float alpha=m.flags.x==2?s.alpha:1;
    if(s.transmission>0) {
        float3 view=normalize(CameraOrigin.xyz-s.p);float f0=pow((s.ior-1)/(s.ior+1),2),f=f0+(1-f0)*pow(1-saturate(dot(s.n,view)),5);
        float2 uv=input.position.xy/Size.xy;
        float2 offset=float2(dot(s.n,CameraRight.xyz),-dot(s.n,CameraUp.xyz))*s.thickness*(1-1/s.ior)/max(length(CameraOrigin.xyz-s.p),.01);
        int2 q=clamp((int2)((uv+offset)*Size.xy),0,int2(Size.xy)-1);
        float3 absorption=m.attenuation.w>0?pow(max(m.attenuation.rgb,1e-6),s.thickness/m.attenuation.w):1;
        color=lerp(color,Sky(reflect(-view,s.n))*f+OpaqueColor[q].rgb*absorption*s.base*(1-f),s.transmission);
    }
    TransparentOutput result;result.color=float4(min(color,65000)*alpha,alpha);result.motion=MotionVector(s.p,false);result.reactive=1;return result;
}
