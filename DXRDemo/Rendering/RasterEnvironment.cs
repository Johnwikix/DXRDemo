using System.Numerics;
using DXRDemo.Scene;

namespace DXRDemo.Rendering;

/// <summary>在场景准备阶段确定性预积分光栅环境；漫反射保存 E/π，镜面保存 GGX 卷积。</summary>
internal static class RasterEnvironment
{
    internal const int SpecularTexels = 10923, TexelCount = SpecularTexels + 16 * 8;
    private static readonly Lazy<Vector4[]> Default = new(() => Build(null));

    internal static Vector4[] Prepare(SceneEnvironment? map, CancellationToken cancellation)
        => map == null ? Default.Value : Build(map, cancellation);

    private static Vector4[] Build(SceneEnvironment? map, CancellationToken cancellation = default)
    {
        var result = new Vector4[TexelCount];
        int offset = 0;
        for (int level = 0; level < 9; level++)
        {
            bool diffuse = level == 8;
            int width = diffuse ? 16 : 128 >> level, height = diffuse ? 8 : Math.Max(1, 64 >> level);
            float roughness = level / 7f, a = roughness * roughness;
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    float phi = (x + .5f) / width * MathF.Tau, theta = (y + .5f) / height * MathF.PI;
                    Vector3 n = new(MathF.Cos(phi) * MathF.Sin(theta), MathF.Cos(theta), MathF.Sin(phi) * MathF.Sin(theta));
                    Vector3 t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < .99f ? Vector3.UnitY : Vector3.UnitX, n));
                    Vector3 b = Vector3.Cross(n, t), sum = default;
                    float weight = 0;
                    int samples = level == 0 ? 1 : 128;
                    for (int i = 0; i < samples; i++)
                    {
                        uint bits = (uint)i;
                        bits = (bits << 16) | (bits >> 16);
                        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
                        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
                        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
                        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
                        float u = (i + .5f) / samples, v = bits * 2.3283064365386963e-10f;
                        float cos = diffuse ? MathF.Sqrt(1 - u) : MathF.Sqrt((1 - u) / (1 + (a * a - 1) * u));
                        float sin = MathF.Sqrt(MathF.Max(0, 1 - cos * cos)), angle = MathF.Tau * v;
                        Vector3 h = t * (sin * MathF.Cos(angle)) + b * (sin * MathF.Sin(angle)) + n * cos;
                        Vector3 l = diffuse ? h : level == 0 ? n : 2 * Vector3.Dot(n, h) * h - n;
                        float w = diffuse || level == 0 ? 1 : MathF.Max(0, Vector3.Dot(n, l));
                        sum += Sample(map, l) * w;
                        weight += w;
                    }
                    result[offset + y * width + x] = new(sum / MathF.Max(weight, 1e-8f), 0);
                }
            }
            offset += width * height;
        }
        return result;
    }

    private static Vector3 Sample(SceneEnvironment? map, Vector3 direction)
    {
        if (map == null)
            return Vector3.Lerp(new(.45f, .55f, .7f), new(.1f, .2f, .4f), Math.Clamp(direction.Y, 0, 1));
        float u = (MathF.Atan2(direction.Z, direction.X) - map.Rotation) / MathF.Tau;
        float px = (u - MathF.Floor(u)) * map.Width - .5f;
        float py = MathF.Acos(Math.Clamp(direction.Y, -1, 1)) / MathF.PI * map.Height - .5f;
        int x = (int)MathF.Floor(px), y = (int)MathF.Floor(py);
        Vector3 Tap(int dx, int dy)
        {
            var p = map.Pixels[Math.Clamp(y + dy, 0, map.Height - 1) * map.Width + ((x + dx) % map.Width + map.Width) % map.Width];
            return new(p.X, p.Y, p.Z);
        }
        return Vector3.Lerp(Vector3.Lerp(Tap(0, 0), Tap(1, 0), px - x), Vector3.Lerp(Tap(0, 1), Tap(1, 1), px - x), py - y);
    }
}
