using System.Globalization;
using System.Numerics;
using System.Text;

namespace DXRDemo.Scene;

/// <summary>Linear, latitude-longitude HDR lighting. Rotation is around world Y, in radians.</summary>
public sealed record SceneEnvironment(int Width, int Height, Vector4[] Pixels, float Rotation = 0)
{
    /// <summary>Decodes Radiance RGBE (modern scanline RLE or flat RGBE) without GPU work.</summary>
    public static SceneEnvironment LoadRadiance(string path, int maximumWidth = 1024, float rotation = 0)
    {
        if (maximumWidth is < 8 or > 4096 || !float.IsFinite(rotation)) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        string Line()
        {
            var text = new StringBuilder();
            for (int i = 0; i < 4096; i++)
            {
                byte b = reader.ReadByte();
                if (b == 10) return text.ToString().TrimEnd('\r');
                text.Append((char)b);
            }
            throw new InvalidDataException("HDR header line too long.");
        }
        string signature = Line();
        if (signature is not ("#?RADIANCE" or "#?RGBE")) throw new InvalidDataException("Expected Radiance RGBE HDR.");
        bool format = false;
        for (string line = Line(); line.Length != 0; line = Line())
            if (line == "FORMAT=32-bit_rle_rgbe") format = true;
        if (!format) throw new InvalidDataException("Unsupported HDR pixel format.");
        string[] dimensions = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 4 || dimensions[0] is not ("-Y" or "+Y") || dimensions[2] is not ("+X" or "-X"))
            throw new InvalidDataException("Expected Y/X HDR scanlines.");
        int height = int.Parse(dimensions[1], CultureInfo.InvariantCulture), width = int.Parse(dimensions[3], CultureInfo.InvariantCulture);
        if (width < 1 || height < 1 || (long)width * height > 64 * 1024 * 1024) throw new InvalidDataException("HDR dimensions exceed the decode budget.");
        double resize = Math.Min(1, maximumWidth / (double)Math.Max(width, height));
        int outputWidth = Math.Max(1, (int)Math.Round(width * resize)), outputHeight = Math.Max(1, (int)Math.Round(height * resize));
        var pixels = new Vector4[checked(outputWidth * outputHeight)];
        var counts = new int[pixels.Length];
        var row = new byte[checked(width * 4)];
        for (int y = 0; y < height; y++)
        {
            byte a = reader.ReadByte(), b = reader.ReadByte(), c = reader.ReadByte(), d = reader.ReadByte();
            if (width >= 8 && width <= 32767 && a == 2 && b == 2 && (c & 128) == 0)
            {
                if ((c << 8 | d) != width) throw new InvalidDataException("Invalid HDR scanline width.");
                for (int channel = 0; channel < 4; channel++)
                {
                    int x = 0;
                    while (x < width)
                    {
                        int code = reader.ReadByte(), length = code > 128 ? code - 128 : code;
                        if (length == 0 || x + length > width) throw new InvalidDataException("Invalid HDR run.");
                        if (code > 128)
                        {
                            byte value = reader.ReadByte();
                            for (int j = 0; j < length; j++) row[(x + j) * 4 + channel] = value;
                        }
                        else for (int j = 0; j < length; j++) row[(x + j) * 4 + channel] = reader.ReadByte();
                        x += length;
                    }
                }
            }
            else
            {
                row[0] = a; row[1] = b; row[2] = c; row[3] = d;
                reader.BaseStream.ReadExactly(row.AsSpan(4));
                // Legacy chained runs have a different decoder; reject them rather than misread lighting.
                for (int x = 0; x < width; x++)
                    if (row[x * 4] == 1 && row[x * 4 + 1] == 1 && row[x * 4 + 2] == 1)
                        throw new InvalidDataException("Legacy RGBE run encoding is not supported; resave as modern Radiance HDR.");
            }
            int sy = dimensions[0] == "-Y" ? y : height - y - 1;
            for (int x = 0; x < width; x++)
            {
                int sx = dimensions[2] == "+X" ? x : width - x - 1;
                int index = (int)(sy * (long)outputHeight / height) * outputWidth + (int)(sx * (long)outputWidth / width);
                int offset = x * 4, exponent = row[offset + 3];
                float scale = exponent == 0 ? 0 : MathF.ScaleB(1, exponent - 136);
                Vector4 value = new(row[offset] * scale, row[offset + 1] * scale, row[offset + 2] * scale, 0);
                if (!float.IsFinite(value.X + value.Y + value.Z)) throw new InvalidDataException("Non-finite HDR radiance.");
                pixels[index] += (value - pixels[index]) / ++counts[index];
            }
        }
        return new(outputWidth, outputHeight, pixels, rotation);
    }
}
