RWTexture2D<float4> Frame : register(u0);
cbuffer HudConstants : register(b0)
{
    float4 HudParams;
};
cbuffer FontBuffer : register(b1)
{
    // cbuffer 标量数组的元素步长是 16 字节；CPU 上传的是连续 32-bit 元素。
    // 显式打包为四元向量，与 896 * sizeof(int) 的 CPU 布局一致。
    uint4 Font[224];
};
cbuffer TextBuffer : register(b2)
{
    uint4 Text[133];
};

[numthreads(8, 8, 1)]
void Loading(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    Frame.GetDimensions(width, height);
    if (id.x >= width || id.y >= height) return;
    Frame[id.xy] = float4(0.025f, 0.03f, 0.04f, 1.0f);
}

[numthreads(8, 8, 1)]
void Hud(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    Frame.GetDimensions(width, height);
    int2 dest = int2(id.xy) + 8;
    if (dest.x >= int(width) || dest.y >= int(height)) return;

    int x = int(id.x) - 8;
    int y = int(id.y) - 4;
    int scale = int(HudParams.y);
    bool ink = false;
    if (x >= 0 && y >= 0)
    {
        int col = x / (6 * scale);
        int row = y / (9 * scale);
        int gx = x / scale % 6;
        int gy = y / scale % 9;
        if (col < 76 && row < 7 && gx < 5 && gy < 7)
        {
            uint textIndex = uint(row * 76 + col);
            uint c = min(Text[textIndex >> 2][textIndex & 3], 127u);
            uint fontIndex = c * 7 + uint(gy);
            ink = (Font[fontIndex >> 2][fontIndex & 3] & (1u << (4 - gx))) != 0;
        }
    }

    float4 old = Frame[dest];
    Frame[dest] = float4(ink ? HudParams.xxx : old.rgb * 0.24f, old.a);
}
