namespace DXRDemo.SuperResolution;

/// <summary>Identifies the native bridge's super-resolution algorithms.</summary>
public enum ReconstructionMode
{
    /// <summary>Renders at the output resolution without reconstruction.</summary>
    Off = 0,
    /// <summary>Uses Intel XeSS Super Resolution.</summary>
    XeSS = 3,
    /// <summary>Uses AMD FidelityFX Super Resolution 3.1.</summary>
    Fsr = 4,
    /// <summary>Uses NVIDIA DLSS Super Resolution with preset K.</summary>
    Dlss = 5,
    /// <summary>Uses NVIDIA DLSS Ray Reconstruction (DLSSD) on noisy PBR radiance.</summary>
    DlssRayReconstruction = 6
}

/// <summary>Publishes immutable render-thread capability and fallback information to the UI.</summary>
public sealed record ReconstructionStatus(uint Capabilities, ReconstructionMode Requested,
    ReconstructionMode Active, int InputWidth, int InputHeight, int OutputWidth, int OutputHeight, string Message,
    bool NrdAvailable = false, bool NrdActive = false)
{
    /// <summary>Reports whether the device and installed SDK expose an algorithm.</summary>
    public bool Supports(ReconstructionMode mode) => mode == ReconstructionMode.Off || (Capabilities & (1u << (int)mode)) != 0;
}
