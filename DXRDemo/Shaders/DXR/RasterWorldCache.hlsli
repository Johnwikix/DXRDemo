// 同 C# RasterWorldCache.Stride：40 个 float4 = 640 字节；64 个方向距离矩打包在 32 个 float4。
struct WorldProbe { float4 cell,position; float4 irradiance[6]; float4 moments[32]; };
struct ProbeRay { float4 radiance,directionDistance; };
StructuredBuffer<WorldProbe> WorldProbes : register(t9);
int3 CacheModulo(int3 cell,int3 size) {return (cell%size+size)%size;}
uint CacheCount() {return (uint)(PreviousForward.x*PreviousForward.y*PreviousForward.z);}
// 三层共用一个缓冲；PreviousForward.w 为总探针数，cell 身份在每层独立验证。
float4 CacheGrid(uint level) {
    float scale=(float)(1u<<level);
    return float4(floor((PreviousOrigin.xyz+PreviousForward.xyz*.5)/scale)-PreviousForward.xyz*.5,PreviousOrigin.w*scale);
}
uint CacheIndex(int3 cell,uint level) {
    int3 q=CacheModulo(cell,(int3)PreviousForward.xyz);
    return level*CacheCount()+(q.z*(uint)PreviousForward.y+q.y)*(uint)PreviousForward.x+q.x;
}
float3 CacheAxis(uint axis) {float3 n=0;n[axis/2]=(axis%2)==0?1:-1;return n;}
int3 CacheCell(uint index) {
    uint count=CacheCount(),level=index/count;index%=count;
    int3 size=(int3)PreviousForward.xyz,origin=(int3)CacheGrid(level).xyz;
    int3 slot=int3(index%size.x,(index/size.x)%size.y,index/(size.x*size.y));
    return origin+CacheModulo(slot-CacheModulo(origin,size),size);
}
// CPU 固定 128/64/64 的三级预算；各层独立轮转，32 帧覆盖全部槽位，总射线数不增加。
uint CacheUpdateSlot(uint update,uint frame,uint nearBudget,uint totalBudget) {
    uint farBudget=(totalBudget-nearBudget)/2;
    uint level=update<nearBudget?0:1+(update-nearBudget)/farBudget;
    uint budget=level==0?nearBudget:farBudget;
    uint offset=level==0?0:nearBudget+(level-1)*farBudget;
    return level*CacheCount()+(frame*budget+update-offset)%CacheCount();
}
float3 CacheDirection(uint bin) {
    float2 xy=(float2(bin%8,bin/8)+.5)/8*2-1;
    float3 n=float3(xy,1-abs(xy.x)-abs(xy.y));
    if(n.z<0)n.xy=(1-abs(n.yx))*float2(n.x>=0?1:-1,n.y>=0?1:-1);
    return normalize(n);
}
float2 CacheMoments(WorldProbe probe,float3 direction) {
    float3 oct=direction/max(dot(abs(direction),1),1e-8);
    if(oct.z<0)oct.xy=(1-abs(oct.yx))*float2(oct.x>=0?1:-1,oct.y>=0?1:-1);
    // 距离矩使用方向最近邻，避免把墙面和无遮挡方向的矩混合成高方差的假可见性。
    uint2 texel=min((uint2)(saturate(oct.xy*.5+.5)*8),7u);
    uint bin=texel.y*8+texel.x;float4 packed=probe.moments[bin/2];
    return (bin&1)==0?packed.xy:packed.zw;
}
struct CacheCandidate {float3 position,irradiance;float weight;};
float3 CacheQueryPosition(float3 world,float3 normal,float spacing) {
    // 共面接收点需要向表面前方偏移；粗层偏移由基础间距封顶。
    return world+normal*min(spacing*.05,PreviousOrigin.w*.1);
}
CacheCandidate CacheNeighbor(float3 world,float3 normal,int3 cell,float trilinear,uint level) {
    CacheCandidate result=(CacheCandidate)0;float4 layout=CacheGrid(level);
    if(any(cell<(int3)layout.xyz)||any(cell>=(int3)(layout.xyz+PreviousForward.xyz)))return result;
    WorldProbe probe=WorldProbes[CacheIndex(cell,level)];
    if(probe.cell.w<=0||any((int3)probe.cell.xyz!=cell))return result;
    float3 delta=world-probe.position.xyz;float distance=length(delta);float3 direction=delta/max(distance,1e-6);
    float2 moments=CacheMoments(probe,direction);
    float variance=max(moments.y-moments.x*moments.x,layout.w*layout.w*1e-4);
    float excess=max(distance-moments.x,0);float visibility=variance/(variance+excess*excess);
    visibility=visibility*visibility*visibility;
    float facing=distance<1e-5?1:pow(saturate(dot(normal,-direction)),2);
    result.weight=trilinear*visibility*facing;result.position=probe.position.xyz;
    float3 weights=abs(normal);weights/=max(weights.x+weights.y+weights.z,1e-6);
    [unroll]for(uint a=0;a<3;a++)result.irradiance+=probe.irradiance[a*2+(normal[a]<0?1:0)].rgb*weights[a];
    return result;
}
float CacheCoverage(float3 world,uint level) {
    float spacing=PreviousOrigin.w*(1u<<level);
    float3 edge=PreviousForward.xyz*.5-1-abs((world-CameraOrigin.xyz)/spacing);
    return saturate(min(edge.x,min(edge.y,edge.z)));
}
float3 CacheLevelIrradiance(float3 world,float3 normal,uint level) {
    float spacing=CacheGrid(level).w;world=CacheQueryPosition(world,normal,spacing);
    float3 grid=world/spacing-.5;int3 base=(int3)floor(grid);float3 f=frac(grid);
    float3 result=0;float sum=0;
    [unroll]for(uint corner=0;corner<8;corner++) {
        int3 offset=int3(corner&1,(corner>>1)&1,(corner>>2)&1);float3 tri=lerp(1-f,f,(float3)offset);
        CacheCandidate probe=CacheNeighbor(world,normal,base+offset,tri.x*tri.y*tri.z,level);
        result+=probe.irradiance*probe.weight;sum+=probe.weight;
    }
    // 全部邻居被遮挡/未完成时保持无效；禁止把极小的穿墙权重归一化为完整照明。
    return sum>.005?max(result/sum,0):0;
}

float3 CacheIrradiance(float3 world,float3 normal) {
    if(PreviousOrigin.w<=0)return 0;
    float3 result=0;float remaining=1;
    [unroll]for(uint level=0;level<3;level++) {
        // 用连续相机位置形成一格过渡带，整格滚动不改变层权重。
        float weight=CacheCoverage(world,level);
        if(weight>0)result+=CacheLevelIrradiance(world,normal,level)*(remaining*weight);
        remaining*=1-weight;
        if(remaining<=0)break;
    }
    // 层选择只依赖空间覆盖，不因遮挡/无效探针改选粗层，避免把薄墙遮挡当成缓存缺失。
    return result;
}
