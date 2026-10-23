using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Scene;

namespace DXRDemo.DXR;

/// <summary>Solid-angle weighted alias table; sampling and miss MIS share the same density.</summary>
internal sealed class EnvironmentDistribution
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Texel { public Vector4 Radiance, Sampling; }
    internal Texel[] Texels { get; }
    internal EnvironmentDistribution(SceneEnvironment environment)
    {
        int width = environment.Width, height = environment.Height;
        if (width < 1 || height < 1 || (long)width * height > 8 * 1024 * 1024 ||
            environment.Pixels.Length != width * height || !float.IsFinite(environment.Rotation))
            throw new ArgumentException("Invalid environment map.");
        int count = environment.Pixels.Length;
        Texels = new Texel[count];
        double[] power = new double[count], solidAngles = new double[count];
        double total = 0;
        for (int y = 0; y < height; y++)
        {
            double omega = 2 * Math.PI / width * (Math.Cos(Math.PI * y / height) - Math.Cos(Math.PI * (y + 1) / height));
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x; Vector4 p = environment.Pixels[i];
                if (!float.IsFinite(p.X + p.Y + p.Z) || p.X < 0 || p.Y < 0 || p.Z < 0) throw new ArgumentException("Invalid HDR radiance.");
                solidAngles[i] = omega;
                total += power[i] = (.2126 * p.X + .7152 * p.Y + .0722 * p.Z) * omega;
            }
        }
        var small = new Stack<int>(); var large = new Stack<int>();
        var scaled = new double[count];
        for (int i = 0; i < count; i++)
        {
            // Uniform support is required for bilinear filtering across a black/bright texel edge.
            double pmf = total > 0 ? .95 * power[i] / total + .05 * solidAngles[i] / (4 * Math.PI) : solidAngles[i] / (4 * Math.PI);
            Texels[i].Radiance = new(environment.Pixels[i].X, environment.Pixels[i].Y, environment.Pixels[i].Z, (float)(pmf / solidAngles[i]));
            Texels[i].Sampling = new(1, i, 0, 0);
            scaled[i] = pmf * count;
            (scaled[i] < 1 ? small : large).Push(i);
        }
        while (small.Count > 0 && large.Count > 0)
        {
            int s = small.Pop(), l = large.Pop();
            Texels[s].Sampling = new((float)scaled[s], l, 0, 0);
            scaled[l] += scaled[s] - 1;
            (scaled[l] < 1 ? small : large).Push(l);
        }
    }
}
