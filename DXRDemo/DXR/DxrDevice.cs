using System;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace DXRDemo.DXR;

public sealed partial class DxrDevice : IDisposable
{
    public ID3D12Device5 Device { get; private set; } = null!;
    public ID3D12CommandQueue CommandQueue { get; private set; } = null!;
    public ID3D12CommandAllocator CommandAllocator { get; private set; } = null!;
    public ID3D12GraphicsCommandList4 CommandList { get; private set; } = null!;
    public IDXGIFactory7 DxgiFactory { get; private set; } = null!;
    public ID3D12Fence Fence { get; private set; } = null!;
    /// <summary>Highest shader model reported by the active D3D12 runtime/driver.</summary>
    public ShaderModel HighestShaderModel { get; private set; } = ShaderModel.Model6_5;
    /// <summary>True when DXR 1.2 / SM 6.9 shader features are available.</summary>
    public bool SupportsShaderModel69 => (int)HighestShaderModel >= (int)ShaderModel.Model6_9;

    /// <summary>Selects the app-local Agility runtime before any D3D12 device exists.</summary>
    public static bool TryActivateAgilityRuntime()
    {
        if (!System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "D3D12", "D3D12Core.dll")))
            return false;
        try
        {
            using var configuration = D3D12.D3D12GetInterface<ID3D12SDKConfiguration>(
                D3D12.D3D12SDKConfigurationClsId);
            return configuration.SetSDKVersion(619, @".\D3D12\").Success;
        }
        catch
        {
            return false;
        }
    }

    private ulong _fenceValue;
    private bool _disposed;
    private readonly AutoResetEvent _fenceEvent = new(false);

    public static DxrDevice CreateDefault()
    {
        var device = new DxrDevice();
        try
        {
            device.Initialize();
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public void Initialize(ID3D12Device5? sharedDevice = null)
    {
#if DEBUG
        // Debug layer validates every command list - significant CPU cost per frame.
        // Only enable in Debug builds.
        try
        {
            if (sharedDevice == null)
            {
                using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
                debug.EnableDebugLayer();
            }
        }
        catch
        {
        }
#endif

        DxgiFactory = global::Vortice.DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory7>(false);

        IDXGIAdapter1? adapter = null;
        Device = sharedDevice?.QueryInterface<ID3D12Device5>()!;
        for (uint i = 0; sharedDevice == null && DxgiFactory.EnumAdapters1(i, out adapter).Success; i++)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0)
                continue;

            try
            {
                Device = D3D12.D3D12CreateDevice<ID3D12Device5>(adapter, FeatureLevel.Level_11_0);
                if (Device != null)
                    break;
            }
            catch
            {
            }
        }
        adapter?.Dispose();

        if (Device == null)
            throw new InvalidOperationException("No D3D12 hardware adapter found.");

        // Check DXR support via FeatureDataD3D12Options5.RaytracingTier
        var options = Device.CheckFeatureSupport<FeatureDataD3D12Options5>(Vortice.Direct3D12.Feature.Options5);
        if (options.RaytracingTier < RaytracingTier.Tier1_0)
            throw new NotSupportedException(
                $"GPU does not support DXR. Reported tier: {options.RaytracingTier}");

        // SM 6.9 is exposed by the D3D12 runtime used by the process. Keep a
        // 6.5 fallback because older Windows runtimes and drivers can still
        // support DXR even when the 6.9 feature query is unavailable.
        // HighestShaderModel is an input AND output. A zero-initialized generic
        // query is invalid and silently disabled the HitObject/SER path.
        var shaderModel = new FeatureDataShaderModel { HighestShaderModel = ShaderModel.Model6_9 };
        if (Device.CheckFeatureSupport(Vortice.Direct3D12.Feature.ShaderModel, ref shaderModel))
            HighestShaderModel = shaderModel.HighestShaderModel;
        else
            HighestShaderModel = Device.CheckHighestShaderModel(ShaderModel.Model6_5);

        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        CommandQueue = Device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
        CommandAllocator = Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        CommandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, CommandAllocator, null);
        CommandList.Close();

        Fence = Device.CreateFence<ID3D12Fence>(0);
    }

    /// <summary>Queues a fence signal after the currently submitted work; returns the fence value.</summary>
    public ulong SignalFence()
    {
        ulong value = ++_fenceValue;
        CommandQueue.Signal(Fence, value).CheckError();
        return value;
    }

    /// <summary>Blocks the CPU until the GPU reaches the given fence value.</summary>
    public void WaitForFenceValue(ulong value)
    {
        if (Fence.CompletedValue >= value) return;
        Fence.SetEventOnCompletion(value, _fenceEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
        _fenceEvent.WaitOne();
    }

    /// <summary>Full CPU/GPU sync (used for one-time init flushes).</summary>
    public ulong SignalAndWait()
    {
        ulong value = SignalFence();
        WaitForFenceValue(value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Fence?.Dispose();
        CommandList?.Dispose();
        CommandAllocator?.Dispose();
        CommandQueue?.Dispose();
        Device?.Dispose();
        DxgiFactory?.Dispose();
        _fenceEvent.Dispose();
    }
}
