namespace DXRDemo.Shaders;

/// <summary>
/// Capability flags exposed by every <see cref="IShaderPass"/>.
/// The host UI inspects these flags to decide which controls, hints and
/// parameter panels to surface — no per-shader switch logic needed in the UI.
/// </summary>
[Flags]
public enum ShaderCapabilities : uint
{
    None = 0,

    /// <summary>The shader needs iTime and advances automatically.</summary>
    UsesTime = 1 << 0,

    /// <summary>The shader needs iMouse and reacts to pointer movement.</summary>
    UsesMouse = 1 << 1,

    /// <summary>The shader needs iResolution (most do).</summary>
    UsesResolution = 1 << 2,

    /// <summary>The shader can be paused via a UI toggle.</summary>
    SupportsPause = 1 << 3,

    /// <summary>The shader exposes its own adjustable parameters
    /// (see <see cref="IShaderPassWithParameters"/>).</summary>
    SupportsCustomParameters = 1 << 4,
}