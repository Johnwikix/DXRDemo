using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

internal static class DxrResources
{
    public static ID3D12Resource CreateUploadBuffer(ID3D12Device device, ulong sizeBytes, string name = "Buffer")
    {
        var heapProps = new HeapProperties(
            HeapType.Upload,
            CpuPageProperty.Unknown,
            MemoryPool.Unknown, 0, 0);
        var desc = new ResourceDescription
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = sizeBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.RowMajor,
            Flags = ResourceFlags.None,
        };
        return device.CreateCommittedResource(heapProps, HeapFlags.None, desc, ResourceStates.GenericRead);
    }

    public static ID3D12Resource CreateDefaultBuffer(
        ID3D12Device device,
        ulong sizeBytes,
        ResourceFlags flags,
        ResourceStates initialState,
        string name = "Buffer")
    {
        var heapProps = new HeapProperties(
            HeapType.Default,
            CpuPageProperty.Unknown,
            MemoryPool.Unknown, 0, 0);
        var desc = new ResourceDescription
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = sizeBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.RowMajor,
            Flags = flags,
        };
        return device.CreateCommittedResource(heapProps, HeapFlags.None, desc, initialState, null);
    }

    public static ID3D12Resource CreateUnorderedAccessTexture2D(
        ID3D12Device device,
        uint width,
        uint height,
        Format format,
        ResourceStates initialState,
        string name = "Texture")
    {
        var heapProps = new HeapProperties(
            HeapType.Default,
            CpuPageProperty.Unknown,
            MemoryPool.Unknown, 0, 0);
        var desc = new ResourceDescription
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Layout = TextureLayout.Unknown,
            Flags = ResourceFlags.AllowUnorderedAccess,
        };
        return device.CreateCommittedResource(heapProps, HeapFlags.None, desc, initialState);
    }
}