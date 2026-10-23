using System.Numerics;
using DXRDemo.Assets.Importers;
using DXRDemo.Camera;

namespace DXRDemo.Scene;

public enum RtxptScene { Kitchen, Convergence, Bistro }

/// <summary>Packaged, portable RTXPT assets. Runtime loading never depends on a developer checkout.</summary>
public static class RtxptAssets
{
    public const string KitchenPath = "RTXPT/Kitchen/kitchen.gltf";
    public const string ConvergencePath = "RTXPT/ConvergenceTest/ConvergenceTest.gltf";
    public const string BistroPath = "RTXPT/Bistro/bistro.gltf";
    // Bistro's authored scene uses auto exposure. Use a visible fixed starting point
    // for this renderer's manual exposure control; users can still override it.
    public static float InitialExposure(RtxptScene scene) => scene switch
    { RtxptScene.Bistro => 6, RtxptScene.Convergence => 0, _ => 1.3f };

    public static Task<SceneDocument> LoadAsync(bool convergence = false,
        CancellationToken cancellation = default, int maximumTextureSize = 16384) =>
        LoadAsync(convergence ? RtxptScene.Convergence : RtxptScene.Kitchen, cancellation, maximumTextureSize);

    public static async Task<SceneDocument> LoadAsync(RtxptScene selected,
        CancellationToken cancellation = default, int maximumTextureSize = 16384)
    {
        bool convergence = selected == RtxptScene.Convergence, bistro = selected == RtxptScene.Bistro;
        string relative = selected switch { RtxptScene.Kitchen => KitchenPath, RtxptScene.Convergence => ConvergencePath,
            RtxptScene.Bistro => BistroPath, _ => throw new ArgumentOutOfRangeException(nameof(selected)) };
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Models", relative);
        SceneDocument document = await GltfImporter.LoadAsync(path, cancellation, maximumTextureSize).ConfigureAwait(false);
        Vector3 origin = bistro ? new(-20, 1.8f, 12) : convergence ? new(0, 2, 8) : new(1.211005f, 1.804751f, 3.852390f);
        Quaternion rotation = bistro ? Quaternion.Normalize(new(0, -.7071068f, 0, .7071068f)) :
            convergence ? Quaternion.Identity : Quaternion.Normalize(new(-.020305f, .224962f, .004690f, .974145f));
        // Authored cameras from kitchen-proc-sky / convergence-test / bistro-programmer-art.
        CameraFrame camera = CameraFrame.Look(origin, Vector3.Transform(-Vector3.UnitZ, rotation),
            bistro ? 1.04f : convergence ? .660595f : .628029f, convergence || bistro ? .001f : .01f, 1000);
        var scenes = document.Scenes.Select(scene => scene with
        {
            Name = bistro ? "RTXPT · Bistro 街角" : convergence ? "RTXPT · Convergence Test" : "RTXPT · Kitchen",
            DefaultCamera = camera,
            Lights = convergence ? [.. scene.Lights, new SceneLight
            {
                PositionType = Vector4.Zero,
                DirectionRange = new(Vector3.Transform(-Vector3.UnitZ,
                    Quaternion.Normalize(new(-.23053891f, -.15879166f, -.6890466f, .66846975f))), 0),
                ColorIntensity = new(1, 1, 1, 2)
            }] : scene.Lights
        }).ToArray();
        return document with { Scenes = scenes };
    }
}
