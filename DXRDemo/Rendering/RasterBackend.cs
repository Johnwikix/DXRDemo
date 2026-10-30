using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DXRDemo.Camera;
using DXRDemo.DXR;
using DXRDemo.Scene;
using PrimitiveTopology = Vortice.Direct3D.PrimitiveTopology;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DXRDemo.Rendering;

/// <summary>共享 LOD0 分簇光栅；可选硬件一跳光照按需借用同一份 AS。</summary>
internal sealed unsafe partial class RasterBackend : IDisposable, ISceneUploader
{
    private const int Textures = GpuSceneCache.MaxTextures, ExtraCount = 10;
    private readonly int ShadowSize, LocalSize;
    private const ResourceStates ShaderRead = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
    private sealed class Resources : GpuResourceOwner
    {
    }
    private readonly DxrDevice _gpu = new();
    private readonly GpuSceneCache _cache;
    private readonly bool _requestHardwareLighting;
    private RasterHardwareLighting? _hardware;
    private uint _frameIndex;
    internal bool HardwareLightingActive => _hardware != null;
    private readonly Resources _owned = new();
    private Resources _sized = new();
    private GpuSceneLease? _lease;
    private ID3D12RootSignature _root = null!;
    private ID3D12PipelineState _depthPass = null!, _gbuffer = null!, _shadow = null!, _transparentPass = null!, _transparentGuides = null!, _clustersPass = null!, _lighting = null!, _reflections = null!;
    private ID3D12Resource? _filteredEnvironment;
    private ID3D12Resource _constants = null!, _sunShadow = null!, _localShadow = null!, _clusters = null!;
    private ID3D12Resource _base = null!, _normal = null!, _emission = null!, _depth = null!, _guideDepth = null!, _color = null!, _motion = null!, _reactive = null!, _opaqueColor = null!;
    private ID3D12DescriptorHeap _heap = null!, _rtv = null!, _dsv = null!;
    private readonly ID3D12DescriptorHeap[] _heaps = new ID3D12DescriptorHeap[2];
    private GpuInterval _timer = null!;
    private int _stride, _rtvStride, _dsvStride, _width, _height;
    private int[] _opaque = [], _transparent = [];
    private Vector3[] _centers = [];
    private float[] _radii = [];
    private int[] _visibleOpaque = [], _visibleTransparent = [];
    private int _visibleOpaqueCount, _visibleTransparentCount;
    internal int VisibleInstances => _visibleOpaqueCount + _visibleTransparentCount;
    private readonly TransparencyOrder _order = new();
    private bool _disposed, _initialized;
    private uint _shadowViewMask;
    internal double GpuMilliseconds
    {
        get; private set;
    }
    internal GpuScene? SharedScene => _lease?.Scene;
    internal ID3D12Resource OpaqueSnapshot => _opaqueColor;
    internal ID3D12Resource SceneColor => _color;
    internal int ShadowedLocalLights
    {
        get; private set;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection, PreviousViewProjection;
        public Vector4 Origin, Forward, Right, Up, Size, Temporal, SunDirection, SunRadiance, Limits, Splits;
        public fixed float ShadowViews[256];
        public Vector4 ShadowIds, PreviousOrigin, Environment, EnvironmentMap;
    }

