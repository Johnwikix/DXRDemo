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
StructuredBuffer<float4> FilteredEnvironment : register(t8);
// 与 RasterEnvironment 的固定、有限预积分布局一致；不改变路径追踪的环境 PDF。
float3 EnvironmentLevel(float3 direction, uint level, bool diffuse = false)
{
    uint width = diffuse ? 16u : 128u >> level, height = diffuse ? 8 : max(1u, 64u >> level);
    uint offset = diffuse ? 10923 : 0;
    if (!diffuse) for (uint i=0; i<level; i++) offset += (128u >> i)*max(1u,64u >> i);
    float2 uv = float2(frac(atan2(direction.z,direction.x)/(2*PI)),acos(clamp(direction.y,-1,1))/PI);
    float2 p = uv*float2(width,height)-.5; int2 q=(int2)floor(p); float2 f=frac(p);
    uint x0=(q.x+(int)width) % width, x1=(x0+1)%width;
    uint y0=clamp(q.y,0,(int)height-1), y1=clamp(q.y+1,0,(int)height-1);
    return lerp(lerp(FilteredEnvironment[offset+y0*width+x0].rgb,FilteredEnvironment[offset+y0*width+x1].rgb,f.x),
        lerp(FilteredEnvironment[offset+y1*width+x0].rgb,FilteredEnvironment[offset+y1*width+x1].rgb,f.x),f.y)*Environment.x;
}
float3 PrefilteredSky(float3 direction, float roughness)
{
    float level=saturate(roughness)*7;uint lo=(uint)level;
    return lerp(EnvironmentLevel(direction,lo),EnvironmentLevel(direction,min(lo+1,7u)),frac(level));
}

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
    // 法线在像素内的变化扩大微表面瓣，抑制低分辨率下的亚像素高光闪烁。
    float3 dx=ddx(n),dy=ddy(n);
    float variance=min(.5*(dot(dx,dx)+dot(dy,dy)),.25);
    s.roughness=sqrt(sqrt(saturate(pow(s.roughness,4)+variance)));
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
float3 EnvironmentWeight(Surface s,float3 v)
{
    // GGX split-sum 的解析 DFG 近似；预过滤辐亮度只乘一次 Fresnel/可见性。
    float nv=saturate(dot(s.n,v));
    float4 r=s.roughness*float4(-1,-.0275,-.572,.022)+float4(1,.0425,1.04,-.04);
    float a004=min(r.x*r.x,exp2(-9.28*nv))*r.x+r.y;
    float2 ab=float2(-1.04,1.04)*a004+r.zw;
    return max(lerp(.04,s.base,s.metallic)*ab.x+ab.y,0);
}
float3 Lighting(Surface s,uint2 pixel,float z,bool includeSpecular = true)
{
    float3 v=normalize(CameraOrigin.xyz-s.p);
    float3 f0=lerp(.04,s.base,s.metallic);
    float3 result=s.emission;
    if(includeSpecular||Environment.y==0)result+=EnvironmentLevel(s.n,0,true)*s.base*(1-s.metallic)*(1-s.transmission)*(1-f0)*s.ao;
    if(includeSpecular) result+=PrefilteredSky(reflect(-v,s.n),s.roughness)*EnvironmentWeight(s,v)*s.ao;
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
        color=Lighting(s,p,z,false);
    }
    SceneColor[p]=float4(min(color,65000),1); Motion[p]=clamp(MotionVector(world,sky),-32700,32700); Reactive[p]=0;
}
float ViewDepth(float d) { return Limits.x*Limits.y/(Limits.x+d*(Limits.y-Limits.x)); }
float3 OpaqueSample(float2 pixel)
{
    float2 p=clamp(pixel-.5,0,Size.xy-1); int2 q=(int2)floor(p),r=min(q+1,int2(Size.xy)-1);float2 f=frac(p);
    return lerp(lerp(OpaqueColor[q].rgb,OpaqueColor[int2(r.x,q.y)].rgb,f.x),
        lerp(OpaqueColor[int2(q.x,r.y)].rgb,OpaqueColor[r].rgb,f.x),f.y);
}
// 有限步数、当前帧颜色的屏幕反射；离屏/遮挡缺失时明确回退预过滤环境。
float ScreenReflection(float3 origin,float3 direction,float3 normal,float roughness,out float3 radiance)
{
    radiance=0;
    float z=dot(origin-CameraOrigin.xyz,CameraForward.xyz);
    float footprint=max(z*CameraForward.w*2/Size.y,1e-4);
    float bias=footprint*1.5;
    float3 start=origin+normal*bias;
    float lastDelta=0,previousT=bias;
    [loop]for(uint i=0;i<48;i++) {
        float t=bias+Limits.w*pow((i+1)/48.0,2);
        float3 samplePoint=start+direction*t;float4 clip=mul(float4(samplePoint,1),ViewProjection);
        if(clip.w<=Limits.x) return 0;
        float2 pixel=(clip.xy/clip.w*float2(.5,-.5)+.5)*Size.xy-Size.zw;
        if(any(pixel<1)||any(pixel>=Size.xy-1))return 0;
        float depth=GDepth[(int2)pixel];
        float delta=clip.w-ViewDepth(depth);
        // 必须从可见表面前方穿过深度；不能把已有遮挡背后的起点当成命中。
        if(depth>0&&delta>0&&lastDelta<=0&&i>0) {
            float lo=previousT,hi=t;
            [unroll]for(uint j=0;j<5;j++) {
                float mid=(lo+hi)*.5;float4 c=mul(float4(start+direction*mid,1),ViewProjection);
                float2 q=(c.xy/c.w*float2(.5,-.5)+.5)*Size.xy-Size.zw;
                float d=GDepth[clamp((int2)q,0,int2(Size.xy)-1)];
                if(d==0||c.w<ViewDepth(d))lo=mid;else hi=mid;
            }
            float4 hit=mul(float4(start+direction*hi,1),ViewProjection);
            pixel=(hit.xy/hit.w*float2(.5,-.5)+.5)*Size.xy-Size.zw;
            int2 q=clamp((int2)pixel,0,int2(Size.xy)-1);
            float d=GDepth[q];float error=hit.w-ViewDepth(d);
            float facing=saturate(dot(normalize(GNormal[q].xyz),-direction)*4);
            if(d==0||error<0||error>max(footprint*3,hit.w*.01)||hi<bias*3)return 0;
            radiance=OpaqueSample(pixel);
            float edge=min(min(pixel.x,pixel.y),min(Size.x-pixel.x,Size.y-pixel.y));
            return saturate(edge/8)*facing*(1-smoothstep(.35,.65,roughness));
        }
        lastDelta=depth==0?-Limits.y:delta;previousT=t;
    }
    return 0;
}
[numthreads(8,8,1)]
void ResolveReflections(uint3 tid:SV_DispatchThreadID)
{
    uint2 p=tid.xy;if(any(p>=(uint2)Size.xy))return;
    float depth=GDepth[p];if(depth==0) {SceneColor[p]=OpaqueColor[p];return;}
    float2 ndc=(p+.5+Size.zw)/Size.xy*2-1;
    float3 ray=CameraForward.xyz+CameraRight.xyz*ndc.x*CameraRight.w*CameraForward.w-CameraUp.xyz*ndc.y*CameraForward.w;
    Surface s=(Surface)0;float4 a=GBase[p],n=GNormal[p];
    s.p=CameraOrigin.xyz+ray*ViewDepth(depth);s.n=normalize(n.xyz);s.base=a.rgb;s.metallic=a.w;s.roughness=n.w;s.ao=GEmission[p].w;
    float3 v=normalize(-ray),direction=reflect(-v,s.n),hit=0;
    float confidence=0;
    if(Temporal.w!=0&&s.roughness<.65)confidence=ScreenReflection(s.p,direction,s.n,s.roughness,hit);
    float3 reflection=lerp(PrefilteredSky(direction,s.roughness),hit,confidence)*EnvironmentWeight(s,v)*s.ao;
    SceneColor[p]=float4(min(OpaqueColor[p].rgb+reflection,65000),1);
}
float4 TransparentPixel(Varyings input,bool front:SV_IsFrontFace) : SV_Target0
{
    AlphaTest(input,front);Surface s=Evaluate(input,front);Material m=Materials[Instances[NodeIndex].mesh.z];
    if(m.flags.x==2 && s.alpha<1.0/255)discard;
    float3 color=Lighting(s,(uint2)input.position.xy,dot(s.p-CameraOrigin.xyz,CameraForward.xyz));
    float alpha=m.flags.x==2?s.alpha:1;
    if(s.transmission>0) {
        float3 view=normalize(CameraOrigin.xyz-s.p);float f0=pow((s.ior-1)/(s.ior+1),2),f=f0+(1-f0)*pow(1-saturate(dot(s.n,view)),5);
        float2 uv=input.position.xy/Size.xy;
        float2 offset=float2(dot(s.n,CameraRight.xyz),-dot(s.n,CameraUp.xyz))*s.thickness*(1-1/s.ior)/max(length(CameraOrigin.xyz-s.p),.01);
        float2 q=(uv+offset)*Size.xy;
        float3 absorption=m.attenuation.w>0?pow(max(m.attenuation.rgb,1e-6),s.thickness/m.attenuation.w):1;
        color=lerp(color,PrefilteredSky(reflect(-view,s.n),s.roughness)*f+OpaqueSample(q)*absorption*s.base*(1-f),s.transmission);
    }
    return float4(min(color,65000)*alpha,alpha);
}

struct TransparentGuideOutput { float2 motion:SV_Target0; float reactive:SV_Target1; };
TransparentGuideOutput TransparentGuides(Varyings input,bool front:SV_IsFrontFace)
{
    AlphaTest(input,front);Material m=Materials[Instances[NodeIndex].mesh.z];
    float alpha=m.flags.x==2?saturate(m.baseColor.a*input.color.a*Texture(m.baseMap,input.uv).a):1;
    if(alpha<1.0/255)discard;
    TransparentGuideOutput result;result.motion=MotionVector(input.world,false);result.reactive=alpha*.25;return result;
}
