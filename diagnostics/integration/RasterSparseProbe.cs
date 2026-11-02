using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;

/// <summary>验证稀疏 GI 的真实 NRD、奇数尺寸、运动及阴影缓存失效。</summary>
internal static class RasterSparseProbe
{
    internal static void Run(DxrDevice device, GpuSceneCache cache)
    {
        using var backend = new RasterBackend(cache, 256, 128, hardwareLighting: true, cachedGi: false);
        backend.Initialize(device);
        var material = SceneMaterial.Default;
        material.BaseColor = new(.5f, .5f, .5f, 1);
        material.Factors = new(0, .6f, 1, 1);
        var asset = RasterProbe.Fixture(material) with { EnvironmentMap = new(8, 4, Enumerable.Repeat(new Vector4(.5f,.5f,.5f,0),32).ToArray()) };
        backend.SetScene(asset);
        var camera = CameraFrame.Look(new(0,0,3), -Vector3.UnitZ, .9f,.01f,100);
        var sun = new SunLightSettings(true,315,45,3,0);
        backend.Render(camera,camera,default,default,65,49,1.0/60,true,1,sun);
        RasterProbe.Require(backend.NrdActive && backend.NrdDispatchCount > 0 && !backend.ShadowsReused && backend.ShadowDrawCount > 0,"Sparse GI/NRD or first shadow update missing");
        var still = backend.Render(camera,camera,new(.25f,-.25f),default,65,49,1.0/60,false,1,sun);
        RasterProbe.Require(backend.ShadowsReused && backend.ShadowDrawCount == 0 && ReconstructionProbe.ReadTexture(device.Device,still.Color,true).All(float.IsFinite),"Jitter rebuilt shadow maps or odd dimensions produced invalid color");
        var moving = camera with { Origin = camera.Origin + new Vector3(.1f,0,0) };
        backend.Render(moving,camera,default,default,65,49,1.0/60,false,1,sun);
        RasterProbe.Require(!backend.ShadowsReused,"Moving camera retained old cascades");
        var motion = ReconstructionProbe.ReadTexture(device.Device,backend.NrdMotion!,true);
        float expected=.1f/(3*MathF.Tan(camera.VerticalFov*.5f)*65/49)*65*.5f/4;
        float error=Math.Abs(motion[(6*17+8)*2]-expected);
        RasterProbe.Require(error<.002,"Sparse NRD motion is not in low-resolution pixels");
        backend.Render(moving,moving,default,default,65,49,1.0/60,false,1,sun with { Azimuth = 150 });
        RasterProbe.Require(!backend.ShadowsReused,"Sun change retained old shadow maps");
        backend.SetScene(asset with { Name = "new identity" });
        backend.Render(moving,moving,default,default,65,49,1.0/60,true,1,sun);
        RasterProbe.Require(!backend.ShadowsReused,"Scene change retained old shadow maps");
        var reset=backend.Render(camera,camera,default,default,63,47,1.0/60,true,0,default);
        RasterProbe.Require(backend.NrdActive && ReconstructionProbe.ReadTexture(device.Device,reset.Color,true).Where((_,i)=>i%4!=3).Max(Math.Abs)<1e-5,"Sparse resize/reset retained old illumination");
        // 巨大的远处包围盒不能抹掉眼前的小 caster；旧的 scene-radius CSM 在此没有足够 texel。
        using var shadowBackend = new RasterBackend(cache, 1024,128);
        shadowBackend.Initialize(device);
        var shadowAsset = RasterProbe.Fixture(material) with {
            Instances = [new("receiver",0,Matrix4x4.CreateScale(2))], Minimum = new(-1000), Maximum = new(1000) };
        var sideSun = new SunLightSettings(true,36.8699f,0,3,0);
        float[] ShadowDraw(SceneAsset scene) {
            shadowBackend.SetScene(scene);
            return ReconstructionProbe.ReadTexture(device.Device,shadowBackend.Render(camera,camera,default,default,128,96,1.0/60,true,0,sideSun).Color,true);
        }
        var lit = ShadowDraw(shadowAsset);
        var cast = ShadowDraw(shadowAsset with { Instances = [shadowAsset.Instances[0],new("small caster",0,Matrix4x4.CreateScale(.12f)*Matrix4x4.CreateTranslation(.5f,0,1))] });
        int shadowPixels=0;
        for(int y=16;y<80;y++) for(int x=16;x<112;x++) {
            int k=(y*128+x)*4;
            if(lit[k]>.1f && cast[k]<lit[k]*.25f) shadowPixels++;
        }
        RasterProbe.Require(shadowPixels>4,$"Large scene lost small close shadow: {shadowPixels}");
        Console.WriteLine($"PASS: 0.24m caster in 2000m bounds retains {shadowPixels} dark shadow pixels at 128x96.");
        Console.WriteLine($"PASS: sparse NRD dispatches={backend.NrdDispatchCount}, input 17x13 for 65x49; motion error={error:R}; odd resize/reset and shadow cache invalidation.");
    }
}
