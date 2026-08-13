using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.DXR;

public sealed class DxrRenderer : IDisposable
{
    private readonly DxrDevice _device;
    private readonly DxrRootSignature _rootSig;
    private readonly DxrShaderCompiler _compiler;
    private readonly DxrAccelerationStructure _accelerationStructure;
    private readonly DxrPipelineState _pipelineState;
    private readonly DxrShaderTable _shaderTable;
    private readonly DxrSwapChain _swapChain;

    private ID3D12Resource _outputTexture = null!;
    private ID3D12Resource _prevFrameTexture = null!;
    private ID3D12Resource _constantBuffer = null!;
    private ID3D12DescriptorHeap _descriptorHeap = null!;
    private uint _descriptorIncrementSize;
    private uint _width;
    private uint _height;

    public uint Width  => _width;
    public uint Height => _height;

    public DxrRenderer()
    {
        _device   = new DxrDevice();
        _rootSig  = new DxrRootSignature();
        _compiler = new DxrShaderCompiler();
        _accelerationStructure = new DxrAccelerationStructure();
        _pipelineState        = new DxrPipelineState();
        _shaderTable          = new DxrShaderTable();
        _swapChain            = new DxrSwapChain();
    }

    public void Initialize(Microsoft.UI.Xaml.Controls.SwapChainPanel panel, uint width, uint height)
    {
        _width  = Math.Max(1, width);
        _height = Math.Max(1, height);

        void Step(string label, Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                string reason = string.Empty;
                try { reason = _device.GetDeviceRemovedReason(); } catch { }

                string suffix = reason == "S_OK" ? string.Empty : $"  [DeviceRemovedReason: {reason}]";
                throw new InvalidOperationException(
                    $"Init failed at [{label}]: {ex.Message}{suffix}", ex);
            }
        }

        Step("Device init",        () => _device.Initialize());
        Step("Root sig",           () => _rootSig.Build(_device.Device));

        byte[]? dxilBytes = null;
        Step("Compile HLSL",       () => {
            string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR");
            string entryHlsl = Path.Combine(shaderDir, "RayTrace.hlsl");
            if (!File.Exists(entryHlsl))
                throw new FileNotFoundException(
                    $"HLSL not found: {entryHlsl}");
            _compiler.CompileLibrary(entryHlsl);
            dxilBytes = _compiler.DxilBytes;
        });

