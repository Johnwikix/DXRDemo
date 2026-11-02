// 同 C# RasterWorldCache.Stride：14 个 float4 = 224 字节。
struct WorldProbe { float4 cell,position; float4 irradiance[6]; float4 moments[6]; };
struct ProbeRay { float4 radiance,directionDistance; };
StructuredBuffer<WorldProbe> WorldProbes : register(t9);
int3 CacheModulo(int3 cell,int3 size) {return (cell%size+size)%size;}
uint CacheIndex(int3 cell) {int3 q=CacheModulo(cell,(int3)PreviousForward.xyz);return (q.z*(uint)PreviousForward.y+q.y)*(uint)PreviousForward.x+q.x;}
float3 CacheAxis(uint axis) {float3 n=0;n[axis/2]=(axis%2)==0?1:-1;return n;}
int3 CacheCell(uint index) {
    int3 size=(int3)PreviousForward.xyz;
    int3 slot=int3(index%size.x,(index/size.x)%size.y,index/(size.x*size.y));
    return (int3)PreviousOrigin.xyz+CacheModulo(slot-CacheModulo((int3)PreviousOrigin.xyz,size),size);
}
float3 CacheIrradiance(float3 world,float3 normal) {
    float spacing=PreviousOrigin.w;if(spacing<=0)return 0;
    float3 grid=world/spacing-.5;int3 base=(int3)floor(grid);float3 f=frac(grid);
    float3 result=0;float sum=0;
    [unroll]for(uint corner=0;corner<8;corner++) {
        int3 offset=int3(corner&1,(corner>>1)&1,(corner>>2)&1);int3 cell=base+offset;
        if(any(cell<(int3)PreviousOrigin.xyz)||any(cell>=(int3)(PreviousOrigin.xyz+PreviousForward.xyz)))continue;
        WorldProbe probe=WorldProbes[CacheIndex(cell)];
        if(probe.cell.w<=0||any((int3)probe.cell.xyz!=cell))continue;
        float3 delta=world-probe.position.xyz;float distance=length(delta);float3 direction=delta/max(distance,1e-6);
        float3 angular=abs(direction);angular/=max(angular.x+angular.y+angular.z,1e-6);
        float2 moments=0;
        [unroll]for(uint a=0;a<3;a++)moments+=probe.moments[a*2+(direction[a]<0?1:0)].xy*angular[a];
        float variance=max(moments.y-moments.x*moments.x,spacing*spacing*1e-4);
        float excess=max(distance-moments.x,0);float visibility=variance/(variance+excess*excess);
        visibility=visibility*visibility*visibility;
        float3 tri=lerp(1-f,f,(float3)offset);
        float facing=distance<1e-5?1:pow(saturate(dot(normal,-direction)),2);
        float w=tri.x*tri.y*tri.z*visibility*facing;
        float3 weights=abs(normal);weights/=max(weights.x+weights.y+weights.z,1e-6);float3 irradiance=0;
        [unroll]for(uint a=0;a<3;a++)irradiance+=probe.irradiance[a*2+(normal[a]<0?1:0)].rgb*weights[a];
        result+=irradiance*w;sum+=w;
    }
    // 全部邻居被遮挡/未完成时保持无效；禁止把极小的穿墙权重归一化为完整照明。
    return sum>.005?max(result/sum,0):0;
}
