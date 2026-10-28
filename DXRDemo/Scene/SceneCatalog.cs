using System.IO;
using DXRDemo.Assets.Importers;

namespace DXRDemo.Scene;

/// <summary>两种渲染管线共用的打包场景身份与加载入口。</summary>
public static class SceneCatalog
{
    public const int RainyCorner = 0, Kitchen = 1, Convergence = 2, Bistro = 3;
    public static IReadOnlyList<SceneChoice> Packaged { get; } = Array.AsReadOnly<SceneChoice>(
    [
        new("雨后便利店 · Rainy Corner", "RainyCorner.glb", 2019650),
        new("RTXPT · Kitchen", RtxptAssets.KitchenPath, 1474751),
        new("RTXPT · Convergence Test", RtxptAssets.ConvergencePath, 46110),
        new("RTXPT · Bistro 街角", RtxptAssets.BistroPath, 4209006)
    ]);

    internal static Task<SceneDocument> LoadAsync(int index, CancellationToken cancellation)
        => index == RainyCorner
            ? GltfImporter.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", Packaged[index].FileName), cancellation)
            : RtxptAssets.LoadAsync((RtxptScene)(index - 1), cancellation);
}

public sealed record SceneChoice(string Name, string FileName, int TriangleCount)
{
    public override string ToString() => $"{Name} · {TriangleCount:N0} triangles";
}
