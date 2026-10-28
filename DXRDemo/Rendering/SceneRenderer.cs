using DXRDemo.DXR;
using DXRDemo.Hdr;
using DXRDemo.SuperResolution;

namespace DXRDemo.Rendering;

/// <summary>窗口级公共帧处理所有者；缓存入口只借用，渲染线程停止后统一释放。</summary>
public sealed class SceneRenderer : IDisposable
{
    private readonly DxrDevice _gpu = new();
    private readonly object _gate = new();
    private object? _active;
    private bool _initialized, _disposed;
    private GpuInterval? _timer;
    internal ReconstructionService Reconstruction { get; private set; } = null!;
    internal DisplayOutput Display { get; private set; } = null!;
    internal double OutputGpuMilliseconds
    {
        get; private set;
    }
    internal int PipelineRevision
    {
        get; private set;
    }
    internal void Initialize(DxrDevice device)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                if (_gpu.Device.NativePointer != device.Device.NativePointer)
                    throw new InvalidOperationException("公共帧服务不能跨设备共享。");
                return;
            }
            try
            {
                _gpu.Initialize(device.Device, requireRayTracing: false);
                Reconstruction = new(device);
                Display = new(device);
                _timer = new(_gpu);
                _initialized = true;
            }
            catch { Dispose(); throw; }
        }
    }
    internal bool Activate(object pipeline)
    {
        if (ReferenceEquals(_active, pipeline))
            return false;
        _active = pipeline;
        PipelineRevision++;
        Reconstruction.Reset();
        return true;
    }
    internal bool Output(in LinearFrame frame, GpuTexture<Rgba64> target, in HdrRenderParameters hdr, float exposure)
    {
        _gpu.CommandAllocator.Reset();
        _gpu.CommandList.Reset(_gpu.CommandAllocator);
        _timer!.Begin(_gpu.CommandList);
        try
        {
            var color = Reconstruction.Record(_gpu.CommandList, frame);
            Display.Record(_gpu.CommandList, color, target, hdr, exposure);
            Reconstruction.Complete(_gpu.CommandList);
            _timer.End(_gpu.CommandList);
            _gpu.CommandList.Close();
        }
        catch (Exception error)
        {
            _gpu.CommandList.Close(); Reconstruction.Reset();
            if (error is VendorReconstructionException && _gpu.Device.DeviceRemovedReason.Success)
            {
                Reconstruction.FallbackToNative(error.Message);
                return false;
            }
            throw;
        }
        _gpu.CommandQueue.ExecuteCommandList(_gpu.CommandList);
        _gpu.SignalAndWait();
        OutputGpuMilliseconds = _timer.ReadMilliseconds();
        _gpu.Device.DeviceRemovedReason.CheckError();
        return true;
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var cleanup = new ResourceCleanup();
        if (_initialized)
            cleanup.Run(() => _gpu.SignalAndWait());
        cleanup.Release(Reconstruction);
        cleanup.Release(Display);
        cleanup.Release(_timer);
        cleanup.Release(_gpu);
        cleanup.Finish();
    }
}
