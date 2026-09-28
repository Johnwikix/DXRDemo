using ComputeSharp;

namespace DXRDemo.Hdr;

/// <summary>
/// Per-frame parameters that control how shader output is encoded for the current display.
/// </summary>
/// <param name="IsHdrEnabled">Whether the display is in HDR10 mode (PQ encoding) or SDR mode (sRGB encoding).</param>
/// <param name="SdrWhiteLevelInNits">Luminance (nits) that SDR white (1.0) maps to in HDR mode.</param>
/// <param name="MaxLuminanceInNits">Peak luminance (nits) of the display, used to clamp the HDR signal.</param>
public readonly record struct HdrRenderParameters(
    bool IsHdrEnabled,
    float SdrWhiteLevelInNits,
    float MaxLuminanceInNits)
{
    /// <summary>
    /// Gets the default parameters (SDR mode).
    /// </summary>
    public static HdrRenderParameters Default { get; } = new(
        IsHdrEnabled: false,
        SdrWhiteLevelInNits: 200,
        MaxLuminanceInNits: 1000);
}

/// <summary>
/// Contract for components that produce frames for <see cref="HdrShaderPanel"/>.
/// Shaders render into a 16-bit <c>R16G16B16A16_UNORM</c> frame texture, encoding their linear
/// output with the PQ curve (HDR10) or an sRGB gamma (SDR) before writing it — the host panel
/// then converts the frame into the swap chain back buffer.
/// </summary>
public interface IHdrShaderRunner
{
    /// <summary>
    /// Renders one frame into <paramref name="texture"/>.
    /// </summary>
    /// <param name="texture">The frame texture (R16G16B16A16_UNORM, width * height pixels).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="timespan">Elapsed time since rendering started.</param>
    /// <param name="parameter">A <see cref="HdrRenderParameters"/> value controlling the output encoding.</param>
    /// <returns>Whether a frame was produced and should be presented.</returns>
    bool TryExecute(ReadWriteTexture2D<Rgba64, Float4> texture, int width, int height, TimeSpan timespan, object? parameter);
}
