using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;

namespace DXRDemo.Rendering;

/// <summary>
/// 一个宿主、一个设备的场景资源所有者。上传及现有消费者均等待自己的 GPU fence 后返回；
/// 租约必须在消费者提交完成后释放。异步提交接入前须增加宿主 fence 退休机制。
/// </summary>
public sealed partial class GpuSceneCache : IDisposable
{
    internal const int MaxTextures = 1024;
    private readonly object _gate = new();
    private readonly DxrDevice _gpu = new();
    private readonly Dictionary<SceneAsset, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private bool _initialized, _disposed, _deviceDisposed;
    private int _stride;
    internal int UploadCount
    {
        get; private set;
    }
    internal int AccelerationBuildCount
    {
        get; private set;
    }
    internal int ResidentSceneCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    internal void Initialize(ID3D12Device5 device)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                if (_gpu.Device.NativePointer != device.NativePointer)
                    throw new InvalidOperationException("GpuSceneCache 不能跨 D3D12 设备共享。");
                return;
            }
            try
            {
                _gpu.Initialize(device, requireRayTracing: false);
                _stride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(Vortice.Direct3D12.DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
                _initialized = true;
            }
            catch
            {
                _disposed = true;
                DisposeDeviceIfUnused();
                throw;
            }
        }
    }

    internal GpuSceneLease Acquire(SceneAsset asset, bool rayTracing = false, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
                throw new InvalidOperationException("先初始化 GpuSceneCache。");
            cancellation.ThrowIfCancellationRequested();
            bool created = !_entries.TryGetValue(asset, out Entry? entry);
            entry ??= new Entry(BuildScene(asset, cancellation));
            try
            {
                if (rayTracing && entry.RayTracing == null)
                {
                    entry.RayTracing = BuildRayTracingScene(entry.Scene, cancellation);
                    AccelerationBuildCount++;
                }
                cancellation.ThrowIfCancellationRequested();
                if (created)
                {
                    _entries.Add(asset, entry);
                    UploadCount++;
                }
                entry.References++;
                return new GpuSceneLease(this, entry);
            }
            catch
            {
                if (created)
                    entry.Dispose();
                throw;
            }
        }
    }

    internal sealed class Entry(GpuScene scene) : IDisposable
    {
        internal readonly GpuScene Scene = scene;
        internal RayTracingScene? RayTracing;
        internal int References;
        public void Dispose()
        {
            try
            {
                RayTracing?.Dispose();
            }
            finally { Scene.Dispose(); }
        }
    }

    internal void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.References != 0)
                return;
            _entries.Remove(entry.Scene.Asset);
            try
            {
                entry.Dispose();
            }
            finally { DisposeDeviceIfUnused(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            // 关闭缓存禁止新租用；已有租约仍有效，最后归还时再释放设备引用。
            DisposeDeviceIfUnused();
        }
    }

    private void DisposeDeviceIfUnused()
    {
        if (!_disposed || _entries.Count != 0 || _deviceDisposed)
            return;
        _deviceDisposed = true;
        _gpu.Dispose();
    }
}

internal sealed class GpuSceneLease(GpuSceneCache owner, GpuSceneCache.Entry entry) : IDisposable
{
    private GpuSceneCache? _owner = owner;
    internal GpuScene Scene => _owner != null ? entry.Scene : throw new ObjectDisposedException(nameof(GpuSceneLease));
    internal RayTracingScene? RayTracing => _owner != null ? entry.RayTracing : throw new ObjectDisposedException(nameof(GpuSceneLease));
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
}
