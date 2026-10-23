using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Scene;

namespace DXRDemo.DXR;

/// <summary>Builds power-weighted triangle alias/CDF tables and matching hit-light area PDFs.</summary>
internal sealed class SceneLightDistribution
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Emitter { public Vector4 P0, E1, E2, Meta; }
    internal Emitter[] Emitters { get; }
    internal float[] InstanceAreaPdf { get; }
    internal int[] InstanceEmitterStart { get; }
    internal float TotalPower { get; }

    internal SceneLightDistribution(SceneAsset asset, CancellationToken cancellation = default)
    {
        var emitters = new List<Emitter>();
        var cumulative = new List<double>();
        InstanceAreaPdf = new float[asset.Instances.Length];
        InstanceEmitterStart = Enumerable.Repeat(-1, asset.Instances.Length).ToArray();
        var weights = new float[asset.Materials.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            SceneMaterial material = asset.Materials[i];
            Vector3 emission = new(material.Emissive.X, material.Emissive.Y, material.Emissive.Z);
            if (material.EmissiveMap.Info.X >= 0)
            {
                SceneTexture texture = asset.Textures[(int)material.EmissiveMap.Info.X];
                byte[] average = texture.Mips[^1];
                static float Linear(byte b) { float v = b / 255f; return v <= .04045f ? v / 12.92f : MathF.Pow((v + .055f) / 1.055f, 2.4f); }
                // A small floor retains support for tiny bright regions lost in the 8-bit terminal mip.
                emission *= Vector3.Max(new(Linear(average[0]), Linear(average[1]), Linear(average[2])), new(1e-4f));
            }
            float weight = Vector3.Dot(emission, new(.2126f, .7152f, .0722f));
            weights[i] = float.IsFinite(weight) ? MathF.Max(0, weight) * (material.Flags.Z != 0 ? 2 : 1) : 0;
        }
        double total = 0;
        for (int i = 0; i < asset.Instances.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            SceneInstance instance = asset.Instances[i];
            SceneMesh mesh = asset.Meshes[instance.Mesh];
            float weight = weights[mesh.Material];
            if (weight <= 0) continue;
            InstanceAreaPdf[i] = weight;
            InstanceEmitterStart[i] = emitters.Count;
            for (int t = 0; t < mesh.Indices.Length; t += 3)
            {
                if ((t & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                Vector3 Point(uint index) { Vector4 p = mesh.Vertices[index].Position; return Vector3.Transform(new(p.X, p.Y, p.Z), instance.Transform); }
                Vector3 p0 = Point(mesh.Indices[t]), e1 = Point(mesh.Indices[t + 1]) - p0, e2 = Point(mesh.Indices[t + 2]) - p0;
                float area = Vector3.Cross(e1, e2).Length() * .5f;
                // Retain zero-area slots so a hit's primitive index maps directly to its emitter.
                if (!float.IsFinite(area) || area <= 1e-12f) area = 0;
                total += (double)area * weight;
                emitters.Add(new() { P0 = new(p0, 0), E1 = new(e1, weight), E2 = new(e2, 0), Meta = new(area, 0, i, t / 3) });
                cumulative.Add(total);
            }
        }
        if (!double.IsFinite(total) || total > float.MaxValue) throw new InvalidDataException("自发光场景的总功率超出范围。");
        TotalPower = (float)total;
        Emitters = emitters.ToArray();
        for (int i = 0; i < Emitters.Length; i++)
        {
            Emitters[i].Meta.Y = total > 0 ? (float)(cumulative[i] / total) : 0;
            Emitters[i].E1.W = total > 0 && Emitters[i].Meta.X > 0 ? (float)(Emitters[i].E1.W / total) : 0;
        }
        for (int i = 0; i < InstanceAreaPdf.Length; i++)
            InstanceAreaPdf[i] = total > 0 ? (float)(InstanceAreaPdf[i] / total) : 0;
        if (Emitters.Length > 0 && total > 0)
        {
            int count = Emitters.Length;
            var scaled = new double[count];
            var small = new Stack<int>(); var large = new Stack<int>();
            for (int i = 0; i < count; i++)
            {
                double previous = i == 0 ? 0 : cumulative[i - 1];
                scaled[i] = (cumulative[i] - previous) / total * count;
                if (scaled[i] < 1) small.Push(i); else large.Push(i);
            }
            while (small.Count > 0 && large.Count > 0)
            {
                int lo = small.Pop(), hi = large.Pop();
                Emitters[lo].P0.W = (float)scaled[lo]; Emitters[lo].E2.W = hi;
                scaled[hi] += scaled[lo] - 1;
                if (scaled[hi] < 1) small.Push(hi); else large.Push(hi);
            }
            foreach (int i in small) { Emitters[i].P0.W = 1; Emitters[i].E2.W = i; }
            foreach (int i in large) { Emitters[i].P0.W = 1; Emitters[i].E2.W = i; }
        }
    }
}
