using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

/// <summary>
/// SVGF/RELAX-style temporal accumulation pass. Combines the current (noisy, 2 SPP) frame with
/// the accumulated history using an exponential moving average whose history length grows frame
/// by frame up to a cap, so static scenes converge toward a clean image while ghosting stays
/// bounded when the scene changes without a host-side <c>frame</c> reset.
///
/// The signal is stored in display-encoded space (PQ or sRGB, see <see cref="RayTraceShader"/>);
/// RGB holds the encoded radiance, W holds the normalized hit distance <c>t / (t + 1)</c> which
/// is used both for the confidence test and as the spatial filter's edge signal.
///
/// Per pixel the pass performs, in a single 3x3 neighborhood scan:
///   - firefly filtering (clamp the center luminance to the neighborhood min/max, NRD-style),
///   - a per-channel AABB of the current frame used to clamp the history before blending
///     (RELAX-style history clamping), which lets the history accumulate for a long time
///     without outliers or stale surfaces polluting the result,
///   - a spatial variance estimate (SVGF fallback when no usable history exists).
///
/// The variance estimate is written to the output W channel and drives the spatial filter
/// strength: converged (low-variance) pixels get filtered less, noisy ones more — the SVGF
/// "temporally stable -&gt; blur less" principle that keeps static frames sharp.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct TemporalAccumulationShader(
    int frame,
    Float2 iResolution,
    IReadWriteNormalizedTexture2D<Float4> signal,
    IReadWriteNormalizedTexture2D<Float4> historyIn,
    IReadWriteNormalizedTexture2D<float> momentIn,
    IReadWriteNormalizedTexture2D<float> momentOut) : IComputeShader<Float4>
{
    /// <summary>
    /// Maximum number of frames the history is allowed to accumulate. The EMA alpha is
    /// <c>1 / min(frame, MaxHistoryFrames)</c>: long enough for static frames to converge
    /// far below the noise floor, short enough that a missed reset cannot leave a visible
    /// ghost behind.
    /// </summary>
    private const int MaxHistoryFrames = 64;

    /// <summary>Hit distance ratio tolerance before confidence collapses (NRD-style log2 distance).</summary>
    private const float ConfidenceSigma = 5.0f;

    private static float Luminance(Float3 c)
    {
        return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;

        Float4 cur = signal[xy];

        // Single 3x3 scan over the current frame gathering everything the pass needs:
        // the firefly clamp range, the per-channel AABB for the history clamp, and the
        // spatial luminance variance used as fallback / floor for the variance estimate.
        Float3 minC = cur.RGB;
        Float3 maxC = cur.RGB;
        float minL = Luminance(cur.RGB);
        float maxL = minL;
        float lumMean = 0.0f;
        float lumMeanSq = 0.0f;

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                Int2 p = new(xy.X + ox, xy.Y + oy);

                if (p.X < 0 || p.X >= iResolution.X || p.Y < 0 || p.Y >= iResolution.Y)
                {
                    continue;
                }

                Float3 tap = signal[p].RGB;
                float tapL = Luminance(tap);

                minC = Hlsl.Min(minC, tap);
                maxC = Hlsl.Max(maxC, tap);
                minL = Hlsl.Min(minL, tapL);
                maxL = Hlsl.Max(maxL, tapL);
                lumMean += tapL;
                lumMeanSq += tapL * tapL;
            }
        }

        float spatialVar = Hlsl.Max(lumMeanSq / 9.0f - (lumMean / 9.0f) * (lumMean / 9.0f), 0.0f);

        // Anti-firefly: clamp the pixel luminance to the neighborhood min/max so isolated
        // high-energy outliers cannot accumulate into the history.
        float lum = Luminance(cur.RGB);
        cur.RGB *= Hlsl.Clamp(lum, minL, maxL) / (lum + 1e-4f);

        Float4 hist = historyIn[xy];
        float distHist = momentIn[xy];

        // Exponential moving average whose history length grows frame by frame up to a cap:
        // static scenes converge (noise floor drops with the square root of the length),
        // while the cap plus the confidence test below keep ghosting bounded.
        float alpha = 1.0f / Hlsl.Min(frame + 1, MaxHistoryFrames);

        // History confidence from the accumulated hit distance. The confidence is 1 while the
        // current hit distance ratio is within a small band around 1 and collapses on
        // disocclusion / surface change, discarding the stale history for that pixel.
        float ratio = cur.W / (distHist + 1e-5f);
        float confidence = Hlsl.Saturate(1.0f - Hlsl.Abs(Hlsl.Log2(ratio)) * ConfidenceSigma);

        Float3 newRgb;
        float newDist;
        float newVar;

        if (frame == 0 || confidence < 0.5f)
        {
            // No usable history: adopt the current frame and seed the variance estimate
            // with the neighborhood variance (SVGF spatial fallback).
            newRgb = cur.RGB;
            newDist = cur.W;
            newVar = spatialVar;
        }
        else
        {
            // Clamp the history to the current frame's per-channel AABB (RELAX-style) so a
            // stale history cannot pull the result toward an outdated color, then blend with
            // the EMA; the confidence scales the blend toward the current frame as well.
            Float3 clamped = Hlsl.Clamp(hist.RGB, minC, maxC);
            float a = alpha * confidence;

            newRgb = Hlsl.Lerp(clamped, cur.RGB, a);
            newDist = Hlsl.Lerp(distHist, cur.W, a);

            // Variance estimate: EMA of the squared luminance deviation, floored by the
            // spatial variance. This drives the spatial filter strength (SVGF).
            float dev = lum - Luminance(hist.RGB);
            newVar = Hlsl.Max(Hlsl.Lerp(hist.W, dev * dev, a), spatialVar);
        }

        momentOut[xy] = newDist;

        return new Float4(newRgb, newVar);
    }
}