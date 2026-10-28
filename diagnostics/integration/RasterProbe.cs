using System.Numerics;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Rendering;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

internal static class RasterProbe
{
    internal static void Run()
    {
        DxrDevice.TryActivateAgilityRuntime();
        using var layer = D3D12.D3D12GetDebugInterface<ID3D12Debug>();
        layer.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault(requireRayTracing: false);
        using var debug = device.Device.QueryInterface<ID3D12InfoQueue>();
        debug.SetBreakOnSeverity(MessageSeverity.Error, false);
        debug.SetBreakOnSeverity(MessageSeverity.Corruption, false);
        using var cache = new GpuSceneCache();
        using var backend = new RasterBackend(cache);
        backend.Initialize(device);
        var material = SceneMaterial.Default;
        material.BaseColor = new(0, 0, 0, 1);
        material.Emissive = new(.25f, .5f, .75f, 0);
        var asset = Fixture(material);
        backend.SetScene(asset);
        var camera = CameraFrame.Look(new(0, 0, 3), -Vector3.UnitZ, .9f, .01f, 100);
        var extreme = camera with
        {
            Near = .001f,
            Far = 100000
        };
        var projection = RasterBackend.Projection(extreme, 1);
        double expectedCoefficient = extreme.Near / ((double)extreme.Far - extreme.Near);
        Require(Math.Abs(projection.M33 / expectedCoefficient - 1) < 1e-7, "Reverse-Z perspective lost the small near/far coefficient");
        var frame = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, 0, default);
        float[] color = ReconstructionProbe.ReadTexture(device.Device, frame.Color, true);
        int center = (48 * 128 + 64) * 4;
        Require(Math.Abs(color[center] - .25) < .001 && Math.Abs(color[center + 1] - .5) < .001, "Raster GBuffer/deferred center radiance incorrect");
        Require(cache.AccelerationBuildCount == 0, "Raster eagerly built ray tracing AS");
        // 首个网格故意放在视野外；非零 mesh vertex/index offset 的 quad 必须使用自己的顶点。
        var displaced = asset.Meshes[0].Vertices[..3].Select(vertex => { vertex.Position.X += 100; return vertex; }).ToArray();
        var offsets = asset with
        {
            Meshes = [new("unrelated triangle", displaced, [0, 2, 1], 0), asset.Meshes[0]],
            Instances = [new("nonzero offsets", 1, Matrix4x4.Identity)]
        };
        backend.SetScene(offsets);
        var offsetFrame = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, 0, default);
        var offsetColor = ReconstructionProbe.ReadTexture(device.Device, offsetFrame.Color, true);
        Require(Math.Abs(offsetColor[center] - .25) < .001 && Math.Abs(offsetColor[(32 * 128 + 40) * 4] - .25) < .001,
            "Manual vertex pulling lost mesh vertex/index offsets");
        backend.SetScene(asset);
        var depth = ReconstructionProbe.ReadTexture(device.Device, frame.Depth, false,
            ResourceStates.DepthRead | ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        float expectedDepth = camera.Near * (camera.Far / 3 - 1) / (camera.Far - camera.Near);
        Require(Math.Abs(depth[48 * 128 + 64] - expectedDepth) < 1e-6 && depth.Min() == 0, "Reverse-Z depth contract");
        var extremeFrame = backend.Render(extreme, extreme, default, default, 128, 96, 1.0 / 60, true, 0, default);
        var extremeDepth = ReconstructionProbe.ReadTexture(device.Device, extremeFrame.Depth, false,
            ResourceStates.DepthRead | ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        double extremeExpected = extreme.Near * ((double)extreme.Far / 3 - 1) / (extreme.Far - extreme.Near);
        Require(Math.Abs(extremeDepth[48 * 128 + 64] / extremeExpected - 1) < 1e-6, "Extreme near/far reverse-Z depth lost precision");
        frame = backend.Render(camera, camera, new(.25f, -.25f), new(-.25f, .25f), 128, 96, 1.0 / 60, false, 0, default);
        Require(ReconstructionProbe.ReadTexture(device.Device, frame.Motion, true).Max(Math.Abs) < .002, "Static motion includes raster jitter");
        var moving = camera with
        {
            Origin = camera.Origin + new Vector3(.1f, 0, 0)
        };
        frame = backend.Render(moving, camera, default, default, 128, 96, 1.0 / 60, false, 0, default);
        var motion = ReconstructionProbe.ReadTexture(device.Device, frame.Motion, true);
        float expectedMotion = .1f / (3 * MathF.Tan(camera.VerticalFov * .5f) * (128f / 96)) * 64;
        Require(Math.Abs(motion[(48 * 128 + 64) * 2] - expectedMotion) < .02, "Moving raster motion differs from independent projection");
        float[] Draw(SceneAsset scene)
        {
            backend.SetScene(scene);
            var f = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, 0, default);
            return ReconstructionProbe.ReadTexture(device.Device, f.Color, true);
        }
        var mirrored = asset with
        {
            Instances = [asset.Instances[0] with { Transform = Matrix4x4.CreateScale(-1, 1, 1) }]
        };
        Require(Math.Abs(Draw(mirrored)[center] - .25) < .001, "Mirrored single-sided geometry disappeared");
        var outside = asset with
        {
            Instances = [asset.Instances[0] with { Transform = Matrix4x4.CreateTranslation(100, 0, 0) }]
        };
        Require(Draw(outside)[center] == 0 && backend.VisibleInstances == 0, "Offscreen geometry was not culled");
        var back = asset with
        {
            Instances = [asset.Instances[0] with { Transform = Matrix4x4.CreateRotationY(MathF.PI) }]
        };
        Require(Draw(back)[center] == 0, "Single-sided back face remained visible");
        var twoSided = material;
        twoSided.Flags.Z = 1;
        Require(Math.Abs(Draw(back with
        {
            Materials = [twoSided]
        })[center] - .25) < .001, "Double-sided back face disappeared");
        var mask = material;
        mask.Flags.X = 1;
        mask.BaseColor.W = 0;
        Require(Draw(asset with
        {
            Materials = [mask]
        })[center] == 0, "MASK alpha cutoff did not discard");
        var blend = material;
        blend.Flags.X = 2;
        blend.BaseColor.W = .5f;
        Require(Math.Abs(Draw(asset with
        {
            Materials = [blend]
        })[center] - .125) < .001, "Premultiplied BLEND energy incorrect");
        Require(backend.OpaqueSnapshot.NativePointer != backend.SceneColor.NativePointer, "Refraction aliases its writable output");
        var glass = SceneMaterial.Default;
        glass.Transmission = new(1, 1.5f, 1, 0);
        glass.Attenuation = new(.5f, .5f, .5f, 1);
        var glassAsset = asset with
        {
            Meshes = [asset.Meshes[0], asset.Meshes[0] with { Material = 1 }],
            Instances = [new("background", 0, Matrix4x4.CreateTranslation(0, 0, -1)), new("glass", 1, Matrix4x4.Identity)],
            Materials = [material, glass],
            Minimum = new(-1, -1, -1)
        };
        Require(Math.Abs(Draw(glassAsset)[center] - .25 * .5 * .96) < .003, "Glass snapshot/Fresnel/thickness absorption incorrect");
        Console.WriteLine("PASS: reverse-Z, static/moving motion, mirrored/single/double-sided, MASK/BLEND, separate opaque snapshot and glass absorption.");
        Shadows(device, backend, camera);
        Overflow(device, backend, camera);
        Materials(device, backend, camera);
        Verify(debug);
        Console.WriteLine("PASS: real raster GBuffer/deferred radiance, shared geometry with no AS, D3D12 Error/Corruption=0.");
    }
    private static void Materials(DxrDevice device, RasterBackend backend, CameraFrame camera)
    {
        const int center = (48 * 128 + 64) * 4;
        var material = SceneMaterial.Default;
        material.BaseColor = new(0, 0, 0, 1);
        material.Emissive = Vector4.One;
        material.EmissiveMap.Info.X = 0;
        var texture = new SceneTexture("sRGB gray", 1, 1, [[128, 128, 128, 255]], true);
        float[] Draw(SceneAsset asset, float environment = 0, SunLightSettings sun = default)
        {
            backend.SetScene(asset);
            var frame = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, environment, sun);
            return ReconstructionProbe.ReadTexture(device.Device, frame.Color, true);
        }
        var asset = Fixture(material) with
        {
            Textures = [texture]
        };
        double expected = Math.Pow((128 / 255.0 + .055) / 1.055, 2.4);
        Require(Math.Abs(Draw(asset)[center] - expected) < .001, "sRGB texture was not decoded in linear space");
        texture = new("UV", 2, 1, [[255, 0, 0, 255, 0, 255, 0, 255]], true, MinFilter: 9728, MagFilter: 9728);
        asset = asset with
        {
            Textures = [texture]
        };
        Require(Draw(asset)[center + 1] > .99, "UV0/nearest sampler mismatch");
        material.EmissiveMap.Transform.X = .5f;
        var shifted = Draw(asset with
        {
            Materials = [material]
        });
        Require(shifted[center] > .99 && shifted[center + 1] < .001, "Texture transform/repeat sampler mismatch");
        material.EmissiveMap.Transform.X = 0;
        material.EmissiveMap.Info.Y = 1;
        Require(Draw(asset with
        {
            Materials = [material]
        })[center] > .99, "UV1 selection ignored");
        material = SceneMaterial.Default;
        material.BaseColor = new(1, 0, 0, 1);
        material.Factors.Y = .8f;
        var sun = new SunLightSettings(true, 0, 0, 3, 0);
        asset = Fixture(material);
        var metal = Draw(asset, 0, sun);
        Require(metal[center] > .01 && metal[center + 1] < .001, "Metallic BRDF introduced a diffuse/white lobe");
        material.Factors.X = 0;
        var dielectric = Draw(asset with
        {
            Materials = [material]
        }, 0, sun);
        Require(dielectric[center + 1] > .001, "Dielectric Fresnel specular missing");
        material.NormalMap.Info.X = 0;
        var mapped = Draw(asset with
        {
            Materials = [material],
            Textures = [new("normal", 1, 1, [[255, 128, 128, 255]], false)]
        }, 0, sun);
        Require(mapped[center] < dielectric[center] * .1, "Tangent normal map did not affect shading");
        material.NormalMap = TextureBinding.Missing;
        material.OcclusionMap.Info.X = 0;
        var occluded = Draw(asset with
        {
            Materials = [material],
            Textures = [new("AO", 1, 1, [[0, 0, 0, 255]], false)]
        }, 1);
        Require(occluded[center] < 1e-6, "Occlusion map did not gate fallback environment lighting");
        Console.WriteLine($"PASS: sRGB={expected:F9}, UV0/UV1/transform/repeat/nearest, metallic/dielectric GGX, normal and AO maps.");
    }
    private static void Shadows(DxrDevice device, RasterBackend backend, CameraFrame camera)
    {
        var material = SceneMaterial.Default;
        material.Factors.X = 0;
        material.Factors.Y = .8f;
        var asset = Fixture(material) with
        {
            Instances = [new("floor", 0, Matrix4x4.CreateScale(2))],
            Minimum = new(-2, -2, 0),
            Maximum = new(2, 2, 1)
        };
        var occluded = asset with
        {
            Instances = [asset.Instances[0], new("caster", 0, Matrix4x4.CreateScale(.25f) * Matrix4x4.CreateTranslation(.5f, 0, 1))]
        };
        float[] Draw(SceneAsset scene, SunLightSettings sun)
        {
            backend.SetScene(scene);
            var f = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, 0, sun);
            return ReconstructionProbe.ReadTexture(device.Device, f.Color, true);
        }
        int Check(float[] lit, float[] shadow)
        {
            int count = 0;
            for (int y = 16; y < 80; y++)
                for (int x = 16; x < 112; x++)
                {
                    int k = (y * 128 + x) * 4;
                    if (lit[k] > .1 && shadow[k] < lit[k] * .25)
                        count++;
                }
            return count;
        }
        var sun = new SunLightSettings(true, 36.8699f, 0, 3, 0);
        int sunPixels = Check(Draw(asset, sun), Draw(occluded, sun));
        Require(sunPixels > 4, $"CSM cast shadow missing: {sunPixels}");
        Require(Draw(asset, default).Where((_, index) => index % 4 < 3).All(value => Math.Abs(value) < 1e-6), "Disabled sun leaked radiance");
        var light = new SceneLight { PositionType = new(1, 0, 2, 1), DirectionRange = new(0, 0, -1, 10), ColorIntensity = new(1, 1, 1, 20) };
        int pointPixels = Check(Draw(asset with
        {
            Lights = [light]
        }, default), Draw(occluded with
        {
            Lights = [light]
        }, default));
        Require(pointPixels > 4 && backend.ShadowedLocalLights == 1, $"Point cube shadow missing: {pointPixels}");
        light.PositionType.W = 2;
        light.DirectionRange = new(Vector3.Normalize(new(-1, 0, -2)), 10);
        light.Spot = new(.95f, .8f, 0, 0);
        int spotPixels = Check(Draw(asset with
        {
            Lights = [light]
        }, default), Draw(occluded with
        {
            Lights = [light]
        }, default));
        Require(spotPixels > 4, $"Spot shadow missing: {spotPixels}");
        light.PositionType.W = 0;
        light.DirectionRange = new(-sun.Direction, 0);
        light.ColorIntensity.W = 3;
        int directionPixels = Check(Draw(asset with
        {
            Lights = [light]
        }, default), Draw(occluded with
        {
            Lights = [light]
        }, default));
        Require(directionPixels > 4, $"Embedded directional CSM shadow missing: {directionPixels}");
        Console.WriteLine($"PASS: CSM sun={sunPixels}, point cube={pointPixels}, spot={spotPixels}, embedded directional={directionPixels} shadow pixels.");
    }
    private static void Overflow(DxrDevice device, RasterBackend backend, CameraFrame camera)
    {
        var material = SceneMaterial.Default;
        material.Factors.X = 0;
        var asset = Fixture(material);
        var light = new SceneLight { PositionType = new(0, 0, 0, 0), DirectionRange = new(0, 0, -1, 0), ColorIntensity = new(1, 1, 1, .01f) };
        float[] Draw(SceneLight[] lights)
        {
            backend.SetScene(asset with
            {
                Lights = lights
            });
            var f = backend.Render(camera, camera, default, default, 128, 96, 1.0 / 60, true, 0, default);
            return ReconstructionProbe.ReadTexture(device.Device, f.Color, true);
        }
        float many = Draw(Enumerable.Repeat(light, 70).ToArray())[(48 * 128 + 64) * 4];
        light.ColorIntensity.W = .7f;
        float one = Draw([light])[(48 * 128 + 64) * 4];
        Require(one > .05 && Math.Abs(many - one) < .002, "Cluster overflow silently dropped lights");
        Console.WriteLine($"PASS: 70-light cluster overflow preserved energy: many={many}, equivalent single={one}.");
    }
    internal static SceneAsset Fixture(SceneMaterial material)
    {
        SceneVertex Vertex(float x, float y) => new()
        {
            Position = new(x, y, 0, 1),
            Normal = new(0, 0, 1, 0),
            Tangent = new(1, 0, 0, 1),
            TexCoord = new((x + 1) * .5f, (y + 1) * .5f, 0, 0),
            Color = Vector4.One
        };
        return new("Raster contracts", [new("quad", [Vertex(-1, -1), Vertex(1, -1), Vertex(1, 1), Vertex(-1, 1)], [0, 1, 2, 0, 2, 3], 0)],
            [new("quad", 0, Matrix4x4.Identity)], [material], [], [], new(-1, -1, 0), new(1, 1, 0), []);
    }
    internal static void Verify(ID3D12InfoQueue debug)
    {
        for (ulong i = 0; i < debug.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = debug.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
    }
    internal static void Require(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
