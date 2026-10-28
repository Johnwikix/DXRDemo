using System.Numerics;
using System.Runtime.InteropServices;

namespace DXRDemo.Scene;

/// <summary>Stores a vertex in the explicit 80-byte GPU layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneVertex
{
    public Vector4 Position, Normal, Tangent, TexCoord, Color;
}

/// <summary>Stores a material texture reference, sampler selection and UV transform (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TextureBinding
{
    public Vector4 Info; // texture index (-1 = absent), UV set, wrap S, wrap T
    public Vector4 Transform; // offset XY, scale XY
    public Vector4 Extra; // rotation, nearest filtering, maximum mip, longest texture edge (0 = query)
    public static TextureBinding Missing => new() { Info = new(-1, 0, 0, 0), Transform = new(0, 0, 1, 1) };
}

/// <summary>Stores metallic-roughness, dielectric transmission/volume factors and texture bindings (432 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneMaterial
{
    public Vector4 BaseColor, Emissive, Factors, Flags;
    public TextureBinding BaseColorMap, MetallicRoughnessMap, NormalMap, OcclusionMap, EmissiveMap;
    /// <summary>Transmission, IOR, thickness, nested priority (1..15; 0 aliases highest priority).</summary>
    public Vector4 Transmission;
    /// <summary>Stores the attenuation RGB color and distance (zero denotes infinite distance).</summary>
    public Vector4 Attenuation;
    /// <summary>References the linear transmission-factor texture, sampled from its red channel.</summary>
    public TextureBinding TransmissionMap;
    /// <summary>References the linear volume-thickness texture, sampled from its green channel.</summary>
    public TextureBinding ThicknessMap;
    public static SceneMaterial Default => new()
    {
        BaseColor = Vector4.One, Factors = new(1, 1, 1, 1), Flags = new(0, .5f, 0, 0),
        BaseColorMap = TextureBinding.Missing, MetallicRoughnessMap = TextureBinding.Missing,
        NormalMap = TextureBinding.Missing, OcclusionMap = TextureBinding.Missing, EmissiveMap = TextureBinding.Missing,
        Transmission = new(0, 1.5f, 0, 0), Attenuation = new(1, 1, 1, 0),
        TransmissionMap = TextureBinding.Missing, ThicknessMap = TextureBinding.Missing
    };
}

/// <summary>Stores a reusable primitive; instances share these indexed vertices.</summary>
public sealed record SceneMesh(string Name, SceneVertex[] Vertices, uint[] Indices, int Material)
{
    /// <summary>文档内稳定的逻辑网格/primitive 身份；未声明身份的资产保持单 primitive 回退。</summary>
    public int LogicalMeshId { get; init; } = -1;
    public int PrimitiveIndex { get; init; } = -1;
}

public sealed record ScenePrimitiveRange(int Mesh, int FirstIndex, int IndexCount, int Material);
public sealed record LogicalSceneMesh(int Id, ScenePrimitiveRange[] Primitives);

/// <summary>Stores a node instance and its complete local-to-world transform.</summary>
public sealed record SceneInstance(string Name, int Mesh, Matrix4x4 Transform)
{
    /// <summary>同一逻辑节点的各材质 primitive 共用此身份，不能按名称或变换猜测合并。</summary>
    public int GeometryInstanceId { get; init; } = -1;
}

/// <summary>Stores a punctual light in world space and glTF photometric units.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneLight
{
    public Vector4 PositionType, DirectionRange, ColorIntensity, Spot;
}

/// <summary>Stores a decoded texture with a full mip chain, in its source transfer function.</summary>
public sealed record SceneTexture(string Name, int Width, int Height, byte[][] Mips, bool Srgb,
    int WrapS = 10497, int WrapT = 10497, int MinFilter = 9987, int MagFilter = 9729)
{
    /// <summary>Optional prebuilt BC7 mip chain; PNG data remains available for CPU light sampling.</summary>
    public byte[][]? Bc7Mips { get; init; }
}

/// <summary>Stores an immutable CPU scene ready for transactional GPU upload.</summary>
public sealed record SceneAsset(string Name, SceneMesh[] Meshes, SceneInstance[] Instances,
    SceneMaterial[] Materials, SceneTexture[] Textures, SceneLight[] Lights, Vector3 Minimum, Vector3 Maximum,
    string[] Warnings)
{
    /// <summary>Gets the authored starting camera, when supplied by the packaged scene.</summary>
    public DXRDemo.Camera.CameraFrame? DefaultCamera { get; init; }
    /// <summary>Optional HDR environment; null selects the procedural sky.</summary>
    public SceneEnvironment? EnvironmentMap { get; init; }
    /// <summary>虚拟几何的逻辑分组；不改变原始 RT mesh/instance 数组的索引。</summary>
    public LogicalSceneMesh[] LogicalMeshes { get; init; } = [];
    /// <summary>Gets the visible triangle count, including instances.</summary>
    public int TriangleCount => Instances.Sum(instance => Meshes[instance.Mesh].Indices.Length / 3);
}

/// <summary>Stores all selectable scenes imported from one glTF/GLB document.</summary>
public sealed record SceneDocument(string Path, SceneAsset[] Scenes, int DefaultScene);
