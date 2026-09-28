using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

[ThreadGroupSize(8, 8, 1)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MeshPathTraceShader(
    int width, int height, int samples, int maxBounces, int frame, Float2 orbit, float distance,
    ReadOnlyBuffer<Float4> triangles, ReadOnlyBuffer<Float4> nodes,
    ReadWriteTexture2D<Float4> output, ReadWriteTexture2D<Rgba32, Float4> normals) : IComputeShader
{
    private static float Random(ref uint state)
    {
        uint old = state;
        state = old * 747796405u + 2891336453u;
        uint word = ((old >> ((int)(old >> 28) + 4)) ^ old) * 277803737u;
        return ((word >> 22) ^ word) * (1.0f / 4294967296.0f);
    }

    private static bool BoxHit(Float3 ro, Float3 inv, Float3 lo, Float3 hi, float tMax)
    {
        Float3 a = (lo - ro) * inv;
        Float3 b = (hi - ro) * inv;
        Float3 near = Hlsl.Min(a, b), far = Hlsl.Max(a, b);
        float enter = Hlsl.Max(Hlsl.Max(near.X, near.Y), Hlsl.Max(near.Z, 0.0001f));
        float exit = Hlsl.Min(Hlsl.Min(far.X, far.Y), Hlsl.Min(far.Z, tMax));
        return enter <= exit;
    }

    private bool HitMesh(Float3 ro, Float3 rd, float tMax, bool anyHit, out float bestT, out int bestId)
    {
        bestT = tMax;
        bestId = -1;
        Float3 inv = new(
            Hlsl.Abs(rd.X) < 1e-20f ? (rd.X < 0 ? -1e20f : 1e20f) : 1.0f / rd.X,
            Hlsl.Abs(rd.Y) < 1e-20f ? (rd.Y < 0 ? -1e20f : 1e20f) : 1.0f / rd.Y,
            Hlsl.Abs(rd.Z) < 1e-20f ? (rd.Z < 0 ? -1e20f : 1e20f) : 1.0f / rd.Z);
        int node = 0;
        int previous = -1;
        while (node >= 0)
        {
            Float4 lo = nodes[node * 3];
            Float4 hi = nodes[node * 3 + 1];
            Float4 info = nodes[node * 3 + 2];
            int parent = (int)info.Z;
            int count = (int)hi.W;
            int axis = (int)info.W;
            bool reverse = (axis == 0 ? rd.X : (axis == 1 ? rd.Y : rd.Z)) < 0;
            int first = reverse ? (int)info.Y : node + 1;
            int second = reverse ? node + 1 : (int)info.Y;
            if (previous != parent)
            {
                int next = previous == first ? second : parent;
                previous = node; node = next; continue;
            }
            if (!BoxHit(ro, inv, lo.XYZ, hi.XYZ, bestT)) { previous = node; node = parent; continue; }
            if (count == 0) { previous = node; node = first; continue; }
            int start = (int)lo.W;
            for (int i = 0; i < count; i++)
            {
                int id = start + i;
                Float3 v0 = triangles[id * 3].XYZ;
                Float3 e1 = triangles[id * 3 + 1].XYZ;
                Float3 e2 = triangles[id * 3 + 2].XYZ;
                Float3 p = Hlsl.Cross(rd, e2);
                float det = Hlsl.Dot(e1, p);
                if (Hlsl.Abs(det) < 1e-10f) continue;
                float rcp = 1.0f / det;
                Float3 s = ro - v0;
                float u = Hlsl.Dot(s, p) * rcp;
                if (u < 0 || u > 1) continue;
                Float3 q = Hlsl.Cross(s, e1);
                float v = Hlsl.Dot(rd, q) * rcp;
                if (v < 0 || u + v > 1) continue;
                float t = Hlsl.Dot(e2, q) * rcp;
                if (t >= 0.0001f && t < bestT)
                {
                    bestT = t; bestId = id;
                    if (anyHit) return true;
                }
            }
            previous = node; node = parent;
        }
        return bestId >= 0;
    }

    private static Float3 Sky(Float3 rd) => Hlsl.Lerp(new Float3(0.45f,0.55f,0.7f), new Float3(0.1f,0.2f,0.4f), Hlsl.Saturate(rd.Y));

    public void Execute()
    {
        Int2 pixel = ThreadIds.XY;
        if (pixel.X >= width || pixel.Y >= height) return;
        float yaw = orbit.X, pitch = orbit.Y;
        Float3 origin = new Float3(Hlsl.Sin(yaw) * Hlsl.Cos(pitch), Hlsl.Sin(pitch), Hlsl.Cos(yaw) * Hlsl.Cos(pitch)) * distance + new Float3(0, 0.9f, 0);
        Float3 forward = Hlsl.Normalize(new Float3(0,0.9f,0) - origin);
        Float3 right = Hlsl.Normalize(Hlsl.Cross(forward, new Float3(0,1,0)));
        Float3 up = Hlsl.Cross(right, forward);
        Float3 sun = Hlsl.Normalize(new Float3(-1,2,1));
        uint rng = (uint)(pixel.X * 73856093 + pixel.Y * 19349663 + frame * 83492791 + 12345);
        Float3 sum = Float3.Zero;
        float distanceSum = 0;
        Float3 primaryNormal = Float3.Zero;
        float primaryMaterial = 3;
        for (int sample = 0; sample < samples; sample++)
        {
            float sx = ((pixel.X + Random(ref rng)) / width * 2 - 1) * (width / (float)height) * 0.5f;
            float sy = (1 - (pixel.Y + Random(ref rng)) / height * 2) * 0.5f;
            Float3 ro = origin;
            Float3 rd = Hlsl.Normalize(forward + right * sx + up * sy);
            Float3 throughput = new(1,1,1);
            Float3 color = Float3.Zero;
            for (int bounce = 0; bounce < maxBounces; bounce++)
            {
                float meshT; int id;
                bool mesh = HitMesh(ro,rd,1000,false,out meshT,out id);
                float groundT = rd.Y < -1e-8f ? -ro.Y / rd.Y : 1000;
                bool ground = groundT > 0.0001f && groundT < meshT;
                if (!mesh && !ground) { if (bounce == 0) distanceSum += 1; color += throughput * Sky(rd); break; }
                float t = ground ? groundT : meshT;
                Float3 normal = ground ? new Float3(0,1,0) : Hlsl.Normalize(Hlsl.Cross(triangles[id*3+1].XYZ,triangles[id*3+2].XYZ));
                if (Hlsl.Dot(normal,rd) > 0) normal = -normal;
                if (bounce == 0)
                {
                    distanceSum += t / (t + 1);
                    if (sample == 0) { primaryNormal = normal; primaryMaterial = ground ? 1 : 0; }
                }
                Float3 albedo = ground ? new Float3(0.7f,0.7f,0.7f) : new Float3(0.65f,0.3f,0.12f);
                Float3 point = ro + rd * t;
                ro = point + normal * 0.0002f;
                float ndotl = Hlsl.Max(Hlsl.Dot(normal,sun),0);
                float shadowT; int shadowId;
                if (ndotl > 0 && !HitMesh(ro,sun,1000,true,out shadowT,out shadowId))
                    color += throughput * albedo * new Float3(3,2.8f,2.5f) * (ndotl / 3.141592654f);
                // Cosine-weighted diffuse continuation, same PRNG and arithmetic in both backends.
                float r = Hlsl.Sqrt(Random(ref rng));
                float phi = Random(ref rng) * 6.283185307f;
                Float3 tangent = Hlsl.Normalize(Hlsl.Cross(Hlsl.Abs(normal.Y) < 0.99f ? new Float3(0,1,0) : new Float3(1,0,0),normal));
                Float3 bitangent = Hlsl.Cross(normal,tangent);
                rd = Hlsl.Normalize(tangent * (r * Hlsl.Cos(phi)) + bitangent * (r * Hlsl.Sin(phi)) + normal * Hlsl.Sqrt(Hlsl.Max(0,1-r*r)));
                throughput *= albedo;
            }
            sum += color;
        }
        output[pixel] = new Float4(sum / samples, distanceSum / samples);
        normals[pixel] = new Float4(primaryNormal * 0.5f + 0.5f, primaryMaterial / 255.0f);
    }
}
