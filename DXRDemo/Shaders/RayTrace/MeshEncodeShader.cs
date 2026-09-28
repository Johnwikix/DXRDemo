using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

// Match the original denoiser's normalized encoded-radiance + hit-distance contract.
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MeshEncodeShader(ReadWriteTexture2D<Float4> input, bool hdr, float white, float peak) : IComputeShader<Float4>
{
    public Float4 Execute()
    {
        Float4 value = input[ThreadIds.XY];
        Float3 color = Hlsl.Max(value.XYZ, Float3.Zero);
        if (hdr)
        {
            // Scene lighting is linear Rec.709; HDR10 signals are Rec.2020 / ST.2084.
            color = new Float3(
                Hlsl.Dot(color, new Float3(0.627404f, 0.329283f, 0.043313f)),
                Hlsl.Dot(color, new Float3(0.069097f, 0.919540f, 0.011362f)),
                Hlsl.Dot(color, new Float3(0.016391f, 0.088013f, 0.895595f)));
            Float3 y = Hlsl.Pow(Hlsl.Min(color * white, peak) / 10000, new Float3(0.1593017578125f, 0.1593017578125f, 0.1593017578125f));
            color = Hlsl.Pow((0.8359375f + 18.8515625f * y) / (1 + 18.6875f * y), new Float3(78.84375f, 78.84375f, 78.84375f));
        }
        else color = Hlsl.Pow(color, new Float3(1.0f / 2.2f, 1.0f / 2.2f, 1.0f / 2.2f));
        return new Float4(color, value.W);
    }
}
