using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace DXRDemo.DXR;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SphereGpuData
{
    public Vector3 Center;
    public float   Radius;
    public Vector3 Albedo;
    public int     MaterialType;
    public float   MaterialParam;
    public Vector3 _pad;
}

public sealed unsafe class DxrAccelerationStructure : IDisposable
{
    public ID3D12Resource Blas { get; private set; } = null!;
    public ID3D12Resource Tlas { get; private set; } = null!;
    public ID3D12Resource SphereBuffer { get; private set; } = null!;
    public ID3D12Resource ScratchBlas { get; private set; } = null!;
    public ID3D12Resource ScratchTlas { get; private set; } = null!;
    public ID3D12Resource InstanceBuffer { get; private set; } = null!;
    private ID3D12Resource AabbBuffer = null!;

    public static readonly SphereGpuData[] Spheres = new[]
    {
        new SphereGpuData { Center = new(0, 1, 0),    Radius = 1.0f,    Albedo = new(0.00f, 0.90f, 0.05f), MaterialType = 0, MaterialParam = 0.00f },
        new SphereGpuData { Center = new(0, 1, 2.5f),  Radius = 1.0f,    Albedo = new(0.90f, 0.90f, 0.90f), MaterialType = 1, MaterialParam = 0.01f },
        new SphereGpuData { Center = new(0, 1, -2.5f), Radius = 1.0f,    Albedo = new(0.00f, 0.00f, 0.00f), MaterialType = 2, MaterialParam = 1.50f },
        new SphereGpuData { Center = new(0, 1, -2.5f), Radius = -0.92f, Albedo = new(0.90f, 0.90f, 0.90f), MaterialType = 2, MaterialParam = 1.50f },
        new SphereGpuData { Center = new(0, -1000, 0), Radius = 1000.0f, Albedo = new(0.90f, 0.90f, 0.90f), MaterialType = 0, MaterialParam = 0.00f },
    };

    /// <summary>
    /// Call each step through this so the callee can inject diagnostics.
    /// </summary>
    public void Build(
        ID3D12Device5 device,
        ID3D12GraphicsCommandList4 commandList,
        Action<string>? step = null)
    {
        step?.Invoke("SphereBuffer");    CreateSphereBuffer(device);
        step?.Invoke("AabbBuffer");      AabbBuffer = CreateAabbBuffer(device);
        step?.Invoke("BlasPrebuildInfo"); var blasInfo = GetBlasPrebuildInfo(device, AabbBuffer);
        step?.Invoke("BlasAlloc");       AllocBlas(device, blasInfo);
        step?.Invoke("BlasScratchAlloc"); AllocBlasScratch(device, blasInfo);
        step?.Invoke("BlasBuild");       BuildBlas(commandList, AabbBuffer);
        step?.Invoke("BlasBarrier");     commandList.ResourceBarrierUnorderedAccessView(Blas);

        step?.Invoke("InstanceBuffer");  CreateInstanceBuffer(device);
        step?.Invoke("TlasPrebuildInfo"); var tlasInfo = GetTlasPrebuildInfo(device);
        step?.Invoke("TlasAlloc");       AllocTlas(device, tlasInfo);
        step?.Invoke("TlasScratchAlloc"); AllocTlasScratch(device, tlasInfo);
        step?.Invoke("TlasBuild");       BuildTlas(commandList);
        step?.Invoke("TlasBarrier");     commandList.ResourceBarrierUnorderedAccessView(Tlas);
    }

    // ── Individual steps ────────────────────────────────

    private void CreateSphereBuffer(ID3D12Device5 device)
    {
        int size = sizeof(SphereGpuData) * Spheres.Length;
        SphereBuffer = DxrResources.CreateUploadBuffer(device, (ulong)size, "SphereBuffer");
        void* ptr;
        SphereBuffer.Map(0, null, &ptr).CheckError();
        for (int i = 0; i < Spheres.Length; i++)
            Marshal.StructureToPtr(Spheres[i], (IntPtr)ptr + i * sizeof(SphereGpuData), false);
        SphereBuffer.Unmap(0);
    }

    private ID3D12Resource CreateAabbBuffer(ID3D12Device5 device)
    {
        ulong aabbTotal = 24ul * (ulong)Spheres.Length;
        var buf = DxrResources.CreateUploadBuffer(device, aabbTotal, "AabbBuffer");
        void* ptr;
        buf.Map(0, null, &ptr).CheckError();
        float* f = (float*)ptr;
        for (int i = 0; i < Spheres.Length; i++)
        {
            float r = Math.Abs(Spheres[i].Radius);
            f[i * 6 + 0] = Spheres[i].Center.X - r;
            f[i * 6 + 1] = Spheres[i].Center.Y - r;
            f[i * 6 + 2] = Spheres[i].Center.Z - r;
            f[i * 6 + 3] = Spheres[i].Center.X + r;
            f[i * 6 + 4] = Spheres[i].Center.Y + r;
            f[i * 6 + 5] = Spheres[i].Center.Z + r;
        }
        buf.Unmap(0);
        return buf;
    }

