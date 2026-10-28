using DXRDemo.Shaders.RayTrace;
using DXRDemo.Scene;
using DXRDemo.Rendering;
using DXRDemo.Shaders.Raster;

namespace DXRDemo.Shaders;

public static class ShaderCatalog
{
    public static IReadOnlyList<ShaderAuthoringInfo> All { get; } =
    [new("ray-trace", "DXR Path Trace · RTXPT", "C# DXR path tracer with RTXPT sampling, PBR transport and HDR10",
        new("RT Demo", null, "See model attribution"), ShaderCapabilities.UsesMouse | ShaderCapabilities.UsesResolution,
        null, static (session, scenes, renderer) => new RayTracePass(session, scenes, renderer)),
     new("raster", "光栅化 · Clustered Deferred", "PBR GBuffer, CSM, forward transparency, Native TAA and shared SR/HDR",
        new("RT Demo",null,"See model attribution"),ShaderCapabilities.UsesMouse|ShaderCapabilities.UsesResolution,
        null,static (session,scenes,renderer)=>new RasterPass(session,scenes,renderer))];
}

public sealed record ShaderAuthoringInfo(string Id, string DisplayName, string Description,
    ShaderAuthor Author, ShaderCapabilities Capabilities, string? OriginalUrl, Func<SceneSession, GpuSceneCache, SceneRenderer, IShaderPass> Factory);
