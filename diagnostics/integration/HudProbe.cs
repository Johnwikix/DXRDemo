using System.Runtime.InteropServices;
using DXRDemo.DXR;
using DXRDemo.Hdr;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

internal static class HudProbe
{
    internal static unsafe void Run()
    {
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        using var effects = new GpuFrameEffects(device);
        using var target = device.CreateTexture2D<Rgba64>(160, 100);
        using var font = DxrResources.CreateUploadBuffer(device.Device, 128 * 7 * 4);
        using var text = DxrResources.CreateUploadBuffer(device.Device, 2304);
        int[] fontRows = new int[128 * 7];
        int[] characters = new int[576];
        int[] a = [14, 17, 17, 31, 17, 17, 17];
        a.CopyTo(fontRows, 'A' * 7);
        characters[0] = characters[5] = characters[76 + 3] = 'A';
        void Write(ID3D12Resource resource, int[] values)
        {
            void* pointer;
            resource.Map(0, null, &pointer).CheckError();
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(new Span<byte>(pointer, values.Length * 4));
            resource.Unmap(0);
        }
        Write(font, fontRows); Write(text, characters);
        foreach (int scale in new[] { 1, 2 })
        foreach (float white in new[] { .92f, .58f })
        {
            effects.DrawLoading(target);
            effects.DrawHud(target, font, text, white, scale, 120, 80);
            Rgba64[,] pixels = target.ToArray();
            foreach (var (row, col) in new[] { (0, 0), (0, 5), (1, 3) })
            for (int y = 0; y < 7; y++)
            for (int x = 0; x < 5; x++)
            {
                // 独立的 5×7 字形参考，同时覆盖紧邻、非四对齐字符和第二行。
                bool ink = (a[y] & (1 << (4 - x))) != 0;
                Rgba64 actual = pixels[12 + (row * 9 + y) * scale, 16 + (col * 6 + x) * scale];
                if (ink && Math.Abs(actual.R / 65535.0 - white) > 2.0 / 65535 || !ink && actual.R / 65535.0 > .02)
                    throw new InvalidOperationException($"HUD glyph mismatch: scale={scale}, white={white}, row={row}, col={col}, x={x}, y={y}, ink={ink}, actual={actual.R / 65535.0:F6}");
            }
        }
        using var hud = new GpuDiagnostics(device);
        using var frame = device.CreateTexture2D<Rgba64>(640, 240);
        effects.DrawLoading(frame);
        hud.Draw(frame, new Runner(), HdrRenderParameters.Default, 1, 16);
        int lit = 0;
        foreach (Rgba64 pixel in frame.ToArray()) if (pixel.R > 50000) lit++;
        if (lit < 50) throw new InvalidOperationException($"Production GPU HUD contains no readable glyphs: {lit} lit pixels");
        for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = debug.GetMessage(i);
            if (message.Severity is MessageSeverity.Error or MessageSeverity.Corruption) throw new InvalidOperationException(message.Description);
        }
        Console.WriteLine($"PASS: HUD packed font/text ABI, 1x/2x glyphs, SDR/HDR encoded whites, production HUD {lit} lit pixels; D3D12 Error/Corruption=0.");
    }

    private sealed class Runner : IHdrShaderRunner, IRenderDiagnostics
    {
        public string DiagnosticText => "ABC 0123\nD3D12";
        public bool TryExecute(GpuTexture<Rgba64> texture, int width, int height, TimeSpan timespan, object? parameter) => false;
    }
}
