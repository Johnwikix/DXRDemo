RWTexture2D<float4> Frame : register(u0);
cbuffer HudConstants : register(b0)
{
    float4 HudParams;
};
cbuffer FontBuffer : register(b1)
{
    int Font[896];
};
cbuffer TextBuffer : register(b2)
{
    int Text[532];
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
            int c = Text[row * 76 + col];
            ink = ((uint)Font[c * 7 + gy] & (1u << (4 - gx))) != 0;
        }
    }

    float4 old = Frame[dest];
    Frame[dest] = float4(ink ? HudParams.xxx : old.rgb * 0.24f, old.a);
}
