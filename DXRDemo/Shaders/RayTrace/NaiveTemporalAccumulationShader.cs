using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

/// <summary>
/// Naive temporal accumulation pass: running mean of every frame since the last reset,
/// <c>acc = hist + (cur - hist) / n</c> with <c>n = frame + 1</c> and no fixed history cap.
/// No confidence test, no history clamping, no variance estimation — the simplest possible
/// temporal mean, kept as a reference mode to compare against NVIDIA NRD RELAX.
///
/// The history texture is updated in place (each thread reads and writes its own pixel only,
/// so there is no cross-thread hazard) and the accumulated result is returned as the target
/// value, i.e. the display texture, in the same dispatch — no ping-pong needed.
///
/// The signal is stored in display-encoded space (PQ or sRGB, see <see cref="RayTraceShader"/>);
/// W carries the normalized hit distance and is averaged along for consistency.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct NaiveTemporalAccumulationShader(
    int frame,
    IReadWriteNormalizedTexture2D<Float4> signal,
    IReadWriteNormalizedTexture2D<Float4> history) : IComputeShader<Float4>
{
    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Float4 cur = signal[xy];
        Float4 hist = history[xy];

        float n = frame + 1.0f;

        Float4 acc = hist + (cur - hist) / n;

        history[xy] = acc;

        return acc;
    }
}