    /// <summary>离屏探针可显式缩小阴影尺寸；生产默认预算保持不变。</summary>
    internal RasterBackend(GpuSceneCache cache, int shadowSize = 2048, int localSize = 1024, bool hardwareLighting = false)
    {
        if (shadowSize is < 16 or > 2048 || localSize is < 16 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(shadowSize));
        _cache = cache;
        _requestHardwareLighting = hardwareLighting;
        ShadowSize = shadowSize;
        LocalSize = localSize;
    }
    internal void Initialize(DxrDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return;
        try
        {
            _cache.Initialize(device.Device);
            _gpu.Initialize(device.Device, requireRayTracing: false);
            var options = _gpu.Device.CheckFeatureSupport<FeatureDataD3D12Options>(Vortice.Direct3D12.Feature.Options);
            if (options.ResourceBindingTier < ResourceBindingTier.Tier2 || _gpu.HighestShaderModel < ShaderModel.Model6_0)
                throw new NotSupportedException("光栅管线需要 Shader Model 6.0 和 Resource Binding Tier 2。");
            var parameters = new List<RootParameter1> {
                new(RootParameterType.ConstantBufferView, new RootDescriptor1(0,0), ShaderVisibility.All),
                new(new RootConstants(1,0,2), ShaderVisibility.All) };
            for (uint i = 0; i < 4; i++)
                parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(i, 0), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, Textures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.Sampler, Textures, 0, 1)), ShaderVisibility.All));
            parameters.Add(new(new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, 0, 2),
                new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 3, 1, 0)), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All));
            parameters.Add(new(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All));
            _root = _owned.Keep(_gpu.Device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters.ToArray())));
            _depthPass = Graphics("VertexMain", "DepthPixel", [], true, false);
            _gbuffer = Graphics("VertexMain", "GBufferPixel", [Format.R8G8B8A8_UNorm, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float], false, false);
            _shadow = Graphics("ShadowVertex", "DepthPixel", [], true, true);
            _transparentPass = Graphics("VertexMain", "TransparentPixel", [Format.R16G16B16A16_Float], false, false, true);
            _transparentGuides = Graphics("VertexMain", "TransparentGuides", [Format.R16G16_Float, Format.R8_UNorm], true, false);
            _clustersPass = Compute("BuildClusters");
            _lighting = Compute("DeferredLighting");
            _reflections = Compute("ResolveReflections");
            _stride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            _rtvStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            _dsvStride = (int)_gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
            _heap = _owned.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, Textures + ExtraCount * 3, DescriptorHeapFlags.ShaderVisible)));
            _rtv = _owned.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.RenderTargetView, 6)));
            _dsv = _owned.Keep(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new(DescriptorHeapType.DepthStencilView, 19)));
            _heaps[0] = _heap;
            _constants = _owned.Keep(DxrResources.CreateUploadBuffer(_gpu.Device, 2048));
            _sunShadow = _owned.Keep(DepthTexture(ShadowSize, ShadowSize, 4, ShaderRead));
            _localShadow = _owned.Keep(DepthTexture(LocalSize, LocalSize, 12, ShaderRead));
            for (int i = 0; i < 16; i++)
            {
                var view = new DepthStencilViewDescription
                {
                    Format = Format.D32_Float,
                    ViewDimension = DepthStencilViewDimension.Texture2DArray,
                    Texture2DArray = new() { ArraySize = 1, FirstArraySlice = (uint)(i < 4 ? i : i - 4) }
                };
                _gpu.Device.CreateDepthStencilView(i < 4 ? _sunShadow : _localShadow, view, Dsv(i));
            }
            var srv = new ShaderResourceViewDescription
            {
                Format = Format.R32G32B32A32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new() { MipLevels = 1 }
            };
            var uav = new UnorderedAccessViewDescription { Format = Format.R32G32B32A32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D };
            for (int table = 0; table < 3; table++)
            {
                for (int i = 0; i < 7; i++)
                    _gpu.Device.CreateShaderResourceView(null, srv, Handle(Textures + table * ExtraCount + i));
                for (int i = 0; i < 3; i++)
                    _gpu.Device.CreateUnorderedAccessView(null, null, uav, Handle(Textures + table * ExtraCount + 7 + i));
            }
            if (_requestHardwareLighting && _gpu.SupportsRayTracing && _gpu.HighestShaderModel >= ShaderModel.Model6_5)
                _hardware = _owned.Keep(new RasterHardwareLighting(device));
            _timer = _owned.Keep(new GpuInterval(_gpu));
            _initialized = true;
        }
        catch { Dispose(); throw; }
    }
    private ID3D12PipelineState Compute(string entry)
    {
        using var compiler = new DxrShaderCompiler();
        compiler.CompileCompute(ShaderFile, entry);
        return _owned.Keep(_gpu.Device.CreateComputePipelineState(new()
        {
            RootSignature = _root,
            ComputeShader = compiler.DxilBytes
        }));
    }
    private static string ShaderFile => Path.Combine(AppContext.BaseDirectory, "Shaders", "DXR", "Raster.hlsl");
    private ID3D12PipelineState Graphics(string vertex, string pixel, Format[] formats, bool writeDepth, bool shadow, bool transparent = false)
    {
        using var vs = new DxrShaderCompiler();
        vs.CompileGraphics(ShaderFile, vertex);
        using var ps = new DxrShaderCompiler();
        ps.CompileGraphics(ShaderFile, pixel, true);
        var blend = BlendDescription.Opaque;
        if (transparent)
        {
            blend.IndependentBlendEnable = true;
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
        }
        return _owned.Keep(_gpu.Device.CreateGraphicsPipelineState(new()
        {
            RootSignature = _root,
            VertexShader = vs.DxilBytes,
            PixelShader = ps.DxilBytes,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = formats,
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new(1, 0),
            SampleMask = uint.MaxValue,
            BlendState = blend,
            RasterizerState = new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                FrontCounterClockwise = true,
                DepthClipEnable = true,
                DepthBias = shadow ? -100 : 0,
                SlopeScaledDepthBias = shadow ? -1.5f : 0
            },
            DepthStencilState = new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = writeDepth ? DepthWriteMask.All : DepthWriteMask.Zero,
                DepthFunc = writeDepth || transparent ? ComparisonFunction.GreaterEqual : ComparisonFunction.Equal,
                StencilEnable = false
            }
        }));
    }
    private ID3D12Resource DepthTexture(int width, int height, int layers, ResourceStates state)
    {
        var description = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height, (ushort)layers, 1);
        description.Flags = ResourceFlags.AllowDepthStencil;
        return _gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, description, state, new ClearValue(Format.D32_Float, 0, 0));
    }
    private CpuDescriptorHandle Handle(int index) => _heap.GetCPUDescriptorHandleForHeapStart() + index * _stride;
    private CpuDescriptorHandle Rtv(int index) => _rtv.GetCPUDescriptorHandleForHeapStart() + index * _rtvStride;
    private CpuDescriptorHandle Dsv(int index) => _dsv.GetCPUDescriptorHandleForHeapStart() + index * _dsvStride;

    public IPreparedScene PrepareScene(SceneAsset asset, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = _cache.Acquire(asset, rayTracing: _hardware != null, cancellation: cancellation);
        try
        {
            var opaque = new List<int>();
            var transparent = new List<int>();
            var centers = new Vector3[asset.Instances.Length];
            var radii = new float[asset.Instances.Length];
            for (int i = 0; i < asset.Instances.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var instance = asset.Instances[i];
                var mesh = asset.Meshes[instance.Mesh];
                var material = asset.Materials[mesh.Material];
                if (material.Flags.X == 2 || material.Transmission.X > 0)
                    transparent.Add(i);
                else
                    opaque.Add(i);
                Vector3 lo = new(float.PositiveInfinity), hi = new(float.NegativeInfinity);
                foreach (ref readonly var v in mesh.Vertices.AsSpan())
                {
                    var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                centers[i] = Vector3.Transform((lo + hi) * .5f, instance.Transform);
                var m = instance.Transform;
                // Frobenius 范数上界覆盖非均匀缩放和 shear，不能用最大轴长漏裁剪。
                float scale = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13 + m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23 + m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33);
                radii[i] = (hi - lo).Length() * .5f * scale;
            }
            var filtered = RasterEnvironment.Prepare(asset.EnvironmentMap, cancellation);
            var environment = DxrResources.CreateUploadBuffer(_gpu.Device, (ulong)(filtered.Length * 16));
            try
            {
                void* pointer;
                environment.Map(0, null, &pointer).CheckError();
                filtered.AsSpan().CopyTo(new Span<Vector4>(pointer, filtered.Length));
                environment.Unmap(0);
                cancellation.ThrowIfCancellationRequested();
                return new Candidate(this, lease, opaque.ToArray(), transparent.ToArray(), centers, radii, environment);
            }
            catch { environment.Dispose(); throw; }
        }
        catch { lease.Dispose(); throw; }
    }
    internal void SetScene(SceneAsset asset)
    {
        if (ReferenceEquals(asset, _lease?.Scene.Asset))
            return;
        using var candidate = PrepareScene(asset, default);
        candidate.Commit();
    }
    private sealed class Candidate(RasterBackend owner, GpuSceneLease lease, int[] opaque, int[] transparent, Vector3[] centers, float[] radii, ID3D12Resource environment) : IPreparedScene
    {
        private GpuSceneLease? _lease = lease;
        private ID3D12Resource? _environment = environment;
        private readonly int[] _visibleOpaque = new int[opaque.Length], _visibleTransparent = new int[transparent.Length];
        public void Commit()
        {
            var next = _lease ?? throw new InvalidOperationException("候选已提交或释放。");
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            owner._gpu.Device.CopyDescriptorsSimple(Textures, owner.Handle(0), next.Scene.TextureViews.GetCPUDescriptorHandleForHeapStart(), DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            owner._lease?.Dispose();
            owner._lease = next;
            owner._filteredEnvironment?.Dispose();
            owner._filteredEnvironment = _environment;
            _environment = null;
            owner._heaps[1] = next.Scene.Samplers;
            owner._opaque = opaque;
            owner._transparent = transparent;
            owner._centers = centers;
            owner._radii = radii;
            owner._visibleOpaque = _visibleOpaque;
            owner._visibleTransparent = _visibleTransparent;
            _lease = null;
        }
        public void Dispose()
        {
            var cleanup = new ResourceCleanup();
            cleanup.Release(_lease);
            _lease = null;
            cleanup.Release(_environment);
            _environment = null;
            cleanup.Finish();
        }
    }
    private ID3D12Resource Target(int width, int height, Format format)
    {
        var desc = ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1);
        desc.Flags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowUnorderedAccess;
        return _sized.Keep(_gpu.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), HeapFlags.None, desc, ResourceStates.NonPixelShaderResource));
    }
    private void EnsureSize(int width, int height)
    {
        if (_width == width && _height == height)
            return;
        _sized.Dispose();
        _sized = new();
        _width = _height = 0;
        _base = Target(width, height, Format.R8G8B8A8_UNorm);
        _normal = Target(width, height, Format.R16G16B16A16_Float);
        _emission = Target(width, height, Format.R16G16B16A16_Float);
        _color = Target(width, height, Format.R16G16B16A16_Float);
        _motion = Target(width, height, Format.R16G16_Float);
        _reactive = Target(width, height, Format.R8_UNorm);
        _opaqueColor = Target(width, height, Format.R16G16B16A16_Float);
        _depth = _sized.Keep(DepthTexture(width, height, 1, ResourceStates.DepthRead | ShaderRead));
        _guideDepth = _sized.Keep(DepthTexture(width, height, 1, ResourceStates.DepthRead | ShaderRead));
        ulong clusterBytes = checked((ulong)((width + 15) / 16) * ((ulong)(height + 15) / 16) * 24 * 65 * 4);
        _clusters = _sized.Keep(DxrResources.CreateDefaultBuffer(_gpu.Device, clusterBytes, ResourceFlags.AllowUnorderedAccess, ResourceStates.UnorderedAccess));
        ID3D12Resource[] targets = [_base, _normal, _emission, _color, _motion, _reactive];
        for (int i = 0; i < targets.Length; i++)
            _gpu.Device.CreateRenderTargetView(targets[i], null, Rtv(i));
        var depthView = new DepthStencilViewDescription { Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D };
        _gpu.Device.CreateDepthStencilView(_depth, depthView, Dsv(16));
        _gpu.Device.CreateDepthStencilView(_guideDepth, depthView, Dsv(18));
        depthView.Flags = DepthStencilViewFlags.ReadOnlyDepth;
        _gpu.Device.CreateDepthStencilView(_depth, depthView, Dsv(17));
        for (int table = 0; table < 2; table++)
        {
            int start = Textures + table * ExtraCount;
            _gpu.Device.CreateShaderResourceView(_base, null, Handle(start));
            _gpu.Device.CreateShaderResourceView(_normal, null, Handle(start + 1));
            _gpu.Device.CreateShaderResourceView(_emission, null, Handle(start + 2));
            var view = new ShaderResourceViewDescription { Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D, Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new() { MipLevels = 1 } };
            _gpu.Device.CreateShaderResourceView(_depth, view, Handle(start + 3));
            view.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
            view.Texture2DArray = new()
            {
                MipLevels = 1,
                ArraySize = 4
            };
            _gpu.Device.CreateShaderResourceView(_sunShadow, view, Handle(start + 4));
            view.Texture2DArray = new()
            {
                MipLevels = 1,
                ArraySize = 12
            };
            _gpu.Device.CreateShaderResourceView(_localShadow, view, Handle(start + 5));
            _gpu.Device.CreateShaderResourceView(_opaqueColor, null, Handle(start + 6));
        }
        _gpu.Device.CreateUnorderedAccessView(_color, null, null, Handle(Textures + 7));
        _gpu.Device.CreateUnorderedAccessView(_motion, null, null, Handle(Textures + 8));
        _gpu.Device.CreateUnorderedAccessView(_reactive, null, null, Handle(Textures + 9));
        _hardware?.Prepare(width, height, _base, _normal, _depth, _opaqueColor, _color);
        _width = width;
        _height = height;
    }

    private void Bind(bool compute, int extraTable)
    {
        var cmd = _gpu.CommandList;
        var scene = _lease!.Scene;
        cmd.SetDescriptorHeaps(2, _heaps);
        if (compute)
        {
            cmd.SetComputeRootSignature(_root);
            cmd.SetComputeRootConstantBufferView(0, _constants.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(3, scene.Instances.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(4, scene.Materials.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(5, scene.Lights.GPUVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(6, _clusters.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(10, scene.EnvironmentTexels.GPUVirtualAddress);
            cmd.SetComputeRootShaderResourceView(11, _filteredEnvironment!.GPUVirtualAddress);
            cmd.SetComputeRootDescriptorTable(7, _heap.GetGPUDescriptorHandleForHeapStart());
            cmd.SetComputeRootDescriptorTable(8, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
            cmd.SetComputeRootDescriptorTable(9, _heap.GetGPUDescriptorHandleForHeapStart() + (Textures + extraTable * ExtraCount) * _stride);
        }
        else
        {
            cmd.SetGraphicsRootSignature(_root);
            cmd.SetGraphicsRootConstantBufferView(0, _constants.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(2, scene.Vertices.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(3, scene.Instances.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(4, scene.Materials.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(5, scene.Lights.GPUVirtualAddress);
            cmd.SetGraphicsRootUnorderedAccessView(6, _clusters.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(10, scene.EnvironmentTexels.GPUVirtualAddress);
            cmd.SetGraphicsRootShaderResourceView(11, _filteredEnvironment!.GPUVirtualAddress);
            cmd.SetGraphicsRootDescriptorTable(7, _heap.GetGPUDescriptorHandleForHeapStart());
            cmd.SetGraphicsRootDescriptorTable(8, scene.Samplers.GetGPUDescriptorHandleForHeapStart());
            cmd.SetGraphicsRootDescriptorTable(9, _heap.GetGPUDescriptorHandleForHeapStart() + (Textures + extraTable * ExtraCount) * _stride);
            cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cmd.IASetIndexBuffer(new IndexBufferView(scene.Indices.GPUVirtualAddress, (uint)scene.Indices.Description.Width, Format.R32_UInt));
        }
    }
    private void Draw(int[] instances, int shadowIndex = 0, int count = -1)
    {
        var cmd = _gpu.CommandList;
        var scene = _lease!.Scene;
        cmd.SetGraphicsRoot32BitConstant(1, (uint)shadowIndex, 1);
        for (int draw = 0; draw < (count < 0 ? instances.Length : count); draw++)
        {
            int i = instances[draw];
            var instance = scene.Asset.Instances[i];
            var mesh = scene.Asset.Meshes[instance.Mesh];
            var offset = scene.MeshOffsets[instance.Mesh];
            cmd.SetGraphicsRoot32BitConstant(1, (uint)i, 0);
            cmd.DrawIndexedInstanced((uint)mesh.Indices.Length, 1, (uint)offset.Index, 0, 0);
        }
    }
    private void Transition(ID3D12Resource resource, ResourceStates before, ResourceStates after) => _gpu.CommandList.ResourceBarrierTransition(resource, before, after);
    internal LinearFrame Render(in CameraFrame camera, in CameraFrame previous, Vector2 jitter, Vector2 previousJitter,
        int width, int height, double seconds, bool reset, float environment, SunLightSettings sun, float lodBias = 0, bool screenReflections = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureSize(width, height);
        Cull(camera);
        Constants constants = BuildFrame(camera, previous, jitter, previousJitter, reset, environment, sun, lodBias);
        constants.Temporal.W = screenReflections ? 1 : 0;
        if (reset) _frameIndex = 0;
        void* pointer;
        _constants.Map(0, null, &pointer).CheckError();
        *(Constants*)pointer = constants;
        _constants.Unmap(0);
        _gpu.CommandAllocator.Reset();
        _gpu.CommandList.Reset(_gpu.CommandAllocator);
        var cmd = _gpu.CommandList;
        _timer.Begin(cmd);
        try
        {
            Bind(false, 2);
            cmd.SetPipelineState(_shadow);
            Transition(_sunShadow, ShaderRead, ResourceStates.DepthWrite);
            Transition(_localShadow, ShaderRead, ResourceStates.DepthWrite);
            for (int i = 0; i < 16; i++)
            {
                if ((_shadowViewMask & (1u << i)) == 0)
                    continue;
                int edge = i < 4 ? ShadowSize : LocalSize;
                cmd.RSSetViewport(0, 0, edge, edge);
                cmd.RSSetScissorRect(new Vortice.RawRect(0, 0, edge, edge));
                cmd.ClearDepthStencilView(Dsv(i), ClearFlags.Depth, 0, 0);
                cmd.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, Dsv(i));
                Draw(_opaque, i);
            }
            Transition(_sunShadow, ResourceStates.DepthWrite, ShaderRead);
            Transition(_localShadow, ResourceStates.DepthWrite, ShaderRead);
            cmd.RSSetViewport(0, 0, width, height);
            cmd.RSSetScissorRect(new Vortice.RawRect(0, 0, width, height));
            Transition(_depth, ResourceStates.DepthRead | ShaderRead, ResourceStates.DepthWrite);
            cmd.ClearDepthStencilView(Dsv(16), ClearFlags.Depth, 0, 0);
            cmd.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, Dsv(16));
            cmd.SetPipelineState(_depthPass);
            Draw(_visibleOpaque, count: _visibleOpaqueCount);
            Transition(_depth, ResourceStates.DepthWrite, ResourceStates.DepthRead | ShaderRead);
            Transition(_base, ResourceStates.NonPixelShaderResource, ResourceStates.RenderTarget);
            Transition(_normal, ResourceStates.NonPixelShaderResource, ResourceStates.RenderTarget);
            Transition(_emission, ResourceStates.NonPixelShaderResource, ResourceStates.RenderTarget);
            cmd.ClearRenderTargetView(Rtv(0), new Vortice.Mathematics.Color4(0));
            cmd.ClearRenderTargetView(Rtv(1), new Vortice.Mathematics.Color4(0));
            cmd.ClearRenderTargetView(Rtv(2), new Vortice.Mathematics.Color4(0));
            Span<CpuDescriptorHandle> buffers = stackalloc CpuDescriptorHandle[3] { Rtv(0), Rtv(1), Rtv(2) };
            cmd.OMSetRenderTargets(buffers, Dsv(17));
            cmd.SetPipelineState(_gbuffer);
            Draw(_visibleOpaque, count: _visibleOpaqueCount);
            Transition(_base, ResourceStates.RenderTarget, ShaderRead);
            Transition(_normal, ResourceStates.RenderTarget, ShaderRead);
            Transition(_emission, ResourceStates.RenderTarget, ShaderRead);
            Bind(true, 0);
            cmd.SetPipelineState(_clustersPass);
            cmd.Dispatch((uint)((width + 15) / 16 + 7) / 8, (uint)((height + 15) / 16 + 7) / 8, 24);
            cmd.ResourceBarrierUnorderedAccessView(_clusters);
            Transition(_color, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_motion, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            Transition(_reactive, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cmd.SetPipelineState(_lighting);
            cmd.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
            Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            Transition(_opaqueColor, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
            cmd.CopyResource(_opaqueColor, _color);
            Transition(_opaqueColor, ResourceStates.CopyDest, ShaderRead);
            // SSR 只读当前帧直接光/漫反射快照，禁止把最终镜面反馈到自身。
            Transition(_color, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
            if (_hardware != null)
            {
                _hardware.Record(cmd, _lease!, camera, jitter, _frameIndex, environment, sun);
                // 结束私有 resolve 的静态 SRV 绑定，再更新它刚读取的 OpaqueColor。
                cmd.SetComputeRootSignature(_root);
            }
            else
            {
                Bind(true, 0);
                cmd.SetPipelineState(_reflections);
                cmd.Dispatch((uint)(width + 7) / 8, (uint)(height + 7) / 8, 1);
            }
            Transition(_color, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            Transition(_opaqueColor, ShaderRead, ResourceStates.CopyDest);
            cmd.CopyResource(_opaqueColor, _color);
            Transition(_opaqueColor, ResourceStates.CopyDest, ShaderRead);
            Transition(_color, ResourceStates.CopySource, ResourceStates.RenderTarget);
            Transition(_motion, ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
            Transition(_reactive, ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
            _order.Centers = _centers;
            _order.Camera = camera;
            Array.Sort(_visibleTransparent, 0, _visibleTransparentCount, _order);
            Bind(false, 1);
            cmd.SetPipelineState(_transparentPass);
            buffers[0] = Rtv(3);
            cmd.OMSetRenderTargets(buffers[..1], Dsv(17));
            Draw(_visibleTransparent, count: _visibleTransparentCount);
            if (_visibleTransparentCount > 0)
            {
                // 颜色按后到前合成；重建信号用独立深度测试选择最近透明面，
                // 避免拿玻璃后的 opaque 深度去重投影玻璃 motion。
                Transition(_depth, ResourceStates.DepthRead | ShaderRead, ResourceStates.CopySource);
                Transition(_guideDepth, ResourceStates.DepthRead | ShaderRead, ResourceStates.CopyDest);
                cmd.CopyResource(_guideDepth, _depth);
                Transition(_depth, ResourceStates.CopySource, ResourceStates.DepthRead | ShaderRead);
                Transition(_guideDepth, ResourceStates.CopyDest, ResourceStates.DepthWrite);
                Bind(false, 2);
                cmd.SetPipelineState(_transparentGuides);
                buffers[0] = Rtv(4);
                buffers[1] = Rtv(5);
                cmd.OMSetRenderTargets(buffers[..2], Dsv(18));
                Draw(_visibleTransparent, count: _visibleTransparentCount);
                Transition(_guideDepth, ResourceStates.DepthWrite, ResourceStates.DepthRead | ShaderRead);
            }
            Transition(_color, ResourceStates.RenderTarget, ResourceStates.NonPixelShaderResource);
            Transition(_motion, ResourceStates.RenderTarget, ResourceStates.NonPixelShaderResource);
            Transition(_reactive, ResourceStates.RenderTarget, ResourceStates.NonPixelShaderResource);
            Transition(_opaqueColor, ShaderRead, ResourceStates.NonPixelShaderResource);
            Transition(_base, ShaderRead, ResourceStates.NonPixelShaderResource);
            Transition(_normal, ShaderRead, ResourceStates.NonPixelShaderResource);
            Transition(_emission, ShaderRead, ResourceStates.NonPixelShaderResource);
            _timer.End(cmd);
            cmd.Close();
        }
        catch { cmd.Close(); throw; }
        _gpu.CommandQueue.ExecuteCommandList(cmd);
        _gpu.SignalAndWait();
        GpuMilliseconds = _timer.ReadMilliseconds();
        _frameIndex++;
        _gpu.Device.DeviceRemovedReason.CheckError();
        return new(_color, _visibleTransparentCount > 0 ? _guideDepth : _depth, _motion, _reactive, width, height, camera, jitter, previousJitter, seconds, reset, true, _normal);
    }
    private void Cull(in CameraFrame camera)
    {
        float tanY = MathF.Tan(camera.VerticalFov * .5f), tanX = tanY * _width / _height;
        float sideX = MathF.Sqrt(1 + tanX * tanX), sideY = MathF.Sqrt(1 + tanY * tanY);
        _visibleOpaqueCount = _visibleTransparentCount = 0;
        foreach (int index in _opaque)
            if (Visible(index, camera, tanX, tanY, sideX, sideY))
                _visibleOpaque[_visibleOpaqueCount++] = index;
        foreach (int index in _transparent)
            if (Visible(index, camera, tanX, tanY, sideX, sideY))
                _visibleTransparent[_visibleTransparentCount++] = index;
    }
    private bool Visible(int index, in CameraFrame camera, float tanX, float tanY, float sideX, float sideY)
    {
        Vector3 delta = _centers[index] - camera.Origin;
        float z = Vector3.Dot(delta, camera.Forward);
        // 一像素余量覆盖抖动；阴影继续提交全部 caster，不能套用主视图裁剪结果。
        float radius = _radii[index] + MathF.Max(z, camera.Near) * 2 * tanY / _height;
        return z + radius >= camera.Near && z - radius <= camera.Far &&
            MathF.Abs(Vector3.Dot(delta, camera.Right)) - z * tanX <= radius * sideX &&
            MathF.Abs(Vector3.Dot(delta, camera.Up)) - z * tanY <= radius * sideY;
    }
    private sealed class TransparencyOrder : IComparer<int>
    {
        internal Vector3[] Centers = []; internal CameraFrame Camera;
        public int Compare(int a, int b)
        {
            int order = Vector3.Dot(Centers[b] - Camera.Origin, Camera.Forward).CompareTo(Vector3.Dot(Centers[a] - Camera.Origin, Camera.Forward));
            return order != 0 ? order : a.CompareTo(b);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var cleanup = new ResourceCleanup();
        if (_initialized) cleanup.Run(() => _gpu.SignalAndWait());
        cleanup.Release(_lease); _lease = null;
        cleanup.Release(_filteredEnvironment); _filteredEnvironment = null;
        if (_hardware != null) cleanup.Run(_hardware.ReleaseSize);
        cleanup.Release(_sized); cleanup.Release(_owned); cleanup.Release(_gpu);
        cleanup.Finish();
    }
}
