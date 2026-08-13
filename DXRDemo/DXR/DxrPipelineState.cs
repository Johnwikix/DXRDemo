using System;
using Vortice.Direct3D12;

namespace DXRDemo.DXR;

public sealed class DxrPipelineState : IDisposable
{
    public ID3D12StateObject StateObject { get; private set; } = null!;
    public ID3D12StateObjectProperties Properties { get; private set; } = null!;

    public void Build(
        ID3D12Device5 device,
        ID3D12RootSignature globalRootSig,
        ReadOnlyMemory<byte> dxilBytes,
        string[] exports)
    {
        var exportDescs = new ExportDescription[exports.Length];
        for (int i = 0; i < exports.Length; i++)
            exportDescs[i] = new ExportDescription(exports[i]);

        var subObjects = new StateSubObject[]
        {
            new(new DxilLibraryDescription(dxilBytes, exportDescs)),
            new(new HitGroupDescription("HitGroup_Sphere",
                HitGroupType.ProceduralPrimitive,
                closestHitShaderImport: "ClosestHit",
                intersectionShaderImport: "IntersectionSphere")),
            new(new GlobalRootSignature(globalRootSig)),
            new(new RaytracingPipelineConfig(maxTraceRecursionDepth: 1)),
            new(new RaytracingShaderConfig(maxPayloadSizeInBytes: 64, maxAttributeSizeInBytes: 4)),
        };

        StateObject = device.CreateStateObject<ID3D12StateObject>(new StateObjectDescription(
            StateObjectType.RaytracingPipeline, subObjects));
        Properties = StateObject.QueryInterface<ID3D12StateObjectProperties>();
    }

    public void Dispose()
    {
        Properties?.Dispose();
        StateObject?.Dispose();
    }
}