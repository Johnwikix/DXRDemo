using System;
using Vortice.Direct3D;
using Vortice.Direct3D12;

namespace DXRDemo.DXR;

/// <summary>
/// Builds the global root signature for the ray-tracing pipeline.
/// Slot layout (must match HLSL §9.1):
///   [0] t0  SceneBVH       (SRV root descriptor - RaytracingAccelerationStructure is raw)
///   [1] u0..u1 RenderTarget + PrevFrame (typed UAV - must be a descriptor table,
///        since SRV/UAV root descriptors can only be raw/structured buffers)
///   [2] b0  Scene          (CBV root descriptor)
///   [3] t1  Spheres        (SRV root descriptor - StructuredBuffer)
/// </summary>
public sealed class DxrRootSignature : IDisposable
{
    public ID3D12RootSignature RootSignature { get; private set; } = null!;

    public void Build(ID3D12Device5 device)
    {
        var parameters = new RootParameter1[]
        {
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All), // t0 SceneBVH
            new(new RootDescriptorTable1(
                    new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0, 0)),
                ShaderVisibility.All), // u0 RT, u1 PrevFrame (typed UAV table)
            new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All), // b0 Scene
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All), // t1 Spheres
        };

        var description = new RootSignatureDescription1(
            RootSignatureFlags.None,
            parameters,
            samplers: null);

        var versioned = new VersionedRootSignatureDescription(description);

        string err = D3D12.D3D12SerializeVersionedRootSignature(versioned, out Blob blob);
        if (!string.IsNullOrEmpty(err))
            throw new InvalidOperationException($"Root signature serialization failed: {err}");

        RootSignature = device.CreateRootSignature<ID3D12RootSignature>(blob.BufferPointer, blob.BufferSize);
        blob.Dispose();
    }

    public void Dispose()
    {
        RootSignature?.Dispose();
    }
}