    private RaytracingAccelerationStructurePrebuildInfo GetBlasPrebuildInfo(
        ID3D12Device5 device, ID3D12Resource aabbBuffer)
    {
        int n = Spheres.Length;
        var geoms = new RaytracingGeometryDescription[n];
        for (int i = 0; i < n; i++)
        {
            geoms[i] = new RaytracingGeometryDescription(
                new RaytracingGeometryAabbsDescription(1,
                    new GpuVirtualAddressAndStride(aabbBuffer.GPUVirtualAddress + (ulong)(i * 24), 24)),
                RaytracingGeometryFlags.Opaque);
        }
        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.None,
            DescriptorsCount = (uint)n,
            Layout = ElementsLayout.Array,
            GeometryDescriptions = geoms,
        };
        return device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
    }

    private void AllocBlas(ID3D12Device5 device, RaytracingAccelerationStructurePrebuildInfo info)
    {
        Blas = DxrResources.CreateDefaultBuffer(device, info.ResultDataMaxSizeInBytes,
            ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure, "BLAS");
    }

    private void AllocBlasScratch(ID3D12Device5 device, RaytracingAccelerationStructurePrebuildInfo info)
    {
        ScratchBlas = DxrResources.CreateDefaultBuffer(device, info.ScratchDataSizeInBytes,
            ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess, "BLAS_Scratch");
    }

    private void BuildBlas(ID3D12GraphicsCommandList4 cmd, ID3D12Resource aabbBuffer)
    {
        int n = Spheres.Length;
        var geoms = new RaytracingGeometryDescription[n];
        for (int i = 0; i < n; i++)
        {
            geoms[i] = new RaytracingGeometryDescription(
                new RaytracingGeometryAabbsDescription(1,
                    new GpuVirtualAddressAndStride(aabbBuffer.GPUVirtualAddress + (ulong)(i * 24), 24)),
                RaytracingGeometryFlags.Opaque);
        }
        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.BottomLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.None,
            DescriptorsCount = (uint)n,
            Layout = ElementsLayout.Array,
            GeometryDescriptions = geoms,
        };
        var desc = new BuildRaytracingAccelerationStructureDescription(
            Blas.GPUVirtualAddress, inputs, 0, ScratchBlas.GPUVirtualAddress);
        cmd.BuildRaytracingAccelerationStructure(desc);
    }

    private void CreateInstanceBuffer(ID3D12Device5 device)
    {
        InstanceBuffer = DxrResources.CreateUploadBuffer(device, 64ul, "Instances");
        void* ptr;
        InstanceBuffer.Map(0, null, &ptr).CheckError();
        NativeMemory.Clear(ptr, 64);
        float* m = (float*)ptr;
        m[0] = 1; m[1] = 0; m[2] = 0; m[3] = 0;
        m[4] = 0; m[5] = 1; m[6] = 0; m[7] = 0;
        m[8] = 0; m[9] = 0; m[10]= 1; m[11]= 0;
        byte* b = (byte*)ptr;
        b[51] = 0xFF;
        b[55] = 0;
        *(ulong*)(b + 56) = Blas.GPUVirtualAddress;
        InstanceBuffer.Unmap(0);
    }

    private RaytracingAccelerationStructurePrebuildInfo GetTlasPrebuildInfo(ID3D12Device5 device)
    {
        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.None,
            DescriptorsCount = 1,
            Layout = ElementsLayout.Array,
            InstanceDescriptions = InstanceBuffer.GPUVirtualAddress,
        };
        return device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
    }

    private void AllocTlas(ID3D12Device5 device, RaytracingAccelerationStructurePrebuildInfo info)
    {
        Tlas = DxrResources.CreateDefaultBuffer(device, info.ResultDataMaxSizeInBytes,
            ResourceFlags.AllowUnorderedAccess, ResourceStates.RaytracingAccelerationStructure, "TLAS");
    }

    private void AllocTlasScratch(ID3D12Device5 device, RaytracingAccelerationStructurePrebuildInfo info)
    {
        ScratchTlas = DxrResources.CreateDefaultBuffer(device, info.ScratchDataSizeInBytes,
            ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess, "TLAS_Scratch");
    }

    private void BuildTlas(ID3D12GraphicsCommandList4 cmd)
    {
        var inputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.None,
            DescriptorsCount = 1,
            Layout = ElementsLayout.Array,
            InstanceDescriptions = InstanceBuffer.GPUVirtualAddress,
        };
        var desc = new BuildRaytracingAccelerationStructureDescription(
            Tlas.GPUVirtualAddress, inputs, 0, ScratchTlas.GPUVirtualAddress);
        cmd.BuildRaytracingAccelerationStructure(desc);
    }

    public void Dispose()
    {
        ScratchTlas?.Dispose();
        ScratchBlas?.Dispose();
        AabbBuffer?.Dispose();
        InstanceBuffer?.Dispose();
        SphereBuffer?.Dispose();
        Tlas?.Dispose();
        Blas?.Dispose();
    }
}