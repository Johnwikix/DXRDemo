using DXRDemo.DXR;
using DXRDemo.Shaders.RayTrace;

namespace DXRDemo.Shaders;

public static class ShaderCatalog
{
    public static IReadOnlyList<ShaderAuthoringInfo> All { get; } =
    [new("ray-trace", "DXR Path Trace", "Hardware triangle tracing with HDR10 and denoising",
        new("RT Demo", null, "See model attribution"), ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution,
        null, static () => new RayTracePass(new DxrMeshBackend())
        {
            SceneIndex = RayTracePass.RainyCornerSceneIndex,
            ExternalLightingEnabled = false,
            Exposure = -2
        })];
}

public sealed record ShaderAuthoringInfo(string Id, string DisplayName, string Description,
    ShaderAuthor Author, ShaderCapabilities Capabilities, string? OriginalUrl, Func<IShaderPass> Factory);
