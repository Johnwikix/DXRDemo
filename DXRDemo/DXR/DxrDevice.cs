using System;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace DXRDemo.DXR;

public sealed class DxrDevice : IDisposable
{
    public ID3D12Device5 Device { get; private set; } = null!;
    public ID3D12CommandQueue CommandQueue { get; private set; } = null!;
    public ID3D12CommandAllocator CommandAllocator { get; private set; } = null!;
    public ID3D12GraphicsCommandList4 CommandList { get; private set; } = null!;
    public IDXGIFactory7 DxgiFactory { get; private set; } = null!;
    public ID3D12Fence Fence { get; private set; } = null!;

    private bool _disposed;

    public void Initialize()
    {
        try
        {
            var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
            debug.EnableDebugLayer();
        }
        catch
        {
        }

        DxgiFactory = global::Vortice.DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory7>(false);

        IDXGIAdapter1? adapter = null;
        for (uint i = 0; DxgiFactory.EnumAdapters1(i, out adapter).Success; i++)
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
                continue;
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

        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        CommandQueue = Device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
        CommandAllocator = Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        CommandList = Device.CreateCommandList<ID3D12GraphicsCommandList4>(
            CommandListType.Direct, CommandAllocator, null);
        CommandList.Close();

        Fence = Device.CreateFence<ID3D12Fence>(0);
    }

    public ulong SignalAndWait()
    {
        ulong value = Fence.CompletedValue + 1;
        CommandQueue.Signal(Fence, value);
        while (Fence.CompletedValue < value)
        {
            Thread.Sleep(0);
        }
        return value;
    }

    public string GetDeviceRemovedReason()
    {
        if (Device == null)
            return "no device";

        var reason = Device.DeviceRemovedReason;
        return reason.Success ? "S_OK" : $"0x{reason.Code:X8} ({reason.Description})";
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
    }
}