        Step("Build AS",           () => BuildAccelerationStructures());
        // Flush AS build to GPU before creating pipeline state (which depends on DXR support)
        Step("Flush AS GPU",       () => _device.SignalAndWait());
        Step("Pipeline state",     () => _pipelineState.Build(_device.Device, _rootSig.RootSignature, dxilBytes!,
            new[]{"RayGen","ClosestHit","ShadowClosestHit","Miss","ShadowMiss","IntersectionSphere"}));
        Step("Shader table",       () => _shaderTable.Build(_device.Device, _pipelineState.Properties));
        Step("Frame resources",    () => CreateFrameResources());
        Step("Swap chain",         () => _swapChain.Initialize(_device.DxgiFactory, _device.CommandQueue, panel, _width, _height));
        Step("Flush GPU",          () => _device.SignalAndWait());
    }

    private void BuildAccelerationStructures()
    {
        _device.CommandAllocator.Reset();
        _device.CommandList.Reset(_device.CommandAllocator, null);

        string? failedStep = null;
        try
        {
            _accelerationStructure.Build(_device.Device, _device.CommandList, s => failedStep = s);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"AS step [{failedStep ?? "?"}] failed: {ex.Message}", ex);
        }

        _device.CommandList.Close();
        _device.CommandQueue.ExecuteCommandList(_device.CommandList);
    }

    private void CreateFrameResources()
    {
        _outputTexture = DxrResources.CreateUnorderedAccessTexture2D(
            _device.Device, _width, _height, Format.R8G8B8A8_UNorm,
            ResourceStates.UnorderedAccess, "OutputTexture");

        _prevFrameTexture = DxrResources.CreateUnorderedAccessTexture2D(
            _device.Device, _width, _height, Format.R8G8B8A8_UNorm,
            ResourceStates.UnorderedAccess, "PrevFrame");

        _constantBuffer = DxrResources.CreateUploadBuffer(
            _device.Device, (ulong)Marshal.SizeOf<SceneConstantsGpu>(), "SceneCB");

        _descriptorHeap = _device.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
            new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                2, DescriptorHeapFlags.ShaderVisible, 0));
        _descriptorIncrementSize = _device.Device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public void Resize(Microsoft.UI.Xaml.Controls.SwapChainPanel panel, uint width, uint height)
    {
        if (_width == width && _height == height)
            return;

        _width  = Math.Max(1, width);
        _height = Math.Max(1, height);

        _swapChain.Resize(_device.DxgiFactory, _device.CommandQueue, panel, _width, _height);

        _descriptorHeap?.Dispose();
        _outputTexture?.Dispose();
        _prevFrameTexture?.Dispose();
        CreateFrameResources();

        _device.SignalAndWait();
    }

    public void Render(float time, float mouseX, float mouseY, float cameraDist, int frame)
    {
        if (_width == 0 || _height == 0) return;

        var constants = new SceneConstantsGpu
        {
            Resolution     = new Vector2(_width, _height),
            Mouse          = new Vector2(mouseX, mouseY), // already Y-flipped by caller (ComputeSharp SetMouse semantics)
            Time           = time,
            CameraDistance = cameraDist,
            Frame          = frame,
        };
        UploadConstants(constants);

        _device.CommandAllocator.Reset();
        _device.CommandList.Reset(_device.CommandAllocator, null);

        // Bind the shader-visible descriptor heap before any dispatch uses the descriptor table.
        _device.CommandList.SetDescriptorHeaps(_descriptorHeap);

        // Recreate the typed-UAV descriptors (the two textures swap roles each frame).
        var cpuHandle = _descriptorHeap.GetCPUDescriptorHandleForHeapStart();
        _device.Device.CreateUnorderedAccessView(_outputTexture, null, null, cpuHandle);
        _device.Device.CreateUnorderedAccessView(
            _prevFrameTexture, null, null, cpuHandle + (int)_descriptorIncrementSize);
        var uavTableGpuHandle = _descriptorHeap.GetGPUDescriptorHandleForHeapStart();

        _device.CommandList.SetPipelineState1(_pipelineState.StateObject);
        _device.CommandList.SetComputeRootSignature(_rootSig.RootSignature);

        _device.CommandList.SetComputeRootShaderResourceView(
            0, _accelerationStructure.Tlas.GPUVirtualAddress);
        _device.CommandList.SetComputeRootDescriptorTable(1, uavTableGpuHandle);
        _device.CommandList.SetComputeRootConstantBufferView(
            2, _constantBuffer.GPUVirtualAddress);
        _device.CommandList.SetComputeRootShaderResourceView(
            3, _accelerationStructure.SphereBuffer.GPUVirtualAddress);

        var desc = new DispatchRaysDescription(
            rayGenerationShaderRecord: new GpuVirtualAddressRange(
                _shaderTable.Buffer.GPUVirtualAddress + DxrShaderTable.RayGenOffset,
                DxrShaderTable.RecordSize),
            missShaderTable: new GpuVirtualAddressRangeAndStride(
                _shaderTable.Buffer.GPUVirtualAddress + DxrShaderTable.MissOffset,
                DxrShaderTable.RecordSize * 2,
                DxrShaderTable.RecordSize),
            hitGroupTable: new GpuVirtualAddressRangeAndStride(
                _shaderTable.Buffer.GPUVirtualAddress + DxrShaderTable.HitGroupOffset,
                DxrShaderTable.RecordSize * 2,
                DxrShaderTable.RecordSize),
            callableShaderTable: default,
            width: _width,
            height: _height,
            depth: 1);
        _device.CommandList.DispatchRays(desc);

        // Copy output to swap chain back buffer
        var backBuffer = _swapChain.GetCurrentBackBuffer();
        _device.CommandList.ResourceBarrierTransition(
            _outputTexture, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        _device.CommandList.ResourceBarrierTransition(
            backBuffer, ResourceStates.Present, ResourceStates.CopyDest);

        _device.CommandList.CopyResource(backBuffer, _outputTexture);

        _device.CommandList.ResourceBarrierTransition(
            backBuffer, ResourceStates.CopyDest, ResourceStates.Present);
        // Return the (now previous-frame) output texture to UAV state for the next frame's
        // progressive accumulation read (u1 PrevFrame).
        _device.CommandList.ResourceBarrierTransition(
            _outputTexture, ResourceStates.CopySource, ResourceStates.UnorderedAccess);

        _device.CommandList.Close();
        _device.CommandQueue.ExecuteCommandList(_device.CommandList);

        // Pipeline swap for progressive accumulation
        (_prevFrameTexture, _outputTexture) = (_outputTexture, _prevFrameTexture);

        _swapChain.Present();
        _device.SignalAndWait();
    }

    private unsafe void UploadConstants(SceneConstantsGpu data)
    {
        void* mapped;
        _constantBuffer.Map(0, null, &mapped).CheckError();
        Marshal.StructureToPtr(data, (IntPtr)mapped, false);
        _constantBuffer.Unmap(0);
    }

    public void Dispose()
    {
        _constantBuffer?.Dispose();
        _descriptorHeap?.Dispose();
        _prevFrameTexture?.Dispose();
        _outputTexture?.Dispose();
        _swapChain?.Dispose();
        _shaderTable?.Dispose();
        _pipelineState?.Dispose();
        _accelerationStructure?.Dispose();
        _compiler?.Dispose();
        _rootSig?.Dispose();
        _device?.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SceneConstantsGpu
{
    public Vector2 Resolution;
    public Vector2 Mouse;
    public float   Time;
    public float   CameraDistance;
    public int     Frame;
    public int     _pad;
}