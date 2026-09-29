using System.IO;
using System.Numerics;
using SharpGLTF.Schema2;
using DXRDemo.Scene;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DXRDemo.Assets.Importers;

/// <summary>Imports glTF 2.0 static scenes into renderer-owned data without touching the GPU.</summary>
public static class GltfImporter
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    { "KHR_lights_punctual", "KHR_texture_transform", "KHR_materials_unlit", "KHR_materials_emissive_strength", "KHR_mesh_quantization",
      "KHR_materials_transmission", "KHR_materials_ior", "KHR_materials_volume" };

    /// <summary>Loads and decodes a document on a worker thread, with cancellation between resources.</summary>
    public static Task<SceneDocument> LoadAsync(string path, CancellationToken cancellation = default)
        => Task.Run(() => LoadCore(path, cancellation), cancellation);

    private static async Task<SceneDocument> LoadCore(string path, CancellationToken cancellation)
    {
        string extension = System.IO.Path.GetExtension(path);
        if (!extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("请选择 glTF 2.0 (.gltf) 或 GLB (.glb) 文件。");
        cancellation.ThrowIfCancellationRequested();
        ModelRoot model = ModelRoot.Load(path);
        foreach (string required in model.ExtensionsRequired)
            if (!SupportedExtensions.Contains(required)) throw new NotSupportedException($"模型需要尚未支持的扩展：{required}。请导出为未压缩的 glTF/GLB 标准材质。");
        var warnings = new List<string>();
        foreach (string used in model.ExtensionsUsed)
            if (!SupportedExtensions.Contains(used)) warnings.Add($"可选扩展 {used} 未启用，使用文件中的标准回退数据。");
        if (model.LogicalSkins.Count != 0) throw new NotSupportedException("当前版本支持静态场景；请先将蒙皮模型烘焙为静态网格。");
        if (model.LogicalAnimations.Count != 0) warnings.Add("当前显示静态姿态，未播放动画。");
        var textures = new List<SceneTexture>();
        var textureMap = new Dictionary<(int, bool), int>();
        var materials = new SceneMaterial[model.LogicalMaterials.Count + 1];
        materials[^1] = SceneMaterial.Default;

        async Task<TextureBinding> Binding(MaterialChannel? channel, bool srgb)
        {
            if (channel is not MaterialChannel c || c.Texture is not Texture texture) return TextureBinding.Missing;
            cancellation.ThrowIfCancellationRequested();
            var key = (texture.LogicalIndex, srgb);
            if (!textureMap.TryGetValue(key, out int index))
            {
                index = textures.Count;
                var image = texture.PrimaryImage?.Content ?? throw new InvalidDataException("贴图没有图像数据。");
                if (!image.IsPng && !image.IsJpg)
                {
                    if (texture.FallbackImage is { } fallback && (fallback.Content.IsPng || fallback.Content.IsJpg)) image = fallback.Content;
                    else throw new NotSupportedException("贴图需要 PNG/JPEG 回退。KTX2/BasisU、DDS 和 WebP 解码尚未接入。");
                }
                var sampler = texture.Sampler;
                var decoded = await Decode(image.Content, texture.Name ?? $"Texture {index}", srgb, cancellation).ConfigureAwait(false);
                textures.Add(decoded with { WrapS = (int)(sampler?.WrapS ?? TextureWrapMode.REPEAT),
                    WrapT = (int)(sampler?.WrapT ?? TextureWrapMode.REPEAT),
                    MinFilter = sampler == null || (int)sampler.MinFilter == 0 ? 9987 : (int)sampler.MinFilter,
                    MagFilter = sampler == null || (int)sampler.MagFilter == 0 ? 9729 : (int)sampler.MagFilter });
                textureMap.Add(key, index);
            }
            int uv = c.TextureTransform?.TextureCoordinateOverride ?? c.TextureCoordinate;
            if (uv is < 0 or > 1) throw new NotSupportedException($"当前支持 TEXCOORD_0/1，材质使用了 TEXCOORD_{uv}。");
            var transform = c.TextureTransform;
            return new TextureBinding { Info = new(index, uv, 0, 0),
                Transform = new(transform?.Offset ?? Vector2.Zero, (transform?.Scale ?? Vector2.One).X, (transform?.Scale ?? Vector2.One).Y),
                Extra = new(transform?.Rotation ?? 0, 0, textures[index].Mips.Length - 1, 0) };
        }

        for (int i = 0; i < model.LogicalMaterials.Count; i++)
        {
            Material m = model.LogicalMaterials[i];
            var baseColor = m.FindChannel("BaseColor"); var mr = m.FindChannel("MetallicRoughness");
            var normal = m.FindChannel("Normal"); var occlusion = m.FindChannel("Occlusion"); var emission = m.FindChannel("Emissive");
            var value = SceneMaterial.Default;
            value.BaseColor = baseColor?.Color ?? Vector4.One;
            Vector4 emissive = emission?.Color ?? Vector4.Zero;
            float strength = Factor(emission, "EmissiveStrength", 1);
            value.Emissive = new(emissive.X * strength, emissive.Y * strength, emissive.Z * strength, 0);
            value.Factors = new(Factor(mr, "MetallicFactor", 1), Factor(mr, "RoughnessFactor", 1),
                Factor(normal, "NormalScale", 1), Factor(occlusion, "OcclusionStrength", 1));
            value.Flags = new(m.Alpha == AlphaMode.MASK ? 1 : m.Alpha == AlphaMode.BLEND ? 2 : 0,
                m.AlphaCutoff, m.DoubleSided ? 1 : 0, m.Unlit ? 1 : 0);
            value.BaseColorMap = await Binding(baseColor, true);
            value.MetallicRoughnessMap = await Binding(mr, false);
            value.NormalMap = await Binding(normal, false);
            value.OcclusionMap = await Binding(occlusion, false);
            value.EmissiveMap = await Binding(emission, true);
            var transmission = m.FindChannel("Transmission");
            var thickness = m.FindChannel("VolumeThickness");
            var attenuation = m.FindChannel("VolumeAttenuation");
            value.Transmission = new(Factor(transmission, "TransmissionFactor", 0), m.IndexOfRefraction,
                Factor(thickness, "ThicknessFactor", 0), 0);
            Vector4 attenuationColor = attenuation?.Color ?? Vector4.One;
            float attenuationDistance = Factor(attenuation, "AttenuationDistance", float.PositiveInfinity);
            value.Attenuation = new(attenuationColor.X, attenuationColor.Y, attenuationColor.Z,
                float.IsFinite(attenuationDistance) && attenuationDistance > 0 ? attenuationDistance : 0);
            value.TransmissionMap = await Binding(transmission, false);
            value.ThicknessMap = await Binding(thickness, false);
            materials[i] = value;
        }

        var meshes = new List<SceneMesh>();
        var primitiveMap = new Dictionary<MeshPrimitive, int>();
        foreach (var mesh in model.LogicalMeshes) foreach (var primitive in mesh.Primitives)
        {
            cancellation.ThrowIfCancellationRequested();
            if (primitive.MorphTargetsCount != 0) throw new NotSupportedException("请先将 Morph Target 烘焙为静态模型。");
            if (primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
                throw new NotSupportedException("DXR 场景支持三角形网格；文件中包含点或线图元。");
            var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array() ?? throw new InvalidDataException("网格缺少 POSITION。");
            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
            var uv0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            var uv1 = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
            var colors = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray();
            var vertices = new SceneVertex[positions.Count];
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 p = positions[v];
                if (!float.IsFinite(p.X + p.Y + p.Z)) throw new InvalidDataException("模型包含非有限顶点。");
                Vector2 a = uv0?[v] ?? default, b = uv1?[v] ?? default;
                vertices[v] = new() { Position = new(p, 1), Normal = new(normals?[v] ?? Vector3.Zero, 0),
                    Tangent = tangents?[v] ?? Vector4.Zero, TexCoord = new(a.X, a.Y, b.X, b.Y), Color = colors?[v] ?? Vector4.One };
            }
            var indices = new List<uint>();
            foreach (var (a, b, c) in primitive.GetTriangleIndices())
            {
                if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length) throw new InvalidDataException("三角形索引越界。");
                indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
            }
            if (indices.Count == 0) throw new InvalidDataException("网格没有可渲染三角形。");
            primitiveMap.Add(primitive, meshes.Count);
            meshes.Add(new(mesh.Name ?? $"Mesh {mesh.LogicalIndex}", vertices, indices.ToArray(), primitive.Material?.LogicalIndex ?? materials.Length - 1));
        }
        var sharedMeshes = meshes.ToArray(); var sharedTextures = textures.ToArray();
        var scenes = new SceneAsset[model.LogicalScenes.Count];
        if (scenes.Length == 0) throw new InvalidDataException("文件没有场景。");
        for (int i = 0; i < scenes.Length; i++)
        {
            var instances = new List<SceneInstance>(); var lights = new List<SceneLight>();
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            void Visit(Node node)
            {
                cancellation.ThrowIfCancellationRequested();
                Matrix4x4 world = node.WorldMatrix;
                if (!Matrix4x4.Invert(world, out _)) throw new InvalidDataException($"节点 {node.Name} 的变换不可逆。");
                if (node.Mesh is { } mesh) foreach (var primitive in mesh.Primitives)
                {
                    int meshIndex = primitiveMap[primitive];
                    instances.Add(new(node.Name ?? mesh.Name ?? "Node", meshIndex, world));
                    foreach (ref readonly var vertex in sharedMeshes[meshIndex].Vertices.AsSpan())
                    {
                        Vector3 p = Vector3.Transform(new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), world);
                        if (!float.IsFinite(p.X + p.Y + p.Z)) throw new InvalidDataException("节点变换产生非有限坐标。");
                        minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
                    }
                }
                if (node.PunctualLight is { } light)
                {
                    Vector3 direction = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world));
                    lights.Add(new() { PositionType = new(world.Translation, light.LightType == PunctualLightType.Directional ? 0 : light.LightType == PunctualLightType.Point ? 1 : 2),
                        DirectionRange = new(direction, float.IsFinite(light.Range) ? light.Range : 0), ColorIntensity = new(light.Color, light.Intensity),
                        Spot = new(MathF.Cos(light.InnerConeAngle), MathF.Cos(light.OuterConeAngle), 0, 0) });
                }
                foreach (Node child in node.VisualChildren) Visit(child);
            }
            foreach (Node root in model.LogicalScenes[i].VisualChildren) Visit(root);
            if (instances.Count == 0) { minimum = new(-1); maximum = new(1); }
            scenes[i] = new(model.LogicalScenes[i].Name ?? $"Scene {i + 1}", sharedMeshes, instances.ToArray(), materials,
                sharedTextures, lights.ToArray(), minimum, maximum, warnings.ToArray());
        }
        return new(System.IO.Path.GetFullPath(path), scenes, model.DefaultScene?.LogicalIndex ?? 0);
    }

    private static float Factor(MaterialChannel? channel, string key, float fallback)
    {
        if (channel is not MaterialChannel c) return fallback;
        foreach (var parameter in c.Parameters) if (parameter.Name == key) return c.GetFactor(key);
        return fallback;
    }

    private static async Task<SceneTexture> Decode(ReadOnlyMemory<byte> bytes, string name, bool srgb, CancellationToken cancellation)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (decoder.PixelWidth > 16384 || decoder.PixelHeight > 16384) throw new NotSupportedException("贴图尺寸超过 D3D12 的 16384 限制。");
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] current = pixels.DetachPixelData();
        int width = checked((int)decoder.PixelWidth), height = checked((int)decoder.PixelHeight);
        var levels = new List<byte[]> { current }; int w = width, h = height;
        while (w > 1 || h > 1)
        {
            cancellation.ThrowIfCancellationRequested();
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2); var next = new byte[checked(nw * nh * 4)];
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) for (int c = 0; c < 4; c++)
            {
                float total = 0; int count = 0;
                // Area coverage includes the last row/column of odd-sized textures.
                for (int sy = y * h / nh; sy < (y + 1) * h / nh; sy++) for (int sx = x * w / nw; sx < (x + 1) * w / nw; sx++)
                {
                    float value = current[(sy * w + sx) * 4 + c] / 255f;
                    total += srgb && c < 3 ? ToLinear(value) : value; count++;
                }
                float average = total / count;
                next[(y * nw + x) * 4 + c] = (byte)Math.Clamp(MathF.Round((srgb && c < 3 ? ToSrgb(average) : average) * 255), 0, 255);
            }
            levels.Add(next); current = next; w = nw; h = nh;
        }
        return new(name, width, height, levels.ToArray(), srgb);
    }
    private static float ToLinear(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f);
    private static float ToSrgb(float x) => x <= .0031308f ? x * 12.92f : 1.055f * MathF.Pow(x, 1 / 2.4f) - .055f;
}
