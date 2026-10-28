using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

internal static class GpuSceneCacheProbe
{
    internal static void Run()
    {
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        using var cache = new GpuSceneCache();
        SceneAsset asset = Fixture();
        Expect<InvalidOperationException>(() => cache.Acquire(asset));
        cache.Initialize(device.Device);
        Require(Marshal.SizeOf<GpuSceneInstance>() == 160, "InstanceData ABI changed");
        using var raster = cache.Acquire(asset);
        using var second = cache.Acquire(asset);
        Require(ReferenceEquals(raster.Scene, second.Scene) && raster.RayTracing == null && cache.AccelerationBuildCount == 0,
            "Base consumers duplicated scene resources or eagerly built AS");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Expect<OperationCanceledException>(() => cache.Acquire(asset, true, canceled.Token));
        Require(raster.RayTracing == null && cache.AccelerationBuildCount == 0, "Canceled acquisition changed AS state");
        using var firstBackend = new DxrSceneBackend(cache) { ReferenceMode = true };
        using var secondBackend = new DxrSceneBackend(cache) { ReferenceMode = true };
        firstBackend.Initialize(device); secondBackend.Initialize(device);
        firstBackend.SetScene(asset); secondBackend.SetScene(asset);
        Require(cache.UploadCount == 1 && cache.AccelerationBuildCount == 1 && cache.ResidentSceneCount == 1,
            "Shared scene uploaded or built AS more than once");
        Require(ReferenceEquals(firstBackend.SharedScene, raster.Scene) && ReferenceEquals(secondBackend.SharedScene, raster.Scene) &&
            ReferenceEquals(firstBackend.SharedRayTracing, secondBackend.SharedRayTracing), "Backends did not share base/AS ownership");
        Require(firstBackend.OutputHeap.NativePointer != secondBackend.OutputHeap.NativePointer, "Mutable output descriptors were shared");

        using var targets = new Targets(device);
        Vector4[,] expected = targets.Trace(firstBackend, 41);
        Require(expected.Cast<Vector4>().Any(v => v.X > .05f), "Scene rendered no radiance");
        Vector4[,] actual = targets.Trace(secondBackend, 41);
        Require(expected.Cast<Vector4>().SequenceEqual(actual.Cast<Vector4>()), "Shared consumers rendered different deterministic pixels");
        // 交错执行并改变目标尺寸，覆盖描述符重绑与每个后端自己的历史/输出。
        using (var resized = new Targets(device, 48, 32)) _ = resized.Trace(firstBackend, 42);
        Require(expected.Cast<Vector4>().SequenceEqual(targets.Trace(secondBackend, 41).Cast<Vector4>()),
            "Other consumer resize changed this backend's bindings");

        SceneAsset invalid = asset with { Instances = [] };
        Expect<InvalidDataException>(() => firstBackend.SetScene(invalid));
        SceneAsset brokenTexture = asset with { Textures = [new("broken", 2, 2, [new byte[1]], true)] };
        Expect<InvalidDataException>(() => firstBackend.SetScene(brokenTexture));
        Require(ReferenceEquals(firstBackend.SharedScene, raster.Scene) && cache.ResidentSceneCount == 1,
            "Rejected candidate replaced or leaked active resources");
        // 资源已上传但尚未提交的候选也必须在放弃后归还，不按名称错误命中。
        SceneAsset replacement = asset with { Name = asset.Name };
        using (IPreparedScene candidate = ((ISceneUploader)firstBackend).PrepareScene(replacement, default))
        {
            Require(cache.ResidentSceneCount == 2 && ReferenceEquals(firstBackend.SharedScene, raster.Scene),
                "Prepared candidate published early or used value equality as cache identity");
        }
        Require(cache.ResidentSceneCount == 1, "Uncommitted candidate was retained");
        // D3D12CreateDevice 对同一适配器返回同一设备；WARP 只用于制造不同设备身份，绝不执行生产渲染。
        using (var warp = device.DxgiFactory.EnumWarpAdapter<Vortice.DXGI.IDXGIAdapter>())
        using (var otherDevice = D3D12.D3D12CreateDevice<ID3D12Device5>(warp, Vortice.Direct3D.FeatureLevel.Level_11_0))
        {
            Require(device.Device.NativePointer != otherDevice.NativePointer, "Device isolation fixture reused original device");
            Expect<InvalidOperationException>(() => cache.Initialize(otherDevice));
        }

        firstBackend.Dispose(); firstBackend.Dispose();
        Require(expected.Cast<Vector4>().SequenceEqual(targets.Trace(secondBackend, 41).Cast<Vector4>()),
            "Disposing another backend invalidated shared resources");
        // 缓存关闭后仍可完成已有租约的提交；禁止新租用，最后归还才销毁设备引用。
        cache.Dispose(); cache.Dispose();
        Expect<ObjectDisposedException>(() => cache.Acquire(asset));
        Require(expected.Cast<Vector4>().SequenceEqual(targets.Trace(secondBackend, 41).Cast<Vector4>()),
            "Closing cache invalidated outstanding leases");
        secondBackend.Dispose(); second.Dispose(); raster.Dispose(); raster.Dispose();
        Require(cache.ResidentSceneCount == 0, "Last lease did not release resources");
        // 销毁之后重新初始化独立缓存；候选失败不能污染后续上传。
        using (var fresh = new GpuSceneCache())
        {
            fresh.Initialize(device.Device);
            Expect<InvalidDataException>(() => fresh.Acquire(invalid));
            Expect<InvalidDataException>(() => fresh.Acquire(brokenTexture));
            using (var lease = fresh.Acquire(asset)) Require(lease.RayTracing == null, "Base reload constructed AS");
            Require(fresh.ResidentSceneCount == 0, "Unused scenes retained native memory");
        }
        for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = debug.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        Console.WriteLine("PASS: shared base scene + lazy LOD0 AS, distinct writable descriptors, deterministic pixels identical, resize/disposal/cancellation/candidate/device isolation, zero idle scenes; D3D12 Error/Corruption=0.");
    }

