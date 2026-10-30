cbuffer Frame : register(b0) { float4 Origin,Forward,Right,Up,Size; }
Texture2D<float4> Base : register(t0);
Texture2D<float4> Normal : register(t1);
Texture2D<float> Depth : register(t2);
Texture2D<float4> Direct : register(t3);
Texture2D<float4> Diffuse : register(t4);
Texture2D<float4> Specular : register(t5);
RWTexture2D<float4> Color : register(u0);
float ViewZ(float d) {return Origin.w*Right.w/(Origin.w+d*(Right.w-Origin.w));}
[numthreads(8,8,1)]
void Composite(uint3 tid:SV_DispatchThreadID) {
    int2 p=tid.xy;if(any(p>=(int2)Size.xy))return;
    if(Depth[p]==0) {Color[p]=Direct[p];return;}
    float4 n=Normal[p],a=Base[p];float z=ViewZ(Depth[p]);
    float3 diffuse=0,specular=0;float sum=0,specSum=0;
    // 一跳切片的有界空间滤波；颜色/法线/深度守卫防止跨薄墙泄漏。镜面不扩大低粗糙度瓣。
    [unroll]for(int y=-2;y<=2;y++)[unroll]for(int x=-2;x<=2;x++) {
        int2 q=clamp(p+int2(x,y),0,int2(Size.xy)-1);float d=Depth[q];if(d==0)continue;
        float4 m=Normal[q];float dz=abs(ViewZ(d)-z);
        float w=exp(-dz/max(z*.005,1e-4))*pow(saturate(dot(n.xyz,m.xyz)),32)*exp(-(x*x+y*y)*.25);
        diffuse+=Diffuse[q].rgb*w;sum+=w;
        float sw=n.w>.25?w*pow(saturate(1-abs(n.w-m.w)),16):((x==0&&y==0)?1:0);
        specular+=Specular[q].rgb*sw;specSum+=sw;
    }
    float3 factor=a.rgb*(1-a.w)*.96;
    Color[p]=float4(min(Direct[p].rgb+diffuse/max(sum,1e-8)*factor+specular/max(specSum,1e-8),65000),1);
}
