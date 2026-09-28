using ComputeSharp;

namespace DXRDemo.Shaders.RayTrace;

/// <summary>
/// SVGF-style spatial filtering pass: one level of an A-trous wavelet filter with a 3x3 box
/// kernel (as used in the GDC 2019 "Real-Time Path Tracing and Denoising in Quake II" talk).
/// The pass is dispatched five times per frame with an increasing <c>level</c> (0..4, step
/// 1, 2, 4, 8, 16), ping-ponging between two buffers; the combined support reaches 33x33
/// pixels. The last level filters straight into the display texture.
///
/// <paramref name="signalIn"/> carries the color in RGB and the per-pixel variance estimate
/// in W (written by <see cref="TemporalAccumulationShader"/>); the variance is filtered with
/// the same weights, so converged (low-variance) pixels end up almost unblurred while noisy
/// ones get the full kernel — SVGF's "temporally stable -&gt; blur less" behavior.
///
/// Edge stopping combines the NRD guide signals:
///   - world-space normals (stored in <paramref name="normalTexture"/>.RGB)
///   - hit distance (read from the current frame's <paramref name="signalDistance"/>.W,
///     decoded back to meters here), whose tolerance grows with the level
///   - material id (stored in <paramref name="normalTexture"/>.W) as a hard edge stop
///   - a variance-guided luminance weight: <c>exp(-|l0 - l1| / (sigma * sqrt(var)))</c>.
///
/// Sky pixels carry a zero normal + material id 3 and filter among themselves using the
/// luminance weight only (the sun disk stays protected, depth is infinite for sky).
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SpatialFilterShader(
    int level,
    Float2 iResolution,
    IReadWriteNormalizedTexture2D<Float4> signalIn,
    IReadWriteNormalizedTexture2D<Float4> signalDistance,
    IReadWriteNormalizedTexture2D<Float4> normalTexture) : IComputeShader<Float4>
{
    /// <summary>Standard deviation of the normal-difference Gaussian (1 - |dot(n0, n1)|).</summary>
    private const float NormalSigma = 0.15f;

    /// <summary>Base standard deviation of the hit-distance-difference weight.</summary>
    private const float DepthSigma = 0.7f;

    /// <summary>Scale of the variance-normalized luminance edge-stopping weight (SVGF sigma_l).</summary>
    private const float LuminanceSigma = 1.0f;

    private static float Luminance(Float3 c)
    {
        return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }

    private static Float3 DecodeNormal(Float4 n)
    {
        return n.RGB * 2.0f - 1.0f;
    }

    private static bool IsSky(Float3 n)
    {
        return Hlsl.Length(n) < 0.5f;
    }

    private static float ToMeters(float enc)
    {
        return enc / (1.0f - enc);
    }

    private static float ComputeWeight(
        Float3 n0, float t0, int m0, float l0, float var0,
        Float3 n1, float t1, int m1, float l1, float depthSigma)
    {
        bool sky0 = IsSky(n0);
        bool sky1 = IsSky(n1);

        if (sky0 != sky1 || m0 != m1)
        {
            return 0.0f;
        }

        float wL = Hlsl.Exp(-Hlsl.Abs(l0 - l1) / (LuminanceSigma * Hlsl.Sqrt(var0) + 1e-4f));

        if (sky0 && sky1)
        {
            return wL;
        }

        float normalDiff = 1.0f - Hlsl.Dot(n0, n1);
        float depthDiff = Hlsl.Abs(t1 - t0) / Hlsl.Max(t0, 0.5f);

        float wN = Hlsl.Exp(-(normalDiff * normalDiff) / (2.0f * NormalSigma * NormalSigma));
        float wD = Hlsl.Exp(-depthDiff / depthSigma);

        return wN * wD * wL;
    }

    public Float4 Execute()
    {
        int step = (int)(1U << level);
        float depthSigma = DepthSigma * Hlsl.Pow(1.5f, level);

        Int2 xy = ThreadIds.XY;

        Float4 c = signalIn[xy];
        float t0 = ToMeters(signalDistance[xy].W);
        Float4 n4 = normalTexture[xy];

        Float3 n0 = DecodeNormal(n4);
        int m0 = (int)Hlsl.Round(n4.W * 255.0f);
        float l0 = Luminance(c.RGB);
        float var0 = Hlsl.Max(c.W, 1e-4f);

        // 3x3 box kernel (A-trous with the given step). The center tap always carries
        // weight 1, so the result falls back to the unfiltered pixel when every
        // neighborhood tap is rejected.
        Float3 acc = c.RGB;
        float vAcc = var0;
        float wSum = 1.0f;

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0)
                {
                    continue;
                }

                Int2 p = new(xy.X + ox * step, xy.Y + oy * step);

                if (p.X < 0 || p.X >= iResolution.X || p.Y < 0 || p.Y >= iResolution.Y)
                {
                    continue;
                }

                Float4 tap = signalIn[p];
                Float4 tn = normalTexture[p];

                float w = ComputeWeight(
                    n0, t0, m0, l0, var0,
                    DecodeNormal(tn), ToMeters(signalDistance[p].W), (int)Hlsl.Round(tn.W * 255.0f), Luminance(tap.RGB), depthSigma);

                acc += tap.RGB * w;
                vAcc += tap.W * w;
                wSum += w;
            }
        }

        Float3 mean = acc / wSum;

        // Filter the variance with the same kernel so higher levels see a cleaner estimate
        // (SVGF), narrowing the blur as the image converges.
        float meanVar = vAcc / wSum;

        return new Float4(mean.X, mean.Y, mean.Z, meanVar);
    }
}
