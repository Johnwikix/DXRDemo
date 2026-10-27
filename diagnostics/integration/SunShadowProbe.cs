using System.Numerics;
using System.Text.Json;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Float4 = System.Numerics.Vector4;

// Real production shadow rays: a knife edge at two distances above a diffuse receiver.
internal static unsafe class SunShadowProbe
{
    internal static void Run(string[] args)
    {
        using var debug = D3D12.D3D12GetDebugInterface<ID3D12Debug>(); debug.EnableDebugLayer();
        using var device = DxrDevice.CreateDefault();
        using var native = device.Device.QueryInterface<ID3D12Device5>();
        using var messages = native.QueryInterface<ID3D12InfoQueue>();
        using var backend = new DxrSceneBackend { CausticsEnabled = false, AdaptiveLightingEnabled = false,
            RestirEnabled = false, FireflyThreshold = 0, UseInlineTraversal = !args.Contains("--legacy-trace") };
        backend.Initialize(device);
        const int width = 256, height = 16;
        using var raw = device.CreateTexture2D<Float4>(width, height);
        using var normal = device.CreateTexture2D<Rgba32>(width, height);
        using var surface = device.CreateTexture2D<Float4>(width, height);
        using var guide = device.CreateTexture2D<Float4>(width, height);
        using var signals = new PbrSignals(device, width, height);
        var camera = CameraFrame.Look(new(0, 0, .5f), -Vector3.UnitZ, .02f, .001f, 100);
        SceneMaterial material = SceneMaterial.Default;
        material.BaseColor = Vector4.One; material.Factors.X = 0; material.Factors.Y = 1;
        material.Transmission = new(0, 1, 0, 0); material.Flags.Z = 1;
        SceneMesh Quad(float left, float right) => new("quad",
            [new() { Position=new(left,-10,0,1), Normal=new(0,0,1,0), Color=Vector4.One },
             new() { Position=new(right,-10,0,1), Normal=new(0,0,1,0), Color=Vector4.One },
             new() { Position=new(right,10,0,1), Normal=new(0,0,1,0), Color=Vector4.One },
             new() { Position=new(left,10,0,1), Normal=new(0,0,1,0), Color=Vector4.One }], [0,1,2,0,2,3], 0);
        var meshes = new[] { Quad(-10,10), Quad(-10,0) };
        var scene = new SceneAsset("solar knife edge", meshes, [new("receiver",0,Matrix4x4.Identity)],
            [material], [], [], new(-10,-10,0), new(10,10,9), []);
        double[] Render(float distance, float diameter, out double unfiltered, out double lobeError)
        {
            backend.SetScene(distance > 0 ? scene with { Instances = [scene.Instances[0], new("edge",1,Matrix4x4.CreateTranslation(0,0,distance))] } : scene);
            backend.Trace(camera, 0, 256, 1, default, 0, raw, normal, surface, guide, signals, new(true,0,0,4,diameter));
            var pixels = raw.ToArray(); var direct = signals.Unfiltered.ToArray();
            var diffuse = signals.Diffuse.ToArray(); var specular = signals.Specular.ToArray();
            var factorD = signals.DiffuseFactor.ToArray(); var factorS = signals.SpecularFactor.ToArray();
            var result = new double[width]; unfiltered = lobeError = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Require(float.IsFinite(pixels[y,x].X) && pixels[y,x].X >= 0, "Invalid solar radiance");
                result[x] += pixels[y,x].X / height;
                unfiltered = Math.Max(unfiltered, Math.Abs(direct[y,x].X));
                lobeError = Math.Max(lobeError, Math.Abs(pixels[y,x].X - direct[y,x].X -
                    diffuse[y,x].X * factorD[y,x].X - specular[y,x].X * factorS[y,x].X));
            }
            return result;
        }
        var reference = Render(0,0,out _,out _);
        var softReference = Render(0,.526f,out _,out _);
        var largeReference = Render(0,20,out _,out _);
        var hard = Render(8,0,out _,out _);
        var near = Render(1,.526f,out double directNear,out double nearLobeError);
        var far = Render(8,.526f,out double directFar,out double farLobeError);
        var wider = Render(8,1.052f,out _,out _);
        double Transition(double[] image) => image.Where((value,x) => value / reference[x] is > .1 and < .9).Count();
        double nearWidth = Transition(near), farWidth = Transition(far), wideWidth = Transition(wider);
        double energyError = softReference.Zip(reference,(a,b)=>Math.Abs(a/b-1)).Max();
        double largeError = largeReference.Zip(reference,(a,b)=>Math.Abs(a/b-1)).Max();
        Console.WriteLine($"Solar penumbra 10-90% pixels: delta={Transition(hard)}, 1m={nearWidth}, 8m={farWidth}, double angle={wideWidth}; irradiance errors={energyError:E3}/{largeError:E3}");
        Directory.CreateDirectory("output/sun-shadow");
        File.WriteAllText("output/sun-shadow/profiles.json", JsonSerializer.Serialize(new { reference, hard, near, far, wider, nearWidth, farWidth, wideWidth, energyError, largeError }));
        Require(Transition(hard) == 0, "Zero angular diameter must retain hard shadows");
        Require(nearWidth >= 6 && farWidth > nearWidth * 6 && farWidth < nearWidth * 10,
            "Solar penumbra does not grow with blocker-receiver separation");
        Require(wideWidth > farWidth * 1.7 && wideWidth < farWidth * 2.3, "Doubling solar diameter must widen penumbrae");
        Require(energyError < .001 && largeError < .003, "Changing solar diameter changed total unoccluded irradiance");
        Require(directNear < 1e-6 && directFar < 1e-6, "Stochastic solar samples bypass denoising through Unfiltered");
        Require(Math.Max(nearLobeError,farLobeError) < 1e-4, "Solar lobe signals no longer recompose raw radiance");
        for (ulong i = 0; i < messages.NumStoredMessagesAllowedByRetrievalFilter; i++)
        {
            var message = messages.GetMessage(i);
            Require(message.Severity is not (MessageSeverity.Error or MessageSeverity.Corruption), message.Description);
        }
        Console.WriteLine("PASS distance-dependent solar penumbrae, angular size, normalized energy, and denoiser routing.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
