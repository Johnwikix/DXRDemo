using System.Runtime.InteropServices;
using System.Numerics;

namespace DXRDemo.SuperResolution;

/// <summary>Declares the borrowed-resource C ABI of the Spectrum-derived vendor bridge.</summary>
internal static unsafe partial class NativeReconstruction
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        internal nint Color, Depth, Motion, Reactive, Output;
        internal uint Width, Height;
        internal float JitterX, JitterY, Milliseconds;
        internal uint Reset;
        internal float NearPlane, FarPlane, VerticalFieldOfView, Padding;
    }

    /// <summary>Supplies noisy radiance, material guides and camera transforms to DLSSD (240 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RayReconstructionFrame
    {
        internal Frame Common;
        internal nint DiffuseAlbedo, SpecularAlbedo, NormalRoughness, SpecularHitDistance;
        internal Matrix4x4 WorldToView, ViewToClip;
    }

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionExecuteDlssd")]
    internal static partial int ExecuteDlssd(nint context, nint commands, RayReconstructionFrame* frame);

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionNgxDiagnostic")]
    internal static partial nint NgxDiagnostic();

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionCapabilities")]
    internal static partial uint Capabilities(nint device);

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionFsrDiagnostic")]
    internal static partial nint FsrDiagnostic();

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionCreateV2")]
    internal static partial int Create(nint device, nint commands, int mode, int preset,
        uint width, uint height, uint inputWidth, uint inputHeight, out nint context);

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionExecute")]
    internal static partial int Execute(nint context, nint commands, Frame* frame);

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "ReconstructionDestroy")]
    internal static partial void Destroy(nint context);
}
