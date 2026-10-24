using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

// The legacy compute and hardware mesh tracers use the same normalized disk as SceneTrace.
internal static class SunSampling
{
    public static Float3 Direction(Float3 axis, float coneHeight, float u, float v)
    {
        float h = u * coneHeight;
        float sine = Hlsl.Sqrt(Hlsl.Max(h * (2 - h), 0));
        Float3 tangent = Hlsl.Normalize(Hlsl.Cross(Hlsl.Abs(axis.Y) < .99f ? new Float3(0,1,0) : new Float3(1,0,0), axis));
        float phi = 6.28318530718f * v;
        return axis * (1 - h) + (tangent * Hlsl.Cos(phi) + Hlsl.Cross(axis,tangent) * Hlsl.Sin(phi)) * sine;
    }
}
