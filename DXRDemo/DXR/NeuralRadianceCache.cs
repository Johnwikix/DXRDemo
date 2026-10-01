using System.IO;
using Vortice.Direct3D12;

namespace DXRDemo.DXR;

/// <summary>GPU-only 32-32-3 MLP with independent sample gradients and a batched Adam update.</summary>
internal sealed class NeuralRadianceCache : IDisposable
{
    internal const int MaxSamples = 256, WeightCount = 1155, CoverageCells = 3072;
    internal const int SampleStride = 80;
    internal const int QueryCounter = CoverageCells, ValidCounter = CoverageCells + 1, TotalCounter = CoverageCells + 2;
    private readonly ID3D12Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D12Resource _dummy;
    private ID3D12RootSignature? _root;
    private ID3D12PipelineState _reset = null!, _clear = null!, _train = null!, _optimize = null!;
    internal ID3D12Resource Network { get; private set; }
    internal ID3D12Resource Samples { get; private set; }
    internal ID3D12Resource Gradients { get; private set; }
    internal ID3D12Resource Coverage { get; private set; }
    internal uint TrainingSteps { get; private set; }
    internal uint ResetCount { get; private set; }
    internal bool ResetPending { get; set; } = true;
    internal bool Allocated => _root != null;
    internal NeuralRadianceCache(ID3D12Device device)
    {
        _device = device;
        _dummy = Keep(DxrResources.CreateDefaultBuffer(device, 256, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess));
        Network = Samples = Gradients = Coverage = _dummy;
    }
    private T Keep<T>(T item) where T : IDisposable { _owned.Add(item); return item; }
    private ID3D12Resource Buffer(ulong size) => Keep(DxrResources.CreateDefaultBuffer(_device, size,
        ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess));
    internal void EnsureAllocated()
    {
        if (Allocated) return;
        var parameters = new RootParameter1[5];
        parameters[0] = new(new RootConstants(1, 0, 4), ShaderVisibility.All);
        for (uint i = 0; i < 4; i++) parameters[i + 1] = new(RootParameterType.UnorderedAccessView, new RootDescriptor1(20 + i, 0), ShaderVisibility.All);
        var root = Keep(_device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None, parameters)));
        ID3D12PipelineState Compile(string entry)
        {
            using var compiler = new DxrShaderCompiler();
            compiler.CompileCompute(Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "NeuralRadianceCache.hlsl"), entry);
            return Keep(_device.CreateComputePipelineState(new() { RootSignature = root, ComputeShader = compiler.DxilBytes }));
        }
        _reset = Compile("ResetNetwork"); _clear = Compile("ClearBatch");
        _train = Compile("TrainSamples"); _optimize = Compile("OptimizeWeights");
        Network = Buffer(WeightCount * 16); Samples = Buffer(MaxSamples * SampleStride);
        Gradients = Buffer(MaxSamples * WeightCount * 4); Coverage = Buffer((CoverageCells + 4) * 4);
        _root = root;
    }
    internal void Bind(ID3D12GraphicsCommandList cmd, uint firstRoot)
    {
        cmd.SetComputeRootUnorderedAccessView(firstRoot, Network.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(firstRoot + 1, Samples.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(firstRoot + 2, Gradients.GPUVirtualAddress);
        cmd.SetComputeRootUnorderedAccessView(firstRoot + 3, Coverage.GPUVirtualAddress);
    }
    private void BindTraining(ID3D12GraphicsCommandList cmd, uint sampleCount, uint step)
    {
        cmd.SetComputeRootSignature(_root!); Bind(cmd, 1);
        cmd.SetComputeRoot32BitConstant(0, sampleCount, 0); cmd.SetComputeRoot32BitConstant(0, step, 1);
    }
    // Called before DXR, after the previous frame's fence. Reset does not depend on camera motion.
    internal void BeginFrame(ID3D12GraphicsCommandList cmd)
    {
        BindTraining(cmd, MaxSamples, 0);
        if (ResetPending)
        {
            cmd.SetPipelineState(_reset); cmd.Dispatch((CoverageCells + 4 + 63) / 64, 1, 1);
            cmd.ResourceBarrierUnorderedAccessView(Network); cmd.ResourceBarrierUnorderedAccessView(Samples);
            cmd.ResourceBarrierUnorderedAccessView(Coverage);
            TrainingSteps = 0; ResetCount++; ResetPending = false;
        }
        cmd.ResourceBarrierUnorderedAccessView(Samples);
        cmd.SetPipelineState(_clear); cmd.Dispatch((MaxSamples + 63) / 64, 1, 1);
        cmd.ResourceBarrierUnorderedAccessView(Coverage);
        cmd.ResourceBarrierUnorderedAccessView(Samples);
    }
    internal void Train(ID3D12GraphicsCommandList cmd, uint sampleCount)
    {
        BindTraining(cmd, sampleCount, TrainingSteps + 1);
        cmd.ResourceBarrierUnorderedAccessView(Network);
        cmd.ResourceBarrierUnorderedAccessView(Samples); cmd.ResourceBarrierUnorderedAccessView(Coverage);
        cmd.ResourceBarrierUnorderedAccessView(Gradients);
        cmd.SetPipelineState(_train); cmd.Dispatch((sampleCount + 31) / 32, 1, 1);
        cmd.ResourceBarrierUnorderedAccessView(Gradients); cmd.ResourceBarrierUnorderedAccessView(Coverage);
        cmd.SetPipelineState(_optimize); cmd.Dispatch((WeightCount + 63) / 64, 1, 1);
        cmd.ResourceBarrierUnorderedAccessView(Network);
        TrainingSteps++;
    }
    public void Dispose() { for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose(); }
}
