using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;

/// <summary>用封闭房间、颜色反弹和屏幕外镜面验证真实 AS 查询，避免以环境占位冒充 GI。</summary>
internal static class RasterHardwareProbe
{
    internal static void Run(DxrDevice device, GpuSceneCache cache, RasterBackend backend)
    {
        RasterProbe.Require(backend.HardwareLightingActive, "Hardware GI test requires actual DXR execution");
        const int width = 64, height = 48;
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        var receiver = SceneMaterial.Default;
        receiver.BaseColor = new(.5f, .5f, .5f, 1);
        receiver.Factors = new(0, 1, 1, 1);
        var black = receiver;
        black.BaseColor = new(0, 0, 0, 1);
        black.Factors.X = 1;
        black.Flags.Z = 1;
        var room = Room(receiver, black);
        var uniform = new SceneEnvironment(8, 4, Enumerable.Repeat(new Vector4(.5f, .5f, .5f, 0), 32).ToArray());
        room = room with { EnvironmentMap = uniform };
        Vector3 Mean(SceneAsset asset, float environment = 1, int frames = 8)
        {
            backend.SetScene(asset);
            Vector3 sum = default;
            for (int i = 0; i < frames; i++)
            {
                var frame = backend.Render(camera, camera, default, default, width, height, 1.0 / 60, i == 0, environment, default);
                var color = ReconstructionProbe.ReadTexture(device.Device, frame.Color, true);
                RasterProbe.Require(color.All(float.IsFinite), "Hardware GI produced non-finite radiance");
                for (int y = 18; y < 30; y++)
                    for (int x = 24; x < 40; x++)
                    {
                        int k = (y * width + x) * 4;
                        sum += new Vector3(color[k], color[k + 1], color[k + 2]);
                    }
            }
            return sum / (frames * 12 * 16);
        }
        var open = Mean(room with { Instances = [room.Instances[0]] });
        var closed = Mean(room);
        RasterProbe.Require(open.X is > .20f and < .30f && closed.Length() < 1e-5,
            $"Geometry did not occlude constant environment: open={open}, closed={closed}");
        Console.WriteLine($"PASS: constant environment open={open}, black enclosure={closed}; expected diffuse .24 plus GGX specular.");

        // 直接光相同，只改变屏幕外墙色；白色接收面的红色增量必须来自次级命中。
        var point = new SceneLight { PositionType = new(0, 0, 2, 1), DirectionRange = new(0, 0, -1, 20), ColorIntensity = new(1, 1, 1, 8) };
        var dark = Mean(room with { Lights = [point] }, 0);
        var red = receiver;
        red.BaseColor = new(.8f, .02f, .02f, 1);
        red.Flags.Z = 1;
        var bounce = Mean(room with { Materials = [receiver, red], Lights = [point] }, 0);
        Vector3 delta = bounce - dark;
        RasterProbe.Require(delta.X > .015f && delta.X > delta.Y * 2,
            $"Offscreen diffuse wall did not bounce red light: dark={dark}, bounce={bounce}");
        Console.WriteLine($"PASS: one-bounce wall color transfer: dark={dark}, red={bounce}, delta={delta}.");

        var emitter = black;
        emitter.Emissive = new(1, .1f, .05f, 0);
        var lit = Mean(room with { Materials = [receiver, emitter] }, 0);
        RasterProbe.Require(lit.X > .2 && lit.X > lit.Y * 3,
            $"Shared emitter proposal failed: {lit}");
        Console.WriteLine($"PASS: emissive enclosure illuminates receiver with MIS: {lit}.");

        camera = camera with { VerticalFov = .25f };
        var mirror = SceneMaterial.Default;
        mirror.Factors = new(1, .02f, 1, 1);
        var asset = RasterProbe.Fixture(mirror);
        var vertices = asset.Meshes[0].Vertices.Select(v => { v.Normal = new(.38268343f, 0, .92387953f, 0); return v; }).ToArray();
        emitter.Emissive = new(4, .2f, .1f, 0);
        asset = asset with
        {
            Meshes = [asset.Meshes[0] with { Vertices = vertices }, asset.Meshes[0] with { Material = 1 }],
            Materials = [mirror, emitter],
            Instances = [asset.Instances[0], new("behind camera emitter", 1,
                Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(8, 0, 8))],
            Minimum = new(-1, -2, 0), Maximum = new(8, 2, 10)
        };
        var reflection = Mean(asset, 0, 1);
        RasterProbe.Require(backend.VisibleInstances == 1 && reflection.X > 2 && reflection.X > reflection.Y * 10,
            $"RT failed to reflect culled, offscreen emitter: {reflection}, visible={backend.VisibleInstances}");
        var removed = Mean(asset with { Instances = [asset.Instances[0]] }, 0, 1);
        RasterProbe.Require(removed.Length() < 1e-5, $"Stale reflection after scene change: {removed}");
        // 私有 compute resolve 读完 OpaqueColor 后，透明 draw 会再次读取更新后的快照。
        // 该组合必须遵守静态 SRV 的绑定生存期，不能只测无透明的合成场景。
        var blend = receiver;
        blend.Flags.X = 2; blend.BaseColor.W = .5f;
        var transparentScene = RasterProbe.Fixture(receiver);
        transparentScene = transparentScene with
        {
            Meshes = [transparentScene.Meshes[0], transparentScene.Meshes[0] with { Material = 1 }],
            Materials = [receiver, blend], Lights = [point],
            Instances = [transparentScene.Instances[0], new("transparent", 1, Matrix4x4.CreateTranslation(0, 0, .5f))]
        };
        backend.SetScene(transparentScene);
        backend.Render(camera, camera, default, default, 32, 24, 1.0 / 60, true, 1, new SunLightSettings(true, 315, 45, 3, 0));
        int uploads = cache.UploadCount, builds = cache.AccelerationBuildCount;
        using var lease = cache.Acquire(backend.SharedScene!.Asset, rayTracing: true);
        RasterProbe.Require(ReferenceEquals(lease.Scene, backend.SharedScene) && lease.RayTracing != null && cache.UploadCount == uploads && cache.AccelerationBuildCount == builds,
            "Hardware raster failed to reuse the shared GPU scene/AS");
        Console.WriteLine($"PASS: offscreen RT reflection={reflection}, removed={removed}; resize and shared AS lease reuse.");
    }

    private static SceneAsset Room(SceneMaterial receiver, SceneMaterial wall)
    {
        var asset = RasterProbe.Fixture(receiver);
        var quad = asset.Meshes[0];
        return asset with
        {
            Meshes = [quad, quad with { Material = 1 }], Materials = [receiver, wall],
            Instances = [new("receiver", 0, Matrix4x4.CreateScale(10)),
                new("back", 1, Matrix4x4.CreateScale(10) * Matrix4x4.CreateTranslation(0, 0, 4)),
                new("left", 1, Matrix4x4.CreateScale(10) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-10, 0, 2)),
                new("right", 1, Matrix4x4.CreateScale(10) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 2)),
                new("top", 1, Matrix4x4.CreateScale(10) * Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 10, 2)),
                new("bottom", 1, Matrix4x4.CreateScale(10) * Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, -10, 2))],
            Minimum = new(-10, -10, 0), Maximum = new(10, 10, 4)
        };
    }
}
