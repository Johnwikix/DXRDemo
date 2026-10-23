using System.Buffers.Binary;
using System.IO;

namespace DXRDemo.Assets.Importers;

/// <summary>Strict reader for optional, single-layer 2D DX10 BC7 sidecars.</summary>
internal static class Bc7Texture
{
    internal static byte[][] Read(string path, int width, int height, int mipCount, bool srgb)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[148];
        file.ReadExactly(header);
        if (U32(header, 0) != 0x20534444 || U32(header, 4) != 124 || U32(header, 76) != 32 ||
            U32(header, 84) != 0x30315844 || U32(header, 128) != (srgb ? 99u : 98u) ||
            U32(header, 132) != 3 || U32(header, 136) != 0 || U32(header, 140) != 1 ||
            width < 4 || height < 4 || width > 16384 || height > 16384 || (width & 3) != 0 || (height & 3) != 0 ||
            U32(header, 16) != width || U32(header, 12) != height || U32(header, 28) != mipCount ||
            mipCount != 1 + (int)MathF.Floor(MathF.Log2(Math.Max(width, height))))
            throw new InvalidDataException("BC7 sidecar does not match the decoded texture.");
        long expected = 148;
        int w = width, h = height;
        for (int level = 0; level < mipCount; level++)
        {
            expected += checked(((w + 3) / 4) * ((h + 3) / 4) * 16);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        if (file.Length != expected) throw new InvalidDataException("BC7 sidecar has an invalid payload length.");
        var mips = new byte[mipCount][];
        w = width; h = height;
        for (int level = 0; level < mipCount; level++)
        {
            mips[level] = new byte[checked(((w + 3) / 4) * ((h + 3) / 4) * 16)];
            file.ReadExactly(mips[level]);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        return mips;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
