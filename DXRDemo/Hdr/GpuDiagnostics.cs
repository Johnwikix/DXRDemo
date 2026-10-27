using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DXRDemo.DXR;
using Vortice.Direct3D12;

namespace DXRDemo.Hdr;

internal sealed class GpuDiagnostics : IDisposable
{
    private const int Columns = 76, Rows = 7;
    private readonly ID3D12Resource _font, _text;
    private readonly GpuFrameEffects _effects;
    private readonly int[] _characters = new int[Columns * Rows];
    private long _start = Stopwatch.GetTimestamp(), _frames, _lastPresented;
    private int _columns = 48, _rows = 6;
    private double _frameMs;

    public GpuDiagnostics(DxrDevice device)
    {
        _effects = new(device);
        _font = DxrResources.CreateUploadBuffer(device.Device, 128 * 7 * sizeof(int), "HUD font");
        _text = DxrResources.CreateUploadBuffer(device.Device, (Columns * Rows * sizeof(int) + 255) & ~255, "HUD text");
        Write(_font, CreateFont());
        Write(_text, _characters);
    }

    public void Draw(GpuTexture<Rgba64> texture, IHdrShaderRunner runner, HdrRenderParameters hdr, long presented, double frameMs)
    {
        _frames++;
        _frameMs += frameMs;
        double seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        if (seconds >= 0.5 || _frames == 1 && _characters[0] == 0)
        {
            string description = runner is IRenderDiagnostics diagnostics ? diagnostics.DiagnosticText : "D3D12";
            string text = description + "\n" + FormattableString.Invariant($"RENDER {_frames / Math.Max(seconds, 0.001):F1} FPS   SUBMIT {(presented - _lastPresented) / Math.Max(seconds, 0.001):F1} FPS")
                + "\n" + FormattableString.Invariant($"FRAME {_frameMs / _frames:F2} MS   {texture.Width} X {texture.Height}   {(hdr.IsHdrEnabled ? "HDR10" : "SDR")}")
                + "\nUNLIMITED";
            Array.Clear(_characters);
            int row = 0, col = 0, longest = 0;
            foreach (char c in text.ToUpper(CultureInfo.InvariantCulture))
            {
                if (c == '\n') { longest = Math.Max(longest, col); row++; col = 0; if (row >= Rows) break; continue; }
                if (col < Columns) _characters[row * Columns + col++] = c < 128 ? c : '?';
            }
            longest = Math.Max(longest, col);
            _columns = longest;
            _rows = Math.Min(Rows, row + 1);
            Write(_text, _characters);
            _start = Stopwatch.GetTimestamp();
            _frames = 0;
            _frameMs = 0;
            _lastPresented = presented;
        }
        float white = 0.92f;
        if (hdr.IsHdrEnabled)
        {
            float n = MathF.Pow(Math.Min(hdr.SdrWhiteLevelInNits, hdr.MaxLuminanceInNits) / 10000, 0.1593017578125f);
            white = MathF.Pow((0.8359375f + 18.8515625f * n) / (1 + 18.6875f * n), 78.84375f);
        }
        int scale = texture.Width >= _columns * 12 + 32 ? 2 : 1;
        _effects.DrawHud(texture, _font, _text, white, scale,
            Math.Min(_columns * 6 * scale + 16, Math.Max(1, texture.Width - 16)),
            Math.Min(_rows * 9 * scale + 8, Math.Max(1, texture.Height - 16)));
    }

    private static unsafe void Write(ID3D12Resource buffer, ReadOnlySpan<int> values)
    {
        void* pointer;
        buffer.Map(0, null, &pointer).CheckError();
        MemoryMarshal.AsBytes(values).CopyTo(new Span<byte>(pointer, values.Length * sizeof(int)));
        buffer.Unmap(0);
    }

    public void Dispose()
    {
        _font.Dispose();
        _text.Dispose();
        _effects.Dispose();
    }

    private static int[] CreateFont()
    {
        var result = new int[128 * 7];
        string keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,:/-?+()";
        string[] glyphs =
        [
            "0E11111F111111", "1E11111E11111E", "0F10101010100F", "1E11111111111E", "1F10101E10101F", "1F10101E101010",
            "0F10101711110F", "1111111F111111", "0E04040404040E", "0702020212120C", "11121418141211", "1010101010101F",
            "111B1515111111", "11191513111111", "0E11111111110E", "1E11111E101010", "0E11111115120D", "1E11111E141211",
            "0F10100E01011E", "1F040404040404", "1111111111110E", "11111111110A04", "11111115151B11", "11110A040A1111",
            "11110A04040404", "1F01020408101F", "0E11131519110E", "040C040404040E", "0E11010204081F", "1E01010E01011E",
            "02060A121F0202", "1F10101E01011E", "0E10101E11110E", "1F010204080808", "0E11110E11110E", "0E11110F01010E",
            "00000000000606", "00000000000604", "00060600060600", "01010204081010", "0000001F000000", "0E110102040004",
            "0004041F040400", "02040808080402", "08040202020408"
        ];
        for (int i = 0; i < keys.Length; i++)
            for (int row = 0; row < 7; row++) result[keys[i] * 7 + row] = Convert.ToInt32(glyphs[i].Substring(row * 2, 2), 16);
        return result;
    }
}
