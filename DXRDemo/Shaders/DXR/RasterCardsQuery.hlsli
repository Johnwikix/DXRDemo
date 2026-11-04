// 与 RasterCardAtlas 的固定 tile/描述符布局同步；只在光栅世界缓存路径启用。
static const uint CARD_SIZE=16,CARD_PAD=4,CARD_PITCH=24,CARD_COLUMNS=8,CARD_CAPACITY=64,CARD_QUEUE=256;
struct CardData {uint4 key;float4 center,u,v,n;};
struct CardBounds {float4 minimum,maximum;};
StructuredBuffer<uint> CardMap : register(t0,space5);
StructuredBuffer<CardData> Cards : register(t1,space5);
Texture2D<float4> CardBase : register(t2,space5);
Texture2D<float4> CardNormal : register(t3,space5);
Texture2D<float4> CardEmission : register(t4,space5);
Texture2D<float4> CardPosition : register(t5,space5);
Texture2D<float4> CardGeometry : register(t6,space5);
Texture2D<float4> CardRadiance : register(t7,space5);
StructuredBuffer<uint2> CardStamp : register(t8,space5);
StructuredBuffer<CardBounds> CardInstanceBounds : register(t9,space5);
RWStructuredBuffer<uint> CardRequests : register(u0,space5);
RWStructuredBuffer<uint2> CardRequestFlags : register(u1,space5);
RWTexture2D<float4> CardNextRadiance : register(u2,space5);
RWStructuredBuffer<uint2> CardNextStamp : register(u3,space5);
uint2 CardTile(uint slot) {return uint2(slot%CARD_COLUMNS,slot/CARD_COLUMNS)*CARD_PITCH;}
uint CardAddress(uint2 pixel) {return pixel.y*(CARD_COLUMNS*CARD_PITCH)+pixel.x;}
void CardFeedback(uint key) {
    uint clock=(uint)CausticBounds.w,tag=CardRequestFlags[key].x;
    if(tag==clock) {
        uint index=CardRequestFlags[key].y,ignored;
        if(index<CARD_QUEUE)InterlockedAdd(CardRequests[1+CARD_QUEUE+index],1,ignored);
        return;
    }
    if(tag==0xffffffff)return;
    uint previous;InterlockedCompareExchange(CardRequestFlags[key].x,tag,0xffffffff,previous);
    if(previous!=tag)return;
    uint index;InterlockedAdd(CardRequests[0],1,index);
    if(index<CARD_QUEUE) {CardRequests[index+1]=key;CardRequests[1+CARD_QUEUE+index]=1;}
    CardRequestFlags[key].y=index;
    DeviceMemoryBarrier();InterlockedExchange(CardRequestFlags[key].x,clock,previous);
}

float4 CardMip(uint slot,float2 uv,uint mip) {
    uint pitch=CARD_PITCH>>mip;int2 start=(int2)CardTile(slot)>>mip;
    float2 q=(CardTile(slot)+CARD_PAD+uv*CARD_SIZE)/(1u<<mip)-.5;
    int2 p=(int2)floor(q);float2 f=frac(q);float4 color=0;
    [unroll]for(uint tap=0;tap<4;tap++) {
        int2 offset=int2(tap&1,tap>>1);float2 w=lerp(1-f,f,(float2)offset);
        int2 pixel=clamp(p+offset,start,start+(int)pitch-1);
        float4 sample=CardRadiance.Load(int3(pixel,mip));
        if(sample.w<=0)return 0;
        color+=sample*(w.x*w.y);
    }
    return color;
}
bool TryCardLighting(Surface surface,Payload hit,out float3 radiance) {
    radiance=0;if(PathSettings.w<=0)return false;
    CardBounds bounds=CardInstanceBounds[hit.instance];if(bounds.minimum.w<=0)return false;
    float3 local=surface.localP;
    float3 normal=surface.localG*(surface.front!=0?1:-1),absolute=abs(normal);
    uint a=absolute.x>=absolute.y&&absolute.x>=absolute.z?0:absolute.y>=absolute.z?1:2;
    uint axis=a*2+(normal[a]<0?1:0);
    float3 extent=bounds.maximum.xyz-bounds.minimum.xyz;
    uint layers=extent[a]>max(max(extent.x,extent.y),extent.z)*1e-4?4:1;
    uint layer=min((uint)(saturate((local[a]-bounds.minimum[a])/max(extent[a],1e-8))*layers),layers-1);
    uint key=hit.instance*24+axis*4+layer;CardFeedback(key);
    uint mapped=CardMap[key],ignored;
    if(mapped==0) {InterlockedAdd(CardRequests[2+CARD_QUEUE*2],1,ignored);return false;}
    uint slot=mapped-1;CardData card=Cards[slot];
    if(card.key.x!=hit.instance||card.key.z!=axis||card.key.w==0)return false;
    float3 delta=local-card.center.xyz;
    // 仅实际驻留命中才读取实例矩阵；深度核验用一次平面变换，避免四个 tap 各自做逆变换。
    float3 depthNormal=mul(float4(card.n.xyz,0),Instances[hit.instance].normal).xyz;
    float2 uv=float2(dot(delta,card.u.xyz)/card.u.w,-dot(delta,card.v.xyz)/card.v.w)*.5+.5;
    if(any(uv<0)||any(uv>1))return false;
    float2 sample=CardTile(slot)+CARD_PAD+uv*CARD_SIZE-.5;int2 pixel=(int2)floor(sample);float2 f=frac(sample);
    float3 result=0;float weight=0;
    float tolerance=max(1e-5,max(card.u.w,card.v.w)/CARD_SIZE*.02);
    [unroll]for(uint tap=0;tap<4;tap++) {
        int2 offset=int2(tap&1,tap>>1),p=pixel+offset;
        float2 w=lerp(1-f,f,(float2)offset);float tri=w.x*w.y;
        if(tri<=0)continue;
        uint2 stamp=CardStamp[CardAddress(p)];
        if(stamp.x!=card.key.w||(uint)CausticBounds.w-stamp.y>128)continue;
        float4 position=CardPosition[p];if(position.w==0||(position.w>0)!=(surface.front!=0))continue;
        if(abs(dot(position.xyz-surface.p,depthNormal))>tolerance)continue;
        if(dot(CardGeometry[p].xyz,surface.g)<.98)continue;
        if(dot(CardNormal[p].xyz,surface.n)<.9)continue;
        float4 value=CardRadiance.Load(int3(p,0));if(value.w<=0)continue;
        result+=value.rgb*tri;weight+=tri;
    }
    if(weight<.95) {InterlockedAdd(CardRequests[3+CARD_QUEUE*2],1,ignored);return false;}
    result/=weight;
    // 受控 mip 只在整片有效时使用；孔洞/深度断层保留 base 级有效样本或回退命中着色。
    uint mip=(uint)clamp(log2(max(min(hit.t*.1,PreviousOrigin.w*.1)/max(card.center.w,1e-8),1)),0,2);
    if(mip>0) {float4 filtered=CardMip(slot,uv,mip);if(filtered.w>.99)result=filtered.rgb;}
    InterlockedAdd(CardRequests[1+CARD_QUEUE*2],1,ignored);radiance=max(result,0);return true;
}
