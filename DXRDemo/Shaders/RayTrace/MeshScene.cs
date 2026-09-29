using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using ComputeSharp;
using DXRDemo.Camera;
using DXRDemo.Scene;

namespace DXRDemo.Shaders.RayTrace;

public sealed record MeshChoice(string Name, string FileName, int TriangleCount)
{
    public override string ToString() => $"{Name} · {TriangleCount:N0} triangles";
}

public sealed record MeshData(Float4[] Triangles, Float4[] Nodes)
{
    public static readonly MeshChoice[] Choices =
    [new("Bunny", "bun_zipper", 69451), new("Armadillo", "Armadillo", 345944), new("Dragon", "dragon_vrip", 871414)];

    private static readonly Task<MeshData>?[] Loads = new Task<MeshData>[Choices.Length];
    public static Task<MeshData> LoadAsync(int index)
    {
        lock (Loads) return Loads[index] ??= Task.Run(() => Load(Choices[index]));
    }

    private static MeshData Load(MeshChoice choice)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", choice.FileName + ".mesh.gz"));
        using var stream = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        int triangles = reader.ReadInt32(), nodes = reader.ReadInt32();
        if (triangles != choice.TriangleCount || nodes <= 0 || nodes > triangles * 2)
            throw new InvalidDataException("Invalid mesh header: " + choice.Name);
        var triangleData = new Float4[checked(triangles * 3)];
        var nodeData = new Float4[checked(nodes * 3)];
        stream.ReadExactly(MemoryMarshal.AsBytes(triangleData.AsSpan()));
        stream.ReadExactly(MemoryMarshal.AsBytes(nodeData.AsSpan()));
        return new MeshData(triangleData, nodeData);
    }
}

public interface IMeshTraceBackend : IDisposable
{
    string Name { get; }
    void Initialize(GraphicsDevice device);
    void Trace(MeshData mesh, int width, int height, int samples, int bounces, int frame,
        Float2 orbit, float distance, ReadWriteTexture2D<Float4> output, ReadWriteTexture2D<Rgba32, Float4> normals,
        ReadWriteTexture2D<Float4>? surfaces = null, Float2 jitter = default, ReadWriteTexture2D<Float4>? normalRoughness = null, CameraFrame? camera = null, SunLightSettings? sun = null, ReadWriteTexture2D<Float4>? directLight = null, float environment = 1);
}

public sealed class SoftwareMeshBackend : IMeshTraceBackend
{
    private GraphicsDevice _device = null!;
    private MeshData? _mesh;
    private ReadOnlyBuffer<Float4>? _triangles, _nodes;
    public string Name => "COMPUTESHARP / SM BVH";
    public void Initialize(GraphicsDevice device) => _device = device;
    public void Trace(MeshData mesh, int width, int height, int samples, int bounces, int frame,
        Float2 orbit, float distance, ReadWriteTexture2D<Float4> output, ReadWriteTexture2D<Rgba32, Float4> normals,
        ReadWriteTexture2D<Float4>? surfaces = null, Float2 jitter = default, ReadWriteTexture2D<Float4>? normalRoughness = null, CameraFrame? camera = null, SunLightSettings? sun = null, ReadWriteTexture2D<Float4>? directLight = null, float environment = 1)
    {
        if (!ReferenceEquals(mesh, _mesh))
        {
            _triangles?.Dispose(); _nodes?.Dispose();
            _triangles = _device.AllocateReadOnlyBuffer(mesh.Triangles);
            _nodes = _device.AllocateReadOnlyBuffer(mesh.Nodes);
            _mesh = mesh;
        }
        CameraFrame view = camera ?? CameraFrame.FromOrbit(orbit.X, orbit.Y, distance);
        SunLightSettings light = sun ?? SunLightSettings.Legacy;
        _device.For(width, height, new MeshPathTraceShader(width, height, samples, bounces, frame,
            normalRoughness != null ? 2 : surfaces != null ? 1 : 0, directLight != null ? 1 : 0, environment, jitter, _triangles!, _nodes!, output, normals, surfaces ?? output, normalRoughness ?? output, directLight ?? output,
            new(view.Origin.X, view.Origin.Y, view.Origin.Z), new(view.Forward.X, view.Forward.Y, view.Forward.Z),
            new(view.Right.X, view.Right.Y, view.Right.Z), new(view.Up.X, view.Up.Y, view.Up.Z),
            new(light.Direction.X, light.Direction.Y, light.Direction.Z), new(light.Radiance.X, light.Radiance.Y, light.Radiance.Z)));
    }
    public void Dispose() { _triangles?.Dispose(); _nodes?.Dispose(); }
}
