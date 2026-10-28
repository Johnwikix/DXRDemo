using System.Numerics;
using DXRDemo.Assets.Importers;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

internal static class RasterSceneProbe
{
    internal static async Task Run()
    {
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        using var cache = new GpuSceneCache();
        using var raster = new RasterBackend(cache);
        raster.Initialize(device);
        using var reference = new DxrSceneBackend(cache) { ReferenceMode = true, CausticsEnabled = false, StablePlanesEnabled = false };
        reference.Initialize(device);
        const int width = 160, height = 100;
        using var raw = device.CreateTexture2D<Vector4>(width, height);
        using var normal = device.CreateTexture2D<Rgba32>(width, height);
        using var surface = device.CreateTexture2D<Vector4>(width, height);
        using var guide = device.CreateTexture2D<Vector4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        foreach (int index in new[] { 0, 1, 2, 3 })
        {
            SceneDocument document = index == 0
                ? await GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "RainyCorner.glb"), maximumTextureSize: 256)
                : await RtxptAssets.LoadAsync(index == 1 ? RtxptScene.Kitchen : index == 2 ? RtxptScene.Convergence : RtxptScene.Bistro, maximumTextureSize: 256);
            var source = document.Scenes[document.DefaultScene];
            var materials = (SceneMaterial[])source.Materials.Clone();
            // 这里隔离原始几何与投影；透明/alpha 和光传输由独立的材质探针验证。
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i].Flags.X = 0;
                materials[i].Transmission.X = 0;
            }
            var asset = source with
            {
                Materials = materials
            };
            raster.SetScene(asset);
            reference.SetScene(asset);
            var controller = new CameraController();
            controller.FrameScene(asset, width / (float)height);
            var camera = controller.Advance(0, out _, out _);
            var frame = raster.Render(camera, camera, default, default, width, height, 1.0 / 60, true, 0, default);
            var depths = ReconstructionProbe.ReadTexture(device.Device, frame.Depth, false, ResourceStates.DepthRead | ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
            reference.Trace(camera, 0, 1, 1, default, 0, raw, normal, surface, guide, signals);
            var positions = surface.ToArray();
            var geometry = new GeometryReference(asset);
            int agreement = 0, compared = 0;
            double worstRatio = 0, worstRelativeDepth = 0;
            int snappedChecks = 0;
            string worstDetail = "";
            double ExpectedDepth(int x, int y)
            {
                var sample = positions[y, x];
                if (sample.W <= 0)
                    return 0;
                float u = (x + .5f) * 2 / width - 1, v = (y + .5f) * 2 / height - 1;
                var direction = Vector3.Normalize(camera.Forward + camera.Right * u * (width / (float)height) * MathF.Tan(camera.VerticalFov * .5f) - camera.Up * v * MathF.Tan(camera.VerticalFov * .5f));
                double z = sample.W * Vector3.Dot(direction, camera.Forward);
                return camera.Near * (camera.Far / z - 1) / (camera.Far - camera.Near);
            }
            for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                var position = positions[y, x];
                bool ray = position.W > 0, hit = depths[y * width + x] > 0;
                if (ray == hit)
                    agreement++;
                if (!ray || !hit)
                    continue;
                double expected = ExpectedDepth(x, y), left = ExpectedDepth(x - 1, y), right = ExpectedDepth(x + 1, y), up = ExpectedDepth(x, y - 1), down = ExpectedDepth(x, y + 1);
                if (left <= 0 || right <= 0 || up <= 0 || down <= 0)
                    continue;
                if (Math.Max(Math.Max(Math.Abs(left - expected), Math.Abs(right - expected)), Math.Max(Math.Abs(up - expected), Math.Abs(down - expected))) > expected * .05)
                    continue;
                // 固定功能光栅 XY 吸附至 1/256 像素；与连续射线比较必须计入随深度梯度变化的误差。
                // 两个亚像素格覆盖顶点吸附/插值，另计浮点运算；薄面使用下面的 CPU 三角形回查。
                double gradient = (Math.Abs(right - left) + Math.Abs(down - up)) * .5;
                double budget = gradient / 128 + Math.Abs(expected) * 4e-6 + 1e-10;
                double error = Math.Abs(depths[y * width + x] - expected);
                if (error > budget)
                {
                    // 亚像素取整还可能改变近共面/薄片之间的遮挡者；用独立 CPU 光栅参考回查。
                    // 参考逐三角形投影并取整到规范的 16.8 网格，不使用 GPU 深度推导期望。
                    double snapped = geometry.SnappedDepth(camera, width, height, x, y, position.W);
                    error = Math.Abs(depths[y * width + x] - snapped);
                    budget = Math.Abs(snapped) * 4e-6 + 1e-10;
                    expected = snapped;
                    snappedChecks++;
                }
                if (error / budget > worstRatio)
                {
                    worstRatio = error / budget;
                    worstDetail = $"pixel={x},{y} expected={expected:E9} actual={depths[y * width + x]:E9} gradient={gradient:E9} budget={budget:E9}";
                }
                worstRelativeDepth = Math.Max(worstRelativeDepth, error / Math.Max(expected, 1e-10));
                compared++;
            }
            double coverage = agreement / (double)((width - 2) * (height - 2));
            Console.WriteLine($"Raster/RT geometry {index}: coverage={coverage:F6}, compared={compared}, max depth error/budget={worstRatio:F6}, relative depth error={worstRelativeDepth:E6}, CPU snapped checks={snappedChecks}, meshes={asset.Meshes.Length}, instances={asset.Instances.Length}, {worstDetail}.");
            RasterProbe.Require(coverage > .98 && compared > 100, "Packaged geometry coverage differs from independent RT");
            RasterProbe.Require(worstRatio < 1, "Packaged depth exceeds fixed-function raster precision budget");
            RasterProbe.Verify(debug);
        }
        Console.WriteLine("PASS: all four packaged scenes match independent RT geometry/projection; D3D12 Error/Corruption=0.");
    }
    private sealed class GeometryReference
    {
        private readonly SceneAsset _asset;
        private readonly (Vector3 Min, Vector3 Max)[] _bounds;
        private readonly Matrix4x4[] _inverse;
        internal GeometryReference(SceneAsset asset)
        {
            _asset = asset;
            _bounds = new (Vector3, Vector3)[asset.Meshes.Length];
            _inverse = new Matrix4x4[asset.Instances.Length];
            for (int i = 0; i < asset.Meshes.Length; i++)
            {
                var lo = new Vector3(float.PositiveInfinity);
                var hi = new Vector3(float.NegativeInfinity);
                foreach (var v in asset.Meshes[i].Vertices)
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                _bounds[i] = (lo, hi);
            }
            for (int i = 0; i < _inverse.Length; i++)
                Matrix4x4.Invert(asset.Instances[i].Transform, out _inverse[i]);
        }
        internal double SnappedDepth(CameraFrame camera, int width, int height, int x, int y, float rtDistance)
        {
            float tan = MathF.Tan(camera.VerticalFov * .5f), aspect = width / (float)height;
            var direction = Vector3.Normalize(camera.Forward + camera.Right * ((x + .5f) * 2 / width - 1) * aspect * tan - camera.Up * ((y + .5f) * 2 / height - 1) * tan);
            double raster = 0;
            Matrix4x4 currentTransform = default;
            Vector3 Project(Vector3 p)
            {
                var d = Vector3.Transform(p, currentTransform) - camera.Origin;
                float z = Vector3.Dot(d, camera.Forward);
                return new((Vector3.Dot(d, camera.Right) / (z * aspect * tan) + 1) * width * .5f, (1 - Vector3.Dot(d, camera.Up) / (z * tan)) * height * .5f, (float)(camera.Near * ((double)camera.Far / z - 1) / (camera.Far - camera.Near)));
            }
            for (int i = 0; i < _asset.Instances.Length; i++)
            {
                var instance = _asset.Instances[i];
                var mesh = _asset.Meshes[instance.Mesh];
                currentTransform = instance.Transform;
                var origin = Vector3.Transform(camera.Origin, _inverse[i]);
                var ray = Vector3.TransformNormal(direction, _inverse[i]);
                var bound = _bounds[instance.Mesh];
                var inverse = _inverse[i];
                float norm = MathF.Sqrt(inverse.M11 * inverse.M11 + inverse.M12 * inverse.M12 + inverse.M13 * inverse.M13 + inverse.M21 * inverse.M21 + inverse.M22 * inverse.M22 + inverse.M23 * inverse.M23 + inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32 + inverse.M33 * inverse.M33);
                float padding = rtDistance * tan * 2 / Math.Min(width, height) / 64 * norm;
                float entry = 0, exit = float.PositiveInfinity;
                for (int axis = 0; axis < 3; axis++)
                {
                    float d = ray[axis], o = origin[axis], lo = bound.Min[axis] - padding, hi = bound.Max[axis] + padding;
                    if (Math.Abs(d) < 1e-15)
                    {
                        if (o < lo || o > hi)
                            exit = -1;
                        continue;
                    }
                    float a = (lo - o) / d, b = (hi - o) / d;
                    entry = Math.Max(entry, Math.Min(a, b));
                    exit = Math.Min(exit, Math.Max(a, b));
                }
                if (exit < entry)
                    continue;
                Vector3 Position(uint id)
                {
                    var p = mesh.Vertices[id].Position;
                    return new(p.X, p.Y, p.Z);
                }
                for (int j = 0; j < mesh.Indices.Length; j += 3)
                {
                    var a = Position(mesh.Indices[j]);
                    var b = Position(mesh.Indices[j + 1]);
                    var c = Position(mesh.Indices[j + 2]);
                    var pa = Project(a);
                    var pb = Project(b);
                    var pc = Project(c);
                    double ax = Math.Round(pa.X * 256) / 256, ay = Math.Round(pa.Y * 256) / 256, bx = Math.Round(pb.X * 256) / 256, by = Math.Round(pb.Y * 256) / 256, cx = Math.Round(pc.X * 256) / 256, cy = Math.Round(pc.Y * 256) / 256;
                    double ar = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                    double u0 = ((by - cy) * (x + .5 - cx) + (cx - bx) * (y + .5 - cy)) / ar, v0 = ((cy - ay) * (x + .5 - cx) + (ax - cx) * (y + .5 - cy)) / ar;
                    bool front = ar * (currentTransform.GetDeterminant() < 0 ? -1 : 1) < 0;
                    if ((_asset.Materials[mesh.Material].Flags.Z != 0 || front) && u0 >= 0 && v0 >= 0 && u0 + v0 <= 1 && pa.Z > 0 && pb.Z > 0 && pc.Z > 0 && pa.Z <= 1 && pb.Z <= 1 && pc.Z <= 1)
                        raster = Math.Max(raster, pa.Z * u0 + pb.Z * v0 + pc.Z * (1 - u0 - v0));
                }
            }
            // 仅为封装资产中这些待复核像素提供参考；未实现跨近面的裁剪，不作通用 CPU 渲染器。
            // 无可验证的三角形返回 0，让调用方断言失败，绝不静默放行。
            return raster;
        }
    }
}
