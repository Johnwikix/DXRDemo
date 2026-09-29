using System.Numerics;
using System.Runtime.InteropServices;

namespace DXRDemo.SuperResolution;

/// <summary>Declares the full NRD RELAX dispatch adapter and its borrowed-resource frame contract.</summary>
internal static unsafe partial class NativeNrd
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Frame
    {
        internal nint Color, NormalRoughness, ViewZ, Motion, Output;
        // System.Numerics row-vector matrices have the same bytes as NRD column-major column-vector matrices.
        internal Matrix4x4 WorldToView, WorldToViewPrevious, ViewToClip;
        internal Vector2 Jitter, PreviousJitter;
        internal float Milliseconds;
        internal uint FrameIndex, Reset, Padding;
        internal nint Specular, SpecularOutput;
        internal float DenoisingRange;
        internal uint Reserved;
    }

    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdAvailable")]
    internal static partial int Available();
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdDiagnostic")]
    internal static partial nint Diagnostic();
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdCreate")]
    internal static partial int Create(nint device, uint width, uint height, out nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdCreatePbr")]
    internal static partial int CreatePbr(nint device, uint width, uint height, out nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdExecute")]
    internal static partial int Execute(nint context, nint commands, Frame* frame);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdDispatchCount")]
    internal static partial uint DispatchCount(nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "NrdDestroy")]
    internal static partial void Destroy(nint context);
}
