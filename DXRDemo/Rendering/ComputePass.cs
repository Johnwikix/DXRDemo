using System.IO;
using DXRDemo.DXR;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>固定描述符布局的原生计算阶段。调用者在 fence 完成后才允许改写描述符和常量。</summary>
internal sealed class ComputePass : GpuResourceOwner
{
    private readonly ID3D12Device5 _device;
    private readonly ID3D12RootSignature _root;
    private readonly ID3D12PipelineState _pipeline;
    private readonly ID3D12DescriptorHeap _heap;
    private readonly int _srvCount, _stride;
    internal ComputePass(ID3D12Device5 device, string file, string entry, int srvCount, int uavCount)
    {
        _device = device;
        _srvCount = srvCount;
        _stride = (int)device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        try
        {
            _root = Keep(device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
                [new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
                 new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, (uint)srvCount, 0),
                     new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, (uint)uavCount, 0)), ShaderVisibility.All)])));
            using var compiler = new DxrShaderCompiler();
            compiler.CompileCompute(Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", file), entry);
            _pipeline = Keep(device.CreateComputePipelineState(new()
            {
                RootSignature = _root,
                ComputeShader = compiler.DxilBytes
            }));
            _heap = Keep(device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                (uint)(srvCount + uavCount), DescriptorHeapFlags.ShaderVisible)));
            var srv = new ShaderResourceViewDescription
            {
                Format = Format.R32G32B32A32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new() { MipLevels = 1 }
            };
            var uav = new UnorderedAccessViewDescription { Format = Format.R32G32B32A32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D };
            for (int i = 0; i < srvCount; i++)
                device.CreateShaderResourceView(null, srv, _heap.GetCPUDescriptorHandleForHeapStart() + i * _stride);
            for (int i = 0; i < uavCount; i++)
                device.CreateUnorderedAccessView(null, null, uav, _heap.GetCPUDescriptorHandleForHeapStart() + (srvCount + i) * _stride);
        }
        catch { Dispose(); throw; }
    }
    internal void Srv(int index, ID3D12Resource resource)
    {
        ShaderResourceViewDescription? view = resource.Description.Format == Format.R32_Typeless
            ? new ShaderResourceViewDescription
            {
                Format = Format.R32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new() { MipLevels = 1 }
            } : null;
        _device.CreateShaderResourceView(resource, view, _heap.GetCPUDescriptorHandleForHeapStart() + index * _stride);
    }
    internal void Uav(int index, ID3D12Resource resource) => _device.CreateUnorderedAccessView(resource, null, null, _heap.GetCPUDescriptorHandleForHeapStart() + (_srvCount + index) * _stride);
    internal void Dispatch(ID3D12GraphicsCommandList4 commands, ID3D12Resource constants, int width, int height)
    {
        commands.SetDescriptorHeaps(_heap);
        commands.SetComputeRootSignature(_root);
        commands.SetComputeRootConstantBufferView(0, constants.GPUVirtualAddress);
        commands.SetComputeRootDescriptorTable(1, _heap.GetGPUDescriptorHandleForHeapStart());
        commands.SetPipelineState(_pipeline);
        commands.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
    }
}
