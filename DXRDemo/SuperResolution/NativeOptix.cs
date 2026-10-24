using System.Runtime.InteropServices;

namespace DXRDemo.SuperResolution;

/// <summary>Optional CUDA-driver / D3D12 external-memory OptiX AOV adapter.</summary>
internal static unsafe partial class NativeOptix
{
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixAvailable")]
    internal static partial int Available(nint device);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixDiagnostic")]
    internal static partial nint Diagnostic();
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixCreate")]
    internal static partial int Create(nint device, uint width, uint height, out nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixExecute")]
    internal static partial int Execute(nint context, nint color, nint normal, nint albedo, nint specular,
        nint output, float* camera, uint pbr, uint reset);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixDestroy")]
    internal static partial void Destroy(nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixDispatchCount")]
    internal static partial uint DispatchCount(nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixGpuMilliseconds")]
    internal static partial float GpuMilliseconds(nint context);
    [LibraryImport("DXRDemo.Reconstruction", EntryPoint = "OptixTotalGpuMilliseconds")]
    internal static partial float TotalGpuMilliseconds(nint context);
}
