using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace DXRDemo.DXR;

public sealed unsafe class DxrShaderTable : IDisposable
{
    public const int RecordSize = 64;
    public const int RayGenOffset         = 0 * RecordSize;
    public const int MissOffset           = 1 * RecordSize;
    public const int HitGroupOffset       = 2 * RecordSize;
    public const int TotalSize            = 3 * RecordSize;

    public ID3D12Resource Buffer { get; private set; } = null!;

    public void Build(
        ID3D12Device device,
        ID3D12StateObjectProperties properties)
    {
        Buffer = DxrResources.CreateUploadBuffer(device, TotalSize, "ShaderTable");

        unsafe
        {
            void* mapped;
            Buffer.Map(0, null, &mapped).CheckError();
            int si = (int)D3D12.ShaderIdentifierSizeInBytes;

            WriteRecord(mapped, RayGenOffset,   properties.GetShaderIdentifier("RayGen"), si);
            WriteRecord(mapped, MissOffset,     properties.GetShaderIdentifier("Miss"), si);
            WriteRecord(mapped, HitGroupOffset, properties.GetShaderIdentifier("HitGroup_Sphere"), si);
        }
        Buffer.Unmap(0);
    }

    private static void WriteRecord(void* basePtr, int offset, IntPtr shaderId, int size)
    {
        byte* dest = (byte*)basePtr + offset;
        byte* src = (byte*)shaderId.ToPointer();
        for (int i = 0; i < size; i++)
            dest[i] = src[i];
    }

    public void Dispose()
    {
        Buffer?.Dispose();
    }
}