    private sealed class Targets : IDisposable
    {
        private readonly GpuTexture<Vector4> _raw, _surface, _guide;
        private readonly GpuTexture<Rgba32> _normals;
        private readonly PbrSignals _signals;
        internal Targets(DxrDevice device, int width = 64, int height = 48)
        {
            _raw = device.CreateTexture2D<Vector4>(width, height);
            _normals = device.CreateTexture2D<Rgba32>(width, height);
            _surface = device.CreateTexture2D<Vector4>(width, height);
            _guide = device.CreateTexture2D<Vector4>(width, height);
            _signals = new PbrSignals(device, width, height);
        }
        internal Vector4[,] Trace(DxrSceneBackend backend, int frame)
        {
            var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .7f, .01f, 100);
            backend.Trace(camera, frame, 4, 3, default, 1, _raw, _normals, _surface, _guide, _signals);
            return _raw.ToArray();
        }
        public void Dispose() { _signals.Dispose(); _guide.Dispose(); _surface.Dispose(); _normals.Dispose(); _raw.Dispose(); }
    }

    private static SceneAsset Fixture()
    {
        SceneVertex Vertex(float x, float y) => new() { Position = new(x, y, 0, 1), Normal = new(0, 0, 1, 0),
            Tangent = new(1, 0, 0, 1), TexCoord = new((x + 1) * .5f, (y + 1) * .5f, 0, 0), Color = Vector4.One };
        SceneVertex[] vertices = [Vertex(-1, -1), Vertex(1, -1), Vertex(1, 1), Vertex(-1, 1)];
        SceneMaterial material = SceneMaterial.Default;
        material.Flags.Z = 1; material.Emissive = new(.3f, .4f, .1f, 0); material.BaseColorMap.Info.X = 0;
        byte[] texels = [255, 32, 16, 255, 16, 255, 32, 255, 32, 16, 255, 255, 192, 160, 128, 255];
        return new("shared GPU fixture", [new("quad", vertices, [0, 1, 2, 0, 2, 3], 0)],
            [new("quad", 0, Matrix4x4.Identity)], [material], [new("color", 2, 2, [texels], true)], [], new(-1, -1, 0), new(1, 1, 0), []);
    }

    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
