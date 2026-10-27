using System.Numerics;
using Vortice.Direct3D12;

namespace DXRDemo.DXR;

internal sealed unsafe class GpuFrameEffects : IDisposable
{
    private readonly DxrDevice _gpu;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D12RootSignature _loadingRoot, _hudRoot;
    private readonly ID3D12PipelineState _loading, _hud;
    private readonly ID3D12Resource _constants;
    private readonly ID3D12DescriptorHeap _loadingHeap, _hudHeap;
    private readonly uint _descriptorStride;

    internal GpuFrameEffects(DxrDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        _loadingRoot = Keep(device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            [new RootParameter1(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0, 0)), ShaderVisibility.All)])));
        _hudRoot = Keep(device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
        [
            new RootParameter1(new RootDescriptorTable1(
                new DescriptorRange1(DescriptorRangeType.ConstantBufferView, 3, 0, 0),
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0, 0)), ShaderVisibility.All)
        ])));
        string shader = Path.Combine(AppContext.BaseDirectory, "Shaders/DXR/FrameEffects.hlsl");
        _loading = Keep(Compile(shader, "Loading", _loadingRoot));
        _hud = Keep(Compile(shader, "Hud", _hudRoot));
        _constants = Keep(DxrResources.CreateUploadBuffer(device, 256, "HUD constants"));
        _loadingHeap = Keep(device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible)));
        _hudHeap = Keep(device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, DescriptorHeapFlags.ShaderVisible)));
        _descriptorStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    private T Keep<T>(T value) where T : IDisposable
    {
        _owned.Add(value);
        return value;
    }

    private ID3D12PipelineState Compile(string shader, string entry, ID3D12RootSignature root)
    {
        using var compiler = new DxrShaderCompiler();
        compiler.CompileCompute(shader, entry);
        return _gpu.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = root,
            ComputeShader = compiler.DxilBytes
        });
    }

    internal void DrawLoading(GpuTexture<Rgba64> target)
    {
        _gpu.Device.CreateUnorderedAccessView(target.Resource, null, null, _loadingHeap.GetCPUDescriptorHandleForHeapStart());
        _gpu.ExecuteAndWait(commands =>
        {
            commands.SetDescriptorHeaps(_loadingHeap);
            commands.SetComputeRootSignature(_loadingRoot);
            commands.SetPipelineState(_loading);
            commands.SetComputeRootDescriptorTable(0, _loadingHeap.GetGPUDescriptorHandleForHeapStart());
            commands.Dispatch((uint)(target.Width + 7) / 8, (uint)(target.Height + 7) / 8, 1);
            commands.ResourceBarrierUnorderedAccessView(target.Resource);
        });
    }

    internal void DrawHud(GpuTexture<Rgba64> target, ID3D12Resource font, ID3D12Resource text,
        float white, int scale, int width, int height)
    {
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        *(Vector4*)pointer = new(white, scale, 0, 0);
        _constants.Unmap(0);
        var device = _gpu.Device;
        var start = _hudHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateConstantBufferView(new ConstantBufferViewDescription(_constants.GPUVirtualAddress, 256), start);
        device.CreateConstantBufferView(new ConstantBufferViewDescription(font.GPUVirtualAddress, (uint)font.Description.Width), start + (int)_descriptorStride);
        device.CreateConstantBufferView(new ConstantBufferViewDescription(text.GPUVirtualAddress, (uint)text.Description.Width), start + (int)(2 * _descriptorStride));
        device.CreateUnorderedAccessView(target.Resource, null, null, start + (int)(3 * _descriptorStride));
        _gpu.ExecuteAndWait(commands =>
        {
            commands.SetDescriptorHeaps(_hudHeap);
            commands.SetComputeRootSignature(_hudRoot);
            commands.SetPipelineState(_hud);
            commands.SetComputeRootDescriptorTable(0, _hudHeap.GetGPUDescriptorHandleForHeapStart());
            commands.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
            commands.ResourceBarrierUnorderedAccessView(target.Resource);
        });
